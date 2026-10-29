using System.Text.Json;
using DocumentReader.Api.Analysis;
using DocumentReader.Api.Ingestion.Excel;
using DocumentReader.Api.Sessions;

namespace DocumentReader.Api.Agent.Tools;

/// <summary>Argument handling and output shaping shared by the table tools.</summary>
public static class TableToolSupport
{
    public const int MaxFilters = 10;

    /// <summary>Resolves a table ID inside the session; IDs from other sessions are simply not found.</summary>
    public static StoredTable ResolveTable(Session session, ToolArguments arguments)
    {
        var tableId = arguments.RequiredString("tableId", 20);
        if (!ToolArguments.IsTableId(tableId) || !session.Documents.TryGetTable(tableId, out var table))
        {
            throw new ToolArgumentException($"Table \"{tableId}\" was not found. Use list_documents to see table IDs.");
        }

        return table;
    }

    /// <summary>Finds a column by its exact name (case-insensitive) or its column letter (e.g. "G").</summary>
    public static TableColumn ResolveColumn(DetectedTable table, string nameOrLetter)
    {
        var column = table.Columns.FirstOrDefault(candidate => candidate.Name.Equals(nameOrLetter, StringComparison.OrdinalIgnoreCase))
            ?? table.Columns.FirstOrDefault(candidate => candidate.Letter.Equals(nameOrLetter, StringComparison.OrdinalIgnoreCase));
        return column ?? throw new ToolArgumentException(
            $"Column \"{Shorten(nameOrLetter)}\" does not exist. Columns: {string.Join(", ", table.Columns.Select(candidate => $"{candidate.Name} ({candidate.Letter})"))}.");
    }

    public static IReadOnlyList<RowFilter> ParseFilters(DetectedTable table, ToolArguments arguments)
    {
        var items = arguments.OptionalObjectArray("filters", MaxFilters);
        if (items is null)
        {
            return [];
        }

        var filters = new List<RowFilter>();
        foreach (var item in items)
        {
            var filterArguments = new ToolArguments(item, "column", "operator", "value");
            var column = ResolveColumn(table, filterArguments.RequiredString("column", 200));
            var filterOperator = filterArguments.RequiredEnum<FilterOperator>("operator");
            var value = ParseFilterValue(filterArguments, filterOperator);

            if (RowFilter.IsOrderingOperator(filterOperator)
                && (column.Type is ColumnType.Text or ColumnType.Boolean || (value!.Number is null && value.Date is null)))
            {
                throw new ToolArgumentException(
                    $"Operator \"{JsonNamingPolicy.CamelCase.ConvertName(filterOperator.ToString())}\" needs a numeric or date column and a number or yyyy-MM-dd value.");
            }

            filters.Add(new RowFilter(column, filterOperator, value));
        }

        return filters;
    }

    public static IReadOnlySet<RowKind>? ParseRowKinds(ToolArguments arguments) =>
        arguments.OptionalEnumArray<RowKind>("rowKinds")?.ToHashSet();

    /// <summary>A row as the model sees it: evidence ID for citation plus values keyed by column name.</summary>
    public static object DescribeRow(StoredTable table, TableRow row, IReadOnlyList<TableColumn> columns) => new
    {
        evidenceId = table.RowEvidenceIds.GetValueOrDefault(row.RowNumber),
        rowNumber = row.RowNumber,
        kind = row.Kind,
        values = columns
            .Where(column => row.Cells.ContainsKey(column.ColumnNumber))
            .ToDictionary(column => column.Name, column => CellValue(row.Cells[column.ColumnNumber])),
    };

    public static object? CellValue(GridCell cell) => cell.IsNumeric ? cell.Number : cell.DisplayText;

    public static IEnumerable<string> EvidenceIdsFor(StoredTable table, IEnumerable<int> rowNumbers) =>
        rowNumbers.Select(rowNumber => table.RowEvidenceIds.GetValueOrDefault(rowNumber)).OfType<string>();

    private static FilterValue? ParseFilterValue(ToolArguments arguments, FilterOperator filterOperator)
    {
        var needsValue = filterOperator is not (FilterOperator.IsEmpty or FilterOperator.IsNotEmpty);
        if (!arguments.TryGet("value", out var element))
        {
            return needsValue ? throw new ToolArgumentException("\"value\" is required for this operator.") : null;
        }

        if (!needsValue)
        {
            throw new ToolArgumentException("\"value\" must be omitted for isEmpty and isNotEmpty.");
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var number) => FilterValue.FromNumber(number),
            JsonValueKind.String when element.GetString() is { Length: > 0 and <= 200 } text => FilterValue.FromText(text),
            _ => throw new ToolArgumentException("\"value\" must be a number or a string of 1 to 200 characters."),
        };
    }

    private static string Shorten(string text) => text.Length > 60 ? text[..60] : text;
}
