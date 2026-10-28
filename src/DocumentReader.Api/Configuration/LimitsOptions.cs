using System.ComponentModel.DataAnnotations;

namespace DocumentReader.Api.Configuration;

/// <summary>Request and work limits (appsettings section "Limits").</summary>
public sealed class LimitsOptions
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

    /// <summary>Maximum model round trips the agent may make while answering one question.</summary>
    [Range(1, 50)]
    public int MaxModelRoundTripsPerQuestion { get; set; } = 8;

    /// <summary>Maximum time spent answering one question.</summary>
    [Range(1, 600)]
    public int QuestionTimeoutSeconds { get; set; } = 120;
}
