using System.Text.Json;
using DocumentReader.Api.Ingestion.Excel;
using DocumentReader.Api.Sessions;

namespace DocumentReader.Api.Agent.Tools;

/// <summary>Describes a detected table: columns with types and currencies, row kinds, and sample rows.</summary>
public sealed class DescribeTableTool : IAgentTool
{
    private const int SampleRowCount = 3;

    public string Name => "describe_table";

    public string Description =>
        "Describes one Excel table: sheet, cell range, header rows, columns (name, letter, type, currency when stated), " +
        "how many rows are items, section headers, subtotals, totals or unknown, a few sample item rows, and detection warnings. " +
        "Use it before query_table or aggregate_table to pick the right columns.";

    public JsonElement InputSchema { get; } = ToolJson.ParseSchema("""
        {
          "type": "object",
          "properties": {
            "tableId": { "type": "string", "pattern": "^T[0-9]+$", "description": "Table ID from list_documents." }
          },
          "required": ["tableId"],
          "additionalProperties": false
        }
        """);

    public ToolResult Execute(Session session, JsonElement input, CancellationToken cancellationToken)
    {
        var arguments = new ToolArguments(input, "tableId");
        var stored = TableToolSupport.ResolveTable(session, arguments);
        var table = stored.Table;
        var samples = table.Rows.Where(row => row.Kind == RowKind.Item).Take(SampleRowCount).ToList();

        return ToolResult.Success(
            new
            {
                tableId = stored.TableId,
                documentId = stored.DocumentId,
                fileName = stored.FileName,
                sheet = table.SheetName,
                range = table.Range,
                headerRows = table.HeaderFirstRow == table.HeaderLastRow
                    ? $"{table.HeaderFirstRow}"
                    : $"{table.HeaderFirstRow}-{table.HeaderLastRow}",
                columns = table.Columns.Select(column => new
                {
                    name = column.Name,
                    letter = column.Letter,
                    type = column.Type,
                    currency = column.Currency,
                }),
                rowCounts = Enum.GetValues<RowKind>().ToDictionary(
                    kind => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString()),
                    kind => table.Rows.Count(row => row.Kind == kind)),
                sampleRows = samples.Select(row => TableToolSupport.DescribeRow(stored, row, table.Columns)),
                warnings = table.Warnings,
            },
            TableToolSupport.EvidenceIdsFor(stored, samples.Select(row => row.RowNumber)));
    }
}
