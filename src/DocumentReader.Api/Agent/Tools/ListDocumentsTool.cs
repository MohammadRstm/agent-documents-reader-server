using System.Text.Json;
using DocumentReader.Api.Sessions;

namespace DocumentReader.Api.Agent.Tools;

/// <summary>Lists the session's documents with their status, sheets and detected tables.</summary>
public sealed class ListDocumentsTool : IAgentTool
{
    private const int MaxWarningsPerDocument = 10;

    public string Name => "list_documents";

    public string Description =>
        "Lists every uploaded document in this conversation: ID, file name, type, processing status, page count or sheets, " +
        "warnings, and detected Excel tables (tableId, sheet, range, row count, column names). Call this first to learn what is available.";

    public JsonElement InputSchema { get; } = ToolJson.ParseSchema("""
        { "type": "object", "properties": {}, "additionalProperties": false }
        """);

    public ToolResult Execute(Session session, JsonElement input, CancellationToken cancellationToken)
    {
        _ = new ToolArguments(input);
        var tables = session.Documents.GetTables();

        var documents = session.Documents.GetDocuments().Select(document => new
        {
            documentId = document.Id,
            fileName = document.FileName,
            fileType = document.FileType,
            status = document.Status,
            pageCount = document.PageCount,
            sheets = document.Sheets,
            warnings = document.Warnings.Take(MaxWarningsPerDocument),
            tables = tables
                .Where(table => table.DocumentId == document.Id)
                .Select(table => new
                {
                    tableId = table.TableId,
                    sheet = table.Table.SheetName,
                    range = table.Table.Range,
                    rowCount = table.Table.Rows.Count,
                    columns = table.Table.Columns.Select(column => column.Name),
                }),
        }).ToList();

        return ToolResult.Success(new { documentCount = documents.Count, documents }, []);
    }
}
