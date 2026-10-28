using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocumentReader.Api.Agent.Tools;

/// <summary>
/// What a tool returns to the model: JSON content, whether it is an error, and the evidence IDs the
/// content refers to (so the agent can later check that cited IDs were actually retrieved).
/// </summary>
public sealed record ToolResult(bool IsError, string Content, IReadOnlyList<string> EvidenceIds)
{
    /// <summary>Upper bound on the serialized size of one tool result sent to the model.</summary>
    public const int MaxContentLength = 40_000;

    public static ToolResult Success(object payload, IEnumerable<string> evidenceIds)
    {
        var content = JsonSerializer.Serialize(payload, ToolJson.Options);
        if (content.Length > MaxContentLength)
        {
            return Error("The result is too large. Narrow the request: ask for fewer rows, columns or IDs, or add filters.");
        }

        return new ToolResult(false, content, evidenceIds.Distinct(StringComparer.Ordinal).ToList());
    }

    public static ToolResult Error(string message) =>
        new(true, JsonSerializer.Serialize(new { error = message }, ToolJson.Options), []);
}

/// <summary>JSON settings for tool input and output: camelCase names and enums, nulls omitted.</summary>
public static class ToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonElement ParseSchema(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return document.RootElement.Clone();
    }
}
