using System.Text.Json;
using DocumentReader.Api.Analysis;
using DocumentReader.Api.Sessions;

namespace DocumentReader.Api.Agent.Tools;

/// <summary>Filters and sorts every row of a table deterministically and returns matching rows.</summary>
public sealed class QueryTableTool : IAgentTool
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private const int MaxColumns = 30;

    public string Name => "query_table";

    public string Description =>
        "Returns rows of an Excel table that match all given filters, optionally sorted. Every row is checked, so totalMatches is exact; " +
        "if truncated is true, raise the limit or narrow the filters before claiming a complete list. Each row has an evidenceId for citation " +
        "and a kind (item, sectionHeader, subtotal, total, unknown). Filter operators: eq, neq, gt, gte, lt, lte (numbers or yyyy-MM-dd dates), " +
        "contains, notContains, isEmpty, isNotEmpty. Columns are referenced by name or column letter.";

    public JsonElement InputSchema { get; } = ToolJson.ParseSchema("""
        {
          "type": "object",
          "properties": {
            "tableId": { "type": "string", "pattern": "^T[0-9]+$" },
            "filters": {
              "type": "array",
              "maxItems": 10,
              "items": {
                "type": "object",
                "properties": {
                  "column": { "type": "string" },
                  "operator": { "type": "string", "enum": ["eq", "neq", "gt", "gte", "lt", "lte", "contains", "notContains", "isEmpty", "isNotEmpty"] },
                  "value": { "type": ["string", "number"] }
                },
                "required": ["column", "operator"],
                "additionalProperties": false
              }
            },
            "rowKinds": { "type": "array", "items": { "type": "string", "enum": ["item", "sectionHeader", "subtotal", "total", "unknown"] }, "description": "Only rows of these kinds (default: all kinds)." },
            "columns": { "type": "array", "items": { "type": "string" }, "maxItems": 30, "description": "Columns to return (default: all)." },
            "sortBy": { "type": "string", "description": "Column to sort by." },
            "sortDescending": { "type": "boolean" },
            "limit": { "type": "integer", "minimum": 1, "maximum": 200, "description": "Maximum rows returned (default 50)." }
          },
          "required": ["tableId"],
          "additionalProperties": false
        }
        """);

    public ToolResult Execute(Session session, JsonElement input, CancellationToken cancellationToken)
    {
        var arguments = new ToolArguments(input, "tableId", "filters", "rowKinds", "columns", "sortBy", "sortDescending", "limit");
        var stored = TableToolSupport.ResolveTable(session, arguments);
        var table = stored.Table;
        var filters = TableToolSupport.ParseFilters(table, arguments);
        var rowKinds = TableToolSupport.ParseRowKinds(arguments);
        var columns = arguments.OptionalStringArray("columns", MaxColumns, 200)?
            .Select(name => TableToolSupport.ResolveColumn(table, name))
            .Distinct()
            .ToList() ?? [.. table.Columns];
        var sortBy = arguments.OptionalString("sortBy", 200) is { } sortName ? TableToolSupport.ResolveColumn(table, sortName) : null;
        var sortDescending = arguments.OptionalBool("sortDescending", false);
        var limit = arguments.OptionalInt("limit", 1, MaxLimit, DefaultLimit);

        var result = TableQuery.Run(table, filters, rowKinds, sortBy, sortDescending, limit);

        return ToolResult.Success(
            new
            {
                tableId = stored.TableId,
                sheet = table.SheetName,
                totalMatches = result.TotalMatches,
                returned = result.Rows.Count,
                truncated = result.IsTruncated,
                rows = result.Rows.Select(row => TableToolSupport.DescribeRow(stored, row, columns)),
            },
            TableToolSupport.EvidenceIdsFor(stored, result.Rows.Select(row => row.RowNumber)));
    }
}
