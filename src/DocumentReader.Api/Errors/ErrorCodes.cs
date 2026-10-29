namespace DocumentReader.Api.Errors;

/// <summary>
/// Machine-readable error codes defined by docs/api-contract.md.
/// </summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string SessionRequired = "session_required";
    public const string SessionNotFound = "session_not_found";
    public const string EvidenceNotFound = "evidence_not_found";
    public const string QuestionInProgress = "question_in_progress";
    public const string DocumentsProcessing = "documents_processing";
    public const string NoDocuments = "no_documents";
    public const string PayloadTooLarge = "payload_too_large";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string RateLimited = "rate_limited";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string InternalError = "internal_error";
}
