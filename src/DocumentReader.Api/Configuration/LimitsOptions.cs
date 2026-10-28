using System.ComponentModel.DataAnnotations;

namespace DocumentReader.Api.Configuration;

/// <summary>Request and work limits (appsettings section "Limits").</summary>
public sealed class LimitsOptions : IValidatableObject
{
    public const string SectionName = "Limits";

    /// <summary>Sessions that may be created per client IP address per minute.</summary>
    [Range(1, 10_000)]
    public int SessionCreationRequestsPerMinute { get; set; } = 10;

    /// <summary>Upload requests allowed per client IP address per minute.</summary>
    [Range(1, 10_000)]
    public int UploadRequestsPerMinute { get; set; } = 10;

    /// <summary>Chat questions allowed per client IP address per minute.</summary>
    [Range(1, 10_000)]
    public int ChatRequestsPerMinute { get; set; } = 20;

    /// <summary>A single file larger than this is reported with status tooLarge and not processed.</summary>
    [Range(1, 1L << 30)]
    public long MaxFileSizeBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>More files than this in one upload request rejects the whole request (413).</summary>
    [Range(1, 100)]
    public int MaxFilesPerUpload { get; set; } = 10;

    /// <summary>An upload request body larger than this is rejected as a whole (413).</summary>
    [Range(1, 1L << 31)]
    public long MaxUploadRequestBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>Uploads that would take a session above this many documents are rejected (413).</summary>
    [Range(1, 1000)]
    public int MaxDocumentsPerSession { get; set; } = 20;

    /// <summary>Only this many pages of a PDF are extracted; later pages are skipped with a warning.</summary>
    [Range(1, 10_000)]
    public int MaxPdfPages { get; set; } = 500;

    /// <summary>Time budget for extracting one file.</summary>
    [Range(1, 600)]
    public int ExtractionTimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum model round trips the agent may make while answering one question.</summary>
    [Range(1, 50)]
    public int MaxModelRoundTripsPerQuestion { get; set; } = 8;

    /// <summary>Maximum time spent answering one question.</summary>
    [Range(1, 600)]
    public int QuestionTimeoutSeconds { get; set; } = 120;

    public TimeSpan ExtractionTimeout => TimeSpan.FromSeconds(ExtractionTimeoutSeconds);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxFileSizeBytes > MaxUploadRequestBytes)
        {
            yield return new ValidationResult(
                $"{nameof(MaxFileSizeBytes)} must not exceed {nameof(MaxUploadRequestBytes)}.",
                [nameof(MaxFileSizeBytes), nameof(MaxUploadRequestBytes)]);
        }
    }
}
