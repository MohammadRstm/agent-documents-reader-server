using System.Globalization;
using System.Text;
using DocumentReader.Api.Configuration;
using ExcelDataReader;
using Microsoft.Extensions.Options;

namespace DocumentReader.Api.Ingestion.Excel;

/// <summary>
/// Reads legacy .xls (BIFF) workbooks with ExcelDataReader into <see cref="SheetGrid"/>s.
/// ExcelDataReader returns the values saved in the file (formulas are not recalculated) but not
/// fonts or formula text, so bold hints are unavailable for .xls.
/// </summary>
public sealed class XlsGridReader(IOptions<LimitsOptions> limitsOptions)
{
    static XlsGridReader()
    {
        // .xls files may use legacy code pages that .NET does not load by default.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public WorkbookGrid Read(byte[] content, CancellationToken cancellationToken)
    {
        var builder = new GridBuilder(limitsOptions.Value, cancellationToken);
        using var stream = new MemoryStream(content, writable: false);
        using var reader = ExcelReaderFactory.CreateBinaryReader(stream);

        do
        {
            var sheet = builder.StartSheet(
                reader.Name ?? "Sheet",
                isHidden: !string.Equals(reader.VisibleState, "visible", StringComparison.OrdinalIgnoreCase),
                stylesAvailable: false);
            if (sheet is null)
            {
                break;
            }

            ReadRows(reader, sheet, builder);
            foreach (var merged in reader.MergeCells ?? [])
            {
                // ExcelDataReader uses 0-based indexes.
                sheet.AddMergedRange(new MergedRange(merged.FromRow + 1, merged.FromColumn + 1, merged.ToRow + 1, merged.ToColumn + 1));
            }

            if (!builder.CanContinue)
            {
                break;
            }
        }
        while (reader.NextResult());

        return builder.Build();
    }

    private static void ReadRows(IExcelDataReader reader, SheetGrid sheet, GridBuilder builder)
    {
        var row = 0;
        while (reader.Read())
        {
            row++;
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var cell = ToGridCell(reader, row, index + 1, index, builder);
                if (cell is not null && !builder.TryAddCell(sheet, cell))
                {
                    return;
                }
            }
        }
    }

    private static GridCell? ToGridCell(IExcelDataReader reader, int row, int column, int index, GridBuilder builder)
    {
        var value = reader.GetValue(index);
        if (value is null)
        {
            return null;
        }

        var numberFormat = reader.GetNumberFormatString(index);

        GridCell Create(CellValueKind kind, string text, decimal? number = null, DateTime? date = null) =>
            new(row, column, kind, text, number, date, Formula: null, numberFormat, IsBold: false);

        switch (value)
        {
            case string text:
                var cleaned = builder.CleanText(text);
                return cleaned.Length == 0 ? null : Create(CellValueKind.Text, cleaned);

            case double or float or int or long or decimal:
                var raw = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                var number = CellText.ToDecimal(raw);
                return number is null
                    ? Create(CellValueKind.Text, raw.ToString("R", CultureInfo.InvariantCulture))
                    : Create(CellValueKind.Number, CellText.FromNumber(number.Value), number);

            case DateTime date:
                return Create(CellValueKind.DateTime, CellText.FromDate(date), date: date);

            case bool flag:
                return Create(CellValueKind.Boolean, CellText.FromBoolean(flag));

            case TimeSpan time:
                return Create(CellValueKind.Text, time.ToString("c", CultureInfo.InvariantCulture));

            default:
                var other = builder.CleanText(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                return other.Length == 0 ? null : Create(CellValueKind.Text, other);
        }
    }
}
