using System.ComponentModel.DataAnnotations;

namespace DocumentReader.Api.Sessions;

/// <summary>Configuration for in-memory session lifetime (appsettings section "Sessions").</summary>
public sealed class SessionLifetimeOptions
{
    public const string SectionName = "Sessions";

    /// <summary>A session expires after this many minutes without a request (sliding expiry).</summary>
    [Range(1, 24 * 60)]
    public int IdleTimeoutMinutes { get; set; } = 60;

    /// <summary>How often the background cleanup removes expired sessions.</summary>
    [Range(1, 3600)]
    public int CleanupIntervalSeconds { get; set; } = 60;

    public TimeSpan IdleTimeout => TimeSpan.FromMinutes(IdleTimeoutMinutes);

    public TimeSpan CleanupInterval => TimeSpan.FromSeconds(CleanupIntervalSeconds);
}
