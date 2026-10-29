using System.Globalization;
using DocumentReader.Api.Ingestion.Excel;

namespace DocumentReader.Api.Analysis;

public enum FilterOperator
{
    Eq,
    Neq,
    Gt,
    Gte,
    Lt,
    Lte,
    Contains,
    NotContains,
    IsEmpty,
    IsNotEmpty,
}

/// <summary>A comparison value, parsed once as text, number and date where possible.</summary>
public sealed record FilterValue(string Text, decimal? Number, DateTime? Date)
{
    public static FilterValue FromNumber(decimal number) =>
        new(number.ToString(CultureInfo.InvariantCulture), number, null);

    /// <summary>Parses "1800", "1,800.50" as numbers and "2027-03-15" (optionally with a time) as dates.</summary>
    public static FilterValue FromText(string text)
    {
        var trimmed = text.Trim();
        decimal? number = decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedNumber)
            ? parsedNumber
            : null;
        DateTime? date = DateTime.TryParseExact(
                trimmed,
                ["yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate)
            ? parsedDate
            : null;
        return new FilterValue(trimmed, number, date);
    }
}

/// <summary>
/// A deterministic condition on one table column. Rows without a value in the column never match
/// a comparison (only <see cref="FilterOperator.IsEmpty"/> matches them).
/// </summary>
public sealed record RowFilter(TableColumn Column, FilterOperator Operator, FilterValue? Value)
{
    public static bool IsOrderingOperator(FilterOperator filterOperator) =>
        filterOperator is FilterOperator.Gt or FilterOperator.Gte or FilterOperator.Lt or FilterOperator.Lte;

    public bool Matches(TableRow row)
    {
        var cell = row.Cells.GetValueOrDefault(Column.ColumnNumber);
        switch (Operator)
        {
            case FilterOperator.IsEmpty:
                return cell is null;
            case FilterOperator.IsNotEmpty:
                return cell is not null;
        }

        if (cell is null || Value is null)
        {
            return false;
        }

        return Operator switch
        {
            FilterOperator.Contains => cell.DisplayText.Contains(Value.Text, StringComparison.OrdinalIgnoreCase),
            FilterOperator.NotContains => !cell.DisplayText.Contains(Value.Text, StringComparison.OrdinalIgnoreCase),
            FilterOperator.Eq => Compare(cell, Value) is 0,
            FilterOperator.Neq => Compare(cell, Value) is not 0,
            FilterOperator.Gt => Compare(cell, Value) is > 0,
            FilterOperator.Gte => Compare(cell, Value) is >= 0,
            FilterOperator.Lt => Compare(cell, Value) is < 0,
            FilterOperator.Lte => Compare(cell, Value) is <= 0,
            _ => false,
        };
    }

    /// <summary>
    /// Compares as numbers or dates when both sides are numbers or dates. Text is only compared for
    /// equality (case-insensitive); ordering text returns null, which never matches.
    /// </summary>
    private int? Compare(GridCell cell, FilterValue value)
    {
        if (cell.IsNumeric && value.Number is { } number)
        {
            return cell.Number!.Value.CompareTo(number);
        }

        if (cell.Kind == CellValueKind.DateTime && cell.Date is { } cellDate && value.Date is { } date)
        {
            // Date-only values compare by day; values with a time compare exactly.
            return date.TimeOfDay == TimeSpan.Zero ? cellDate.Date.CompareTo(date) : cellDate.CompareTo(date);
        }

        if (IsOrderingOperator(Operator))
        {
            return null;
        }

        return string.Equals(cell.DisplayText.Trim(), value.Text, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }
}
