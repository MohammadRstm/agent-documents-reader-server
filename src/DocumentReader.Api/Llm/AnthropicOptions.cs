using System.ComponentModel.DataAnnotations;

namespace DocumentReader.Api.Llm;

/// <summary>
/// Claude API settings (appsettings section "Anthropic").
/// The API key must come from User Secrets or the environment, never from a committed file.
/// </summary>
public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>
    /// Not validated at startup so the API can run (sessions, uploads, evidence) without a key;
    /// chat requests return 502 provider_unavailable while it is missing.
    /// </summary>
    public string? ApiKey { get; set; }

    [Required]
    public string Model { get; set; } = "claude-sonnet-5-5";

    /// <summary>Thinking depth and token spend per request.</summary>
    [Required]
    [RegularExpression("^(low|medium|high|xhigh|max)$", ErrorMessage = "Effort must be low, medium, high, xhigh or max.")]
    public string Effort { get; set; } = "medium";

    /// <summary>Output token cap per model request (thinking counts toward it).</summary>
    [Range(1024, 128_000)]
    public int MaxOutputTokens { get; set; } = 16_000;
}
