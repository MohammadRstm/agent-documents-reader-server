using DocumentReader.Api.Ingestion.Excel;

namespace DocumentReader.Api.Analysis;

/// <summary>Rows that matched a query. <see cref="TotalMatches"/> counts every match, not only the returned rows.</summary>
public sealed record TableQueryResult(int TotalMatches, IReadOnlyList<TableRow> Rows)
{
    public bool IsTruncated => Rows.Count < TotalMatches;
}

/// <summary>
/// Filters and sorts all rows of a table deterministically. Every row is examined, so the match
/// count is exact even when only the first rows are returned.
/// </summary>
public static class TableQuery
{
    public static IEnumerable<TableRow> Filter(
        DetectedTable table,
        IReadOnlyList<RowFilter> filters,
        IReadOnlySet<RowKind>? rowKinds) =>
        table.Rows.Where(row =>
            (rowKinds is null || rowKinds.Contains(row.Kind)) && filters.All(filter => filter.Matches(row)));

    public static TableQueryResult Run(
        DetectedTable table,
        IReadOnlyList<RowFilter> filters,
        IReadOnlySet<RowKind>? rowKinds,
        TableColumn? sortBy,
        bool sortDescending,
        int limit)
    {
        var matches = Filter(table, filters, rowKinds).ToList();
        if (sortBy is not null)
        {
            matches = Sort(matches, sortBy, sortDescending);
        }

        return new TableQueryResult(matches.Count, matches.Take(limit).ToList());
    }

    /// <summary>
    /// Stable sort: numbers, then dates, then text; rows without a value always come last.
    /// </summary>
    private static List<TableRow> Sort(List<TableRow> rows, TableColumn column, bool descending)
    {
        var withValue = rows.Where(row => row.Cells.ContainsKey(column.ColumnNumber)).ToList();
        var withoutValue = rows.Where(row => !row.Cells.ContainsKey(column.ColumnNumber));

        var ordered = descending
            ? withValue.OrderByDescending(row => row.Cells[column.ColumnNumber], CellComparer.Instance)
            : withValue.OrderBy(row => row.Cells[column.ColumnNumber], CellComparer.Instance);
        return [.. ordered, .. withoutValue];
    }

    private sealed class CellComparer : IComparer<GridCell>
    {
        public static readonly CellComparer Instance = new();

        public int Compare(GridCell? x, GridCell? y)
        {
            if (x is null || y is null)
            {
                return (x is null).CompareTo(y is null);
            }

            var rankComparison = Rank(x).CompareTo(Rank(y));
            if (rankComparison != 0)
            {
                return rankComparison;
            }

            return Rank(x) switch
            {
                0 => x.Number!.Value.CompareTo(y.Number!.Value),
                1 => x.Date!.Value.CompareTo(y.Date!.Value),
                _ => string.Compare(x.DisplayText, y.DisplayText, StringComparison.OrdinalIgnoreCase),
            };
        }

        private static int Rank(GridCell cell) =>
            cell.IsNumeric ? 0 : cell.Kind == CellValueKind.DateTime && cell.Date.HasValue ? 1 : 2;
    }
}
