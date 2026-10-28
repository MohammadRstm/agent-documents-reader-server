using System.Text.Json.Serialization;

namespace DocumentReader.Api.Errors;

/// <summary>
/// The body of every non-2xx JSON response, as defined by docs/api-contract.md.
/// </summary>
public sealed record ErrorResponse(
    string Code,
    string Message,
    string TraceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string[]>? Details = null);
