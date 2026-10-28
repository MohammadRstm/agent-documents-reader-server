namespace DocumentReader.Api.Errors;

/// <summary>
/// An expected, client-caused failure that maps directly to a contract error response.
/// The message is shown to the client, so it must be safe and must not contain internal details.
/// </summary>
public sealed class ApiException(
    int statusCode,
    string code,
    string message,
    IReadOnlyDictionary<string, string[]>? details = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public IReadOnlyDictionary<string, string[]>? Details { get; } = details;

    public static ApiException PayloadTooLarge(string message) =>
        new(StatusCodes.Status413PayloadTooLarge, ErrorCodes.PayloadTooLarge, message);

    public static ApiException ValidationFailed(string field, string message) =>
        new(
            StatusCodes.Status400BadRequest,
            ErrorCodes.ValidationFailed,
            "The request is invalid.",
            new Dictionary<string, string[]> { [field] = [message] });
}
