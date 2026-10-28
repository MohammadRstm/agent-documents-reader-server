using ClosedXML.Excel;

namespace DocumentReader.Api.Ingestion.Excel;

/// <summary>
/// Reads .xlsx/.xlsm workbooks with ClosedXML into <see cref="SheetGrid"/>s. Formula cells use the
/// value cached in the file (<see cref="IXLCell.CachedValue"/>); formulas are never recalculated and
/// macros are never run. Bold text and number formats are kept as hints for table detection.
/// </summary>
public sealed class XlsxGridReader
{
    public WorkbookGrid Read(byte[] content, CancellationToken cancellationToken)
    {
        var builder = new GridBuilder(cancellationToken);
        using var stream = new MemoryStream(content, writable: false);
        using var workbook = new XLWorkbook(stream, new LoadOptions { RecalculateAllFormulas = false });

        foreach (var worksheet in workbook.Worksheets)
        {
            var sheet = builder.StartSheet(
                worksheet.Name,
                isHidden: worksheet.Visibility != XLWorksheetVisibility.Visible,
                stylesAvailable: true);
            if (sheet is null)
            {
                break;
            }

            ReadCells(worksheet, sheet, builder);
            foreach (var merged in worksheet.MergedRanges)
            {
                var first = merged.RangeAddress.FirstAddress;
                var last = merged.RangeAddress.LastAddress;
                sheet.AddMergedRange(new MergedRange(first.RowNumber, first.ColumnNumber, last.RowNumber, last.ColumnNumber));
            }

            if (!builder.CanContinue)
            {
                break;
            }
        }

        return builder.Build();
    }

    private static void ReadCells(IXLWorksheet worksheet, SheetGrid sheet, GridBuilder builder)
    {
        foreach (var cell in worksheet.CellsUsed(XLCellsUsedOptions.Contents))
        {
            var gridCell = ToGridCell(cell, sheet);
            if (gridCell is not null && !builder.TryAddCell(sheet, gridCell))
            {
                return;
            }
        }
    }

    private static GridCell? ToGridCell(IXLCell cell, SheetGrid sheet)
    {
        var formula = cell.HasFormula ? cell.FormulaA1 : null;
        var value = cell.CachedValue;
        var row = cell.Address.RowNumber;
        var column = cell.Address.ColumnNumber;
        var style = cell.Style;
        var numberFormat = string.IsNullOrWhiteSpace(style.NumberFormat.Format) ? null : style.NumberFormat.Format;
        var isBold = style.Font.Bold;

        GridCell Create(CellValueKind kind, string text, decimal? number = null, DateTime? date = null) =>
            new(row, column, kind, text, number, date, formula, numberFormat, isBold);

        switch (value.Type)
        {
            case XLDataType.Blank:
                if (formula is not null)
                {
                    sheet.FormulasWithoutCachedValue++;
                }

                return null;

            case XLDataType.Text:
                var text = CellText.Clean(value.GetText());
                return text.Length == 0 ? null : Create(CellValueKind.Text, text);

            case XLDataType.Number:
                var number = CellText.ToDecimal(value.GetNumber());
                return number is null
                    ? Create(CellValueKind.Text, value.GetNumber().ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                    : Create(CellValueKind.Number, CellText.FromNumber(number.Value), number);

            case XLDataType.DateTime:
                var date = value.GetDateTime();
                return Create(CellValueKind.DateTime, CellText.FromDate(date), date: date);

            case XLDataType.Boolean:
                return Create(CellValueKind.Boolean, CellText.FromBoolean(value.GetBoolean()));

            case XLDataType.TimeSpan:
                return Create(CellValueKind.Text, value.GetTimeSpan().ToString("c", System.Globalization.CultureInfo.InvariantCulture));

            case XLDataType.Error:
                return Create(CellValueKind.Error, value.ToString());

            default:
                return Create(CellValueKind.Text, CellText.Clean(value.ToString()));
        }
    }
}
