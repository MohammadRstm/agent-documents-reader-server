using System.Text.Json;
using DocumentReader.Api.Sessions;

namespace DocumentReader.Api.Agent.Tools;

/// <summary>Returns the full text and source of evidence items by ID (only this session's evidence).</summary>
public sealed class GetEvidenceTool : IAgentTool
{
    private const int MaxIds = 10;

    public string Name => "get_evidence";

    public string Description =>
        "Returns the full text and exact source (PDF page, or Excel sheet and cell range) of up to 10 evidence items by evidenceId.";

    public JsonElement InputSchema { get; } = ToolJson.ParseSchema("""
        {
          "type": "object",
          "properties": {
            "evidenceIds": { "type": "array", "items": { "type": "string", "pattern": "^E[0-9]+$" }, "minItems": 1, "maxItems": 10 }
          },
          "required": ["evidenceIds"],
          "additionalProperties": false
        }
        """);

    public ToolResult Execute(Session session, JsonElement input, CancellationToken cancellationToken)
    {
        var arguments = new ToolArguments(input, "evidenceIds");
        var ids = arguments.RequiredStringArray("evidenceIds", MaxIds, 20);

        var found = new List<object>();
        var foundIds = new List<string>();
        var notFound = new List<string>();
        foreach (var id in ids)
        {
            if (ToolArguments.IsEvidenceId(id) && session.Documents.TryGetEvidence(id, out var evidence))
            {
                foundIds.Add(id);
                found.Add(new
                {
                    evidenceId = evidence.Id,
                    documentId = evidence.DocumentId,
                    fileName = evidence.FileName,
                    kind = evidence.Kind,
                    source = evidence.Source,
                    text = evidence.Text,
                });
            }
            else
            {
                notFound.Add(id);
            }
        }

        return ToolResult.Success(new { evidence = found, notFound }, foundIds);
    }
}
