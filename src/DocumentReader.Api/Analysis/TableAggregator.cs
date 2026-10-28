using DocumentReader.Api.Ingestion.Excel;

namespace DocumentReader.Api.Analysis;

public enum AggregateOperation
{
    Sum,
    Count,
    Average,
    Min,
    Max,
}

/// <summary>The value of one aggregation (whole table or one group) and the rows behind it.</summary>
public sealed record AggregateValue(
    decimal? Result,
    int ValuesUsed,
    IReadOnlyList<int> ContributingRows,
    IReadOnlyList<int> ResultRows);

public sealed record AggregateGroup(string Key, AggregateValue Value);

/// <summary>Outcome of an aggregation. When <see cref="Error"/> is set nothing was calculated.</summary>
public sealed record AggregateResult(
    string? Error,
    string? Currency,
    int RowsMatched,
    int ValuesSkipped,
    AggregateValue? Total,
    IReadOnlyList<AggregateGroup> Groups,
    IReadOnlyList<string> Notes)
{
    public static AggregateResult Failed(string error) => new(error, null, 0, 0, null, [], []);
}

/// <summary>
/// Deterministic aggregation over table rows with decimal arithmetic. Callers choose which row
/// kinds take part (tools default to items only, so subtotals and totals are not double-counted).
/// Values in different currencies are never combined.
/// </summary>
public static class TableAggregator
{
    public const int MaxGroups = 100;

    public static AggregateResult Aggregate(
        DetectedTable table,
        TableColumn column,
        AggregateOperation operation,
        IReadOnlyList<RowFilter> filters,
        IReadOnlySet<RowKind> rowKinds,
        TableColumn? groupBy)
    {
        var rows = TableQuery.Filter(table, filters, rowKinds).ToList();
        var notes = new List<string>();

        // Count works on any non-empty value; the other operations need numbers.
        var usable = rows
            .Where(row => row.Cells.TryGetValue(column.ColumnNumber, out var cell)
                && (operation == AggregateOperation.Count || cell.IsNumeric))
            .ToList();
        var skipped = rows.Count - usable.Count;
        if (skipped > 0)
        {
            notes.Add(operation == AggregateOperation.Count
                ? $"{skipped} matching row(s) have no value in \"{column.Name}\" and were not counted."
                : $"{skipped} matching row(s) have no numeric value in \"{column.Name}\" and were skipped.");
        }

        string? currency = null;
        if (operation != AggregateOperation.Count)
        {
            var currencyCheck = ResolveCurrency(column, usable.Select(row => row.Cells[column.ColumnNumber]), notes);
            if (currencyCheck.Error is not null)
            {
                return AggregateResult.Failed(currencyCheck.Error);
            }

            currency = currencyCheck.Currency;
        }

        try
        {
            var total = Calculate(usable, column, operation);
            var groups = new List<AggregateGroup>();
            if (groupBy is not null)
            {
                var grouped = usable
                    .GroupBy(row => row.Cells.TryGetValue(groupBy.ColumnNumber, out var key) ? key.DisplayText : "(empty)", StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (grouped.Count > MaxGroups)
                {
                    return AggregateResult.Failed($"Grouping by \"{groupBy.Name}\" gives {grouped.Count} groups; at most {MaxGroups} are allowed. Add filters or choose another column.");
                }

                groups.AddRange(grouped.Select(group => new AggregateGroup(group.Key, Calculate(group.ToList(), column, operation))));
            }

            return new AggregateResult(null, currency, rows.Count, skipped, total, groups, notes);
        }
        catch (OverflowException)
        {
            return AggregateResult.Failed("The result is too large to calculate exactly.");
        }
    }

    private static AggregateValue Calculate(List<TableRow> rows, TableColumn column, AggregateOperation operation)
    {
        var contributing = rows.Select(row => row.RowNumber).ToList();
        if (operation == AggregateOperation.Count)
        {
            return new AggregateValue(rows.Count, rows.Count, contributing, []);
        }

        if (rows.Count == 0)
        {
            return new AggregateValue(null, 0, [], []);
        }

        var values = rows.Select(row => (row.RowNumber, Value: row.Cells[column.ColumnNumber].Number!.Value)).ToList();
        switch (operation)
        {
            case AggregateOperation.Sum:
                return new AggregateValue(Sum(values.Select(value => value.Value)), values.Count, contributing, []);

            case AggregateOperation.Average:
                var average = Sum(values.Select(value => value.Value)) / values.Count;
                return new AggregateValue(decimal.Round(average, 10), values.Count, contributing, []);

            case AggregateOperation.Min:
            case AggregateOperation.Max:
                var extreme = operation == AggregateOperation.Min ? values.Min(value => value.Value) : values.Max(value => value.Value);
                var extremeRows = values.Where(value => value.Value == extreme).Select(value => value.RowNumber).ToList();
                return new AggregateValue(extreme, values.Count, contributing, extremeRows);

            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private static decimal Sum(IEnumerable<decimal> values)
    {
        var total = 0m;
        foreach (var value in values)
        {
            total += value; // decimal addition throws OverflowException instead of losing precision
        }

        return total;
    }

    /// <summary>
    /// Uses the currency stated by the cells' number formats, falling back to the column header.
    /// Refuses to combine values formatted in different currencies or when header and formats disagree.
    /// </summary>
    private static (string? Currency, string? Error) ResolveCurrency(TableColumn column, IEnumerable<GridCell> cells, List<string> notes)
    {
        var cellList = cells.ToList();
        var formatCurrencies = cellList
            .Select(cell => CurrencyDetector.FromNumberFormat(cell.NumberFormat))
            .ToList();
        var distinct = formatCurrencies.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var headerCurrency = CurrencyDetector.FromText(column.Name);

        if (distinct.Count > 1)
        {
            return (null, $"Values in \"{column.Name}\" are in different currencies ({string.Join(", ", distinct)}). They cannot be combined without an exchange rate.");
        }

        if (distinct.Count == 1)
        {
            if (headerCurrency is not null && headerCurrency != distinct[0])
            {
                return (null, $"The header of \"{column.Name}\" says {headerCurrency} but the values are formatted as {distinct[0]}; the currency is ambiguous.");
            }

            if (formatCurrencies.Any(currency => currency is null))
            {
                notes.Add($"Some values have no currency format; they were assumed to be in {distinct[0]} like the others in this column.");
            }

            return (distinct[0], null);
        }

        if (headerCurrency is null && cellList.Count > 0)
        {
            notes.Add("No currency is stated for these values.");
        }

        return (headerCurrency, null);
    }
}
