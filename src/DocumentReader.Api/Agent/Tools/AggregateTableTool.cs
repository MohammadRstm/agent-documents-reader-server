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
        "Always use this tool for totals instead of adding numbers yourself, and cite the returned evidenceIds. " +
        "When summing item rows without filters or grouping, the result also includes the table's own stated total " +
        "(statedTotal, statedTotalEvidenceId, matchesStatedTotal) and stated subtotals; if matchesStatedTotal is false, " +
        "tell the user that the calculated sum and the document's total differ and give both figures.";

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
        var notes = result.Notes.ToList();
        var check = CrossCheck(stored, column, operation, filters, rowKinds, groupBy, total.Result, notes);

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
                statedTotal = check?.Total?.Value,
                statedTotalEvidenceId = check?.TotalEvidenceId,
                matchesStatedTotal = check?.Total is { } statedTotal ? statedTotal.Value == total.Result : (bool?)null,
                statedSubtotals = check?.Subtotals,
                evidenceIds = evidenceIds.Take(MaxEvidenceIds),
                evidenceIdsTruncated = evidenceIds.Count > MaxEvidenceIds,
                notes,
            },
            evidenceIds.Concat(check?.EvidenceIds ?? []));
    }

    /// <summary>
    /// Deterministic cross-check of a whole-table item sum against the total and subtotals the
    /// document states in the same column. Only meaningful without filters or grouping, because the
    /// stated figures cover the whole table.
    /// </summary>
    private static CrossCheckResult? CrossCheck(
        StoredTable stored,
        TableColumn column,
        AggregateOperation operation,
        IReadOnlyList<RowFilter> filters,
        IReadOnlySet<RowKind> rowKinds,
        TableColumn? groupBy,
        decimal? sum,
        List<string> notes)
    {
        if (operation != AggregateOperation.Sum || filters.Count > 0 || groupBy is not null
            || !rowKinds.SetEquals([RowKind.Item]) || sum is not { } itemSum)
        {
            return null;
        }

        var stated = StatedTotals.Find(stored.Table, column);
        if (stated.Total is null && stated.Subtotals.Count == 0)
        {
            return null;
        }

        string? EvidenceId(StatedValue value) => stored.RowEvidenceIds.GetValueOrDefault(value.RowNumber);

        if (stated.Total is { } statedTotal && statedTotal.Value != itemSum)
        {
            notes.Add($"The sum of the item rows ({itemSum}) differs from the total stated in row {statedTotal.RowNumber} ({statedTotal.Value}). " +
                "Rows may be missing, duplicated or misclassified, or the document's total may be wrong.");
        }

        var subtotalSum = stated.Subtotals.Sum(value => value.Value);
        if (stated.Subtotals.Count > 0 && subtotalSum != itemSum)
        {
            notes.Add($"The stated subtotals add up to {subtotalSum}, which differs from the sum of the item rows ({itemSum}).");
        }

        var subtotals = stated.Subtotals
            .Take(MaxStatedSubtotals)
            .Select(value => new StatedSubtotal(EvidenceId(value), value.RowNumber, value.Label, value.Value))
            .ToList();
        var evidence = subtotals.Select(value => value.EvidenceId)
            .Append(stated.Total is null ? null : EvidenceId(stated.Total))
            .OfType<string>()
            .ToList();

        return new CrossCheckResult(stated.Total, stated.Total is null ? null : EvidenceId(stated.Total), subtotals, evidence);
    }

    private const int MaxStatedSubtotals = 50;

    private sealed record CrossCheckResult(
        StatedValue? Total,
        string? TotalEvidenceId,
        IReadOnlyList<StatedSubtotal> Subtotals,
        IReadOnlyList<string> EvidenceIds);

    private sealed record StatedSubtotal(string? EvidenceId, int RowNumber, string Label, decimal Value);
}
