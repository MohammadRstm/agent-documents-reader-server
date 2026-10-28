using System.Globalization;

namespace DocumentReader.Api.Ingestion.Excel;

public enum CellValueKind
{
    Text,
    Number,
    DateTime,
    Boolean,
    Error,
}

/// <summary>
/// One non-blank cell, as read by either Excel reader. For formula cells the value is the
/// value cached in the file; formulas are never recalculated.
/// </summary>
public sealed record GridCell(
    int Row,
    int Column,
    CellValueKind Kind,
    string DisplayText,
    decimal? Number,
    DateTime? Date,
    string? Formula,
    string? NumberFormat,
    bool IsBold)
{
    public string Address => CellAddress.Format(Row, Column);

    public bool IsNumeric => Kind == CellValueKind.Number && Number.HasValue;
}

/// <summary>A merged cell range, 1-based and inclusive.</summary>
public sealed record MergedRange(int FirstRow, int FirstColumn, int LastRow, int LastColumn)
{
    public bool Contains(int row, int column) =>
        row >= FirstRow && row <= LastRow && column >= FirstColumn && column <= LastColumn;

    public bool IsHorizontal => LastColumn > FirstColumn;
}

/// <summary>
/// The cells of one worksheet in a reader-independent form. Both the .xlsx/.xlsm and the .xls
/// reader fill this model, so table detection works the same for every Excel format.
/// </summary>
public sealed class SheetGrid(string name, bool isHidden, bool stylesAvailable)
{
    private readonly SortedDictionary<int, SortedDictionary<int, GridCell>> _rows = [];
    private readonly List<MergedRange> _mergedRanges = [];

    public string Name { get; } = name;

    public bool IsHidden { get; } = isHidden;

    /// <summary>False for .xls files, where formatting such as bold text is not read.</summary>
    public bool StylesAvailable { get; } = stylesAvailable;

    /// <summary>Formula cells whose file has no saved result; they are treated as blank.</summary>
    public int FormulasWithoutCachedValue { get; set; }

    public int CellCount { get; private set; }

    public IEnumerable<int> RowNumbers => _rows.Keys;

    public int NonEmptyRowCount => _rows.Count;

    public IReadOnlyList<MergedRange> MergedRanges => _mergedRanges;

    public void AddCell(GridCell cell)
    {
        if (!_rows.TryGetValue(cell.Row, out var row))
        {
            row = [];
            _rows[cell.Row] = row;
        }

        if (row.TryAdd(cell.Column, cell))
        {
            CellCount++;
        }
    }

    public void AddMergedRange(MergedRange range) => _mergedRanges.Add(range);

    /// <summary>Cells of a row ordered by column; empty if the row has no cells.</summary>
    public IReadOnlyCollection<GridCell> GetRow(int row) =>
        _rows.TryGetValue(row, out var cells) ? cells.Values : [];

    public GridCell? GetCell(int row, int column) =>
        _rows.TryGetValue(row, out var cells) && cells.TryGetValue(column, out var cell) ? cell : null;
}

/// <summary>A whole workbook read into grids, plus problems found while reading.</summary>
public sealed record WorkbookGrid(IReadOnlyList<SheetGrid> Sheets, IReadOnlyList<string> Warnings, bool IsTruncated);

/// <summary>A1-style cell addresses.</summary>
public static class CellAddress
{
    public static string ColumnLetter(int column)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);
        var letters = string.Empty;
        while (column > 0)
        {
            var remainder = (column - 1) % 26;
            letters = (char)('A' + remainder) + letters;
            column = (column - 1) / 26;
        }

        return letters;
    }

    public static string Format(int row, int column) =>
        ColumnLetter(column) + row.ToString(CultureInfo.InvariantCulture);

    public static string Range(int firstRow, int firstColumn, int lastRow, int lastColumn) =>
        $"{Format(firstRow, firstColumn)}:{Format(lastRow, lastColumn)}";
}

/// <summary>Builds the display text used in evidence for each kind of value.</summary>
public static class CellText
{
    public static string FromNumber(decimal number) => number.ToString("G", CultureInfo.InvariantCulture);

    public static string FromDate(DateTime date) =>
        date.TimeOfDay == TimeSpan.Zero
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string FromBoolean(bool value) => value ? "TRUE" : "FALSE";

    /// <summary>
    /// Converts an Excel number to decimal. Excel stores doubles; converting through decimal keeps
    /// at most 15 significant digits, which avoids binary artifacts such as 0.30000000000000004.
    /// Returns null for values outside the decimal range.
    /// </summary>
    public static decimal? ToDecimal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 7.9e27)
        {
            return null;
        }

        return (decimal)value;
    }

    /// <summary>Removes control characters and bounds the length of a cell's text.</summary>
    public static string Clean(string text)
    {
        var cleaned = new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        return cleaned.Length > ExcelLimits.MaxCellTextLength ? cleaned[..ExcelLimits.MaxCellTextLength] : cleaned;
    }
}
