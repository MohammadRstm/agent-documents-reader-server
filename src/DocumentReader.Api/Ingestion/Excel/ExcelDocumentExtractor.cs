using System.Text;
using ExcelDataReader.Exceptions;

namespace DocumentReader.Api.Ingestion.Excel;

/// <summary>Result of reading an Excel workbook: tables for structured analysis and chunks for evidence.</summary>
public sealed record ExcelExtractionResult(
    DocumentStatus Status,
    IReadOnlyList<SheetSummary> Sheets,
    IReadOnlyList<DetectedTable> Tables,
    IReadOnlyList<ExtractedChunk> Chunks,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads .xlsx/.xlsm (ClosedXML) and .xls (ExcelDataReader) workbooks into one grid model, detects
/// tables on every sheet, and turns each table row and each block of non-table rows into evidence
/// chunks that keep the sheet name and cell range.
/// </summary>
public sealed class ExcelDocumentExtractor(
    XlsxGridReader xlsxReader,
    XlsGridReader xlsReader,
    ILogger<ExcelDocumentExtractor> logger)
{
    /// <summary>Evidence text limit from docs/api-contract.md.</summary>
    public const int MaxEvidenceTextLength = 4000;

    private const int MaxWarnings = 50;

    public ExcelExtractionResult Extract(byte[] content, FileType fileType, CancellationToken cancellationToken)
    {
        if (fileType is not (FileType.Xlsx or FileType.Xlsm or FileType.Xls))
        {
            throw new ArgumentOutOfRangeException(nameof(fileType), fileType, "Not an Excel file type.");
        }

        var warnings = new List<string>();
        if (fileType is FileType.Xlsx or FileType.Xlsm)
        {
            switch (OpenXmlPackageInspector.CheckSafety(content))
            {
                case PackageSafety.TooLarge:
                    return Failed(
                        DocumentStatus.TooLarge,
                        $"The workbook expands to too much data when unpacked (limit {SizeFormatter.Format(ExcelLimits.MaxUncompressedPackageBytes)}) and was not processed.");
                case PackageSafety.Unreadable:
                    return Failed(DocumentStatus.Corrupt, "The workbook could not be read. It may be damaged.");
            }

            if (OpenXmlPackageInspector.ContainsMacros(content))
            {
                warnings.Add("The workbook contains macros. They were ignored and never run.");
            }
        }

        WorkbookGrid grid;
        try
        {
            grid = fileType == FileType.Xls
                ? xlsReader.Read(content, cancellationToken)
                : xlsxReader.Read(content, cancellationToken);
        }
        catch (InvalidPasswordException)
        {
            return Failed(DocumentStatus.Corrupt, "The workbook is password-protected and cannot be read.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogInformation(exception, "Workbook could not be read.");
            return Failed(DocumentStatus.Corrupt, "The workbook could not be read. It may be damaged.");
        }

        return BuildResult(grid, fileType, warnings);
    }

    private static ExcelExtractionResult BuildResult(WorkbookGrid grid, FileType fileType, List<string> warnings)
    {
        warnings.AddRange(grid.Warnings);
        if (fileType == FileType.Xls)
        {
            warnings.Add("Formatting such as bold text and formulas is not available for .xls files; tables were detected from values only.");
        }

        var tables = new List<DetectedTable>();
        var chunks = new List<ExtractedChunk>();
        var sheets = new List<SheetSummary>();

        foreach (var sheet in grid.Sheets)
        {
            if (sheet.IsHidden && sheet.CellCount > 0)
            {
                warnings.Add($"Sheet \"{sheet.Name}\" is hidden in the workbook; its content was included.");
            }

            if (sheet.FormulasWithoutCachedValue > 0)
            {
                warnings.Add($"Sheet \"{sheet.Name}\": {sheet.FormulasWithoutCachedValue} formula cell(s) have no saved result and were treated as empty (formulas are not recalculated).");
            }

            var layout = TableDetector.Detect(sheet);
            warnings.AddRange(layout.Warnings);
            foreach (var table in layout.Tables)
            {
                tables.Add(table);
                chunks.AddRange(TableRowChunks(table, tableNumber: tables.Count));
            }

            chunks.AddRange(TextChunks(sheet, layout.RowsOutsideTables));
            sheets.Add(new SheetSummary(sheet.Name, layout.Tables.Count, sheet.NonEmptyRowCount));
        }

        var hasData = grid.Sheets.Any(sheet => sheet.CellCount > 0);
        DocumentStatus status;
        if (hasData)
        {
            status = grid.IsTruncated ? DocumentStatus.PartiallyIndexed : DocumentStatus.Indexed;
        }
        else if (grid.IsTruncated)
        {
            // Reading stopped (time or limits) before any data was read: nothing usable was extracted.
            status = DocumentStatus.Corrupt;
        }
        else
        {
            status = DocumentStatus.Empty;
            warnings.Insert(0, "The workbook contains no data.");
        }

        return new ExcelExtractionResult(status, sheets, tables, chunks, LimitWarnings(warnings));
    }

    /// <summary>One chunk per table row, labelled with its column headers so it reads on its own.</summary>
    private static IEnumerable<ExtractedChunk> TableRowChunks(DetectedTable table, int tableNumber)
    {
        foreach (var row in table.Rows)
        {
            var text = new StringBuilder();
            text.Append($"Sheet \"{table.SheetName}\", row {row.RowNumber}");
            if (row.Kind != RowKind.Item)
            {
                text.Append($" ({DescribeKind(row.Kind)})");
            }

            text.Append(": ");
            var values = table.Columns
                .Where(column => row.Cells.ContainsKey(column.ColumnNumber))
                .Select(column => $"{column.Name}: {row.Cells[column.ColumnNumber].DisplayText}");
            text.Append(string.Join(" | ", values));

            yield return new ExtractedChunk(
                EvidenceKind.TableRow,
                new ExcelRangeLocation(table.SheetName, table.RowRange(row.RowNumber)),
                Truncate(text.ToString()),
                new TableRowReference(tableNumber, row.RowNumber));
        }
    }

    /// <summary>Groups rows outside tables (titles, notes, key-value blocks) into bounded text chunks.</summary>
    private static IEnumerable<ExtractedChunk> TextChunks(SheetGrid sheet, IReadOnlyList<int> rows)
    {
        var lines = new List<(int Row, int FirstColumn, int LastColumn, string Text)>();
        var length = 0;

        foreach (var row in rows)
        {
            var cells = sheet.GetRow(row);
            if (cells.Count == 0)
            {
                continue;
            }

            var line = Truncate($"Row {row}: {string.Join(" | ", cells.Select(cell => cell.DisplayText))}");
            if (lines.Count > 0 && length + line.Length > TextChunker.DefaultMaxChunkCharacters)
            {
                yield return CreateTextChunk(sheet.Name, lines);
                lines.Clear();
                length = 0;
            }

            lines.Add((row, cells.First().Column, cells.Last().Column, line));
            length += line.Length + 1;
        }

        if (lines.Count > 0)
        {
            yield return CreateTextChunk(sheet.Name, lines);
        }
    }

    private static ExtractedChunk CreateTextChunk(string sheetName, List<(int Row, int FirstColumn, int LastColumn, string Text)> lines)
    {
        var range = CellAddress.Range(lines[0].Row, lines.Min(line => line.FirstColumn), lines[^1].Row, lines.Max(line => line.LastColumn));
        var text = $"Sheet \"{sheetName}\":\n" + string.Join('\n', lines.Select(line => line.Text));
        return new ExtractedChunk(EvidenceKind.TextChunk, new ExcelRangeLocation(sheetName, range), Truncate(text));
    }

    private static string DescribeKind(RowKind kind) => kind switch
    {
        RowKind.SectionHeader => "section header",
        RowKind.Subtotal => "subtotal",
        RowKind.Total => "total",
        RowKind.Unknown => "unclassified row",
        _ => "item",
    };

    private static string Truncate(string text) =>
        text.Length <= MaxEvidenceTextLength ? text : text[..(MaxEvidenceTextLength - 1)] + "…";

    private static List<string> LimitWarnings(List<string> warnings)
    {
        if (warnings.Count <= MaxWarnings)
        {
            return warnings;
        }

        var limited = warnings.Take(MaxWarnings - 1).ToList();
        limited.Add($"…and {warnings.Count - limited.Count} more warnings.");
        return limited;
    }

    private static ExcelExtractionResult Failed(DocumentStatus status, string warning) =>
        new(status, [], [], [], [warning]);
}
