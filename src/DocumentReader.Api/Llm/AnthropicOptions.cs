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
    /// the chat feature checks it before calling the provider.
    /// </summary>
    public string? ApiKey { get; set; }

    [Required]
    public string Model { get; set; } = "claude-sonnet-5-5";

    [Required]
    public string Effort { get; set; } = "medium";
}
