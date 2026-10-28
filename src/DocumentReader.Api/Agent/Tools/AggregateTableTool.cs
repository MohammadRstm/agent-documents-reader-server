using System.Text.Json;
using DocumentReader.Api.Analysis;
using DocumentReader.Api.Ingestion.Excel;
using DocumentReader.Api.Sessions;

namespace DocumentReader.Api.Agent.Tools;

/// <summary>
/// Calculates sum, count, average, min or max over a table column with decimal arithmetic.
/// Only item rows are included by default so subtotals and totals are not counted twice, and
/// values in different currencies are never combined.
/// </summary>
public sealed class AggregateTableTool : IAgentTool
{
    private const int MaxEvidenceIds = 100;

    public string Name => "aggregate_table";

    public string Description =>
        "Calculates sum, count, average, min or max of one column over the rows of an Excel table that match the filters, using exact decimal " +
        "arithmetic on the values saved in the file. By default only item rows are included (subtotal and total rows are excluded to avoid " +
        "double counting); pass rowKinds to change that. Refuses to combine different currencies. Optionally groups by another column. " +
        "Always use this tool for totals instead of adding numbers yourself, and cite the returned evidenceIds.";

    public JsonElement InputSchema { get; } = ToolJson.ParseSchema("""
        {
          "type": "object",
          "properties": {
            "tableId": { "type": "string", "pattern": "^T[0-9]+$" },
            "column": { "type": "string", "description": "Column to aggregate (name or letter)." },
            "operation": { "type": "string", "enum": ["sum", "count", "average", "min", "max"] },
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
            "rowKinds": { "type": "array", "items": { "type": "string", "enum": ["item", "sectionHeader", "subtotal", "total", "unknown"] }, "description": "Row kinds to include (default: [\"item\"])." },
            "groupBy": { "type": "string", "description": "Optional column to group by (at most 100 groups)." }
          },
          "required": ["tableId", "column", "operation"],
          "additionalProperties": false
        }
        """);

    public ToolResult Execute(Session session, JsonElement input, CancellationToken cancellationToken)
    {
        var arguments = new ToolArguments(input, "tableId", "column", "operation", "filters", "rowKinds", "groupBy");
        var stored = TableToolSupport.ResolveTable(session, arguments);
        var table = stored.Table;
        var column = TableToolSupport.ResolveColumn(table, arguments.RequiredString("column", 200));
        var operation = arguments.RequiredEnum<AggregateOperation>("operation");
        var filters = TableToolSupport.ParseFilters(table, arguments);
        var rowKinds = TableToolSupport.ParseRowKinds(arguments) ?? new HashSet<RowKind> { RowKind.Item };
        var groupBy = arguments.OptionalString("groupBy", 200) is { } groupName ? TableToolSupport.ResolveColumn(table, groupName) : null;

        var result = TableAggregator.Aggregate(table, column, operation, filters, rowKinds, groupBy);
        if (result.Error is not null)
        {
            return ToolResult.Error(result.Error);
        }

        var total = result.Total!;
        var evidenceIds = TableToolSupport.EvidenceIdsFor(stored, total.ContributingRows).ToList();

        return ToolResult.Success(
            new
            {
                tableId = stored.TableId,
                sheet = table.SheetName,
                column = column.Name,
                operation,
                currency = result.Currency,
                rowKinds = rowKinds.OrderBy(kind => kind),
                rowsMatched = result.RowsMatched,
                valuesUsed = total.ValuesUsed,
                valuesSkipped = result.ValuesSkipped,
                result = total.Result,
                resultEvidenceIds = TableToolSupport.EvidenceIdsFor(stored, total.ResultRows),
                groups = groupBy is null
                    ? null
                    : result.Groups.Select(group => new
                    {
                        key = group.Key,
                        result = group.Value.Result,
                        valuesUsed = group.Value.ValuesUsed,
                    }),
                evidenceIds = evidenceIds.Take(MaxEvidenceIds),
                evidenceIdsTruncated = evidenceIds.Count > MaxEvidenceIds,
                notes = result.Notes,
            },
            evidenceIds);
    }
}
