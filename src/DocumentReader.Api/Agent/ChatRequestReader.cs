using System.Text.Json;
using DocumentReader.Api.Errors;

namespace DocumentReader.Api.Agent;

/// <summary>
/// Reads and validates the chat request body ({ "question": string }) before any stream is opened:
/// 415 for non-JSON bodies, 400 validation_failed for malformed JSON or an invalid question.
/// </summary>
public static class ChatRequestReader
{
    public const int MaxQuestionLength = 4000;

    /// <summary>Request bodies larger than this are rejected (413) before parsing.</summary>
    public const long MaxRequestBodyBytes = 64 * 1024;

    public static async Task<string> ReadQuestionAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
        {
            throw new ApiException(
                StatusCodes.Status415UnsupportedMediaType,
                ErrorCodes.UnsupportedMediaType,
                "Chat requests must be sent as application/json.");
        }

        JsonElement body;
        try
        {
            body = await request.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }
        catch (JsonException)
        {
            throw ApiException.ValidationFailed("body", "The request body is not valid JSON.");
        }

        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("question", out var questionElement)
            || questionElement.ValueKind != JsonValueKind.String)
        {
            throw ApiException.ValidationFailed("question", "Question is required and must be a string.");
        }

        var question = questionElement.GetString()!.Trim();
        if (question.Length is 0 or > MaxQuestionLength)
        {
            throw ApiException.ValidationFailed("question", $"Question must be 1 to {MaxQuestionLength} characters after trimming.");
        }

        return question;
    }
}
