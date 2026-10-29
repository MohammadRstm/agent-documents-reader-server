using System.Text.Json;

namespace DocumentReader.Api.Llm;

/// <summary>
/// Provider-neutral access to a chat model with tool calling. The agent loop depends only on this
/// interface; Anthropic specifics live in <see cref="AnthropicChatModelClient"/>, and tests use a
/// scripted fake.
/// </summary>
public interface IChatModelClient
{
    /// <summary>False when the provider cannot be called (for example, no API key is configured).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends one request and returns the complete response. Text is also reported incrementally
    /// through <paramref name="onTextDelta"/> while it streams.
    /// </summary>
    /// <exception cref="ChatModelUnavailableException">The provider failed or could not be reached.</exception>
    Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        Func<string, CancellationToken, Task> onTextDelta,
        CancellationToken cancellationToken);
}

/// <summary>A tool offered to the model. <see cref="InputSchema"/> is a JSON Schema object.</summary>
public sealed record ModelToolDefinition(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// One model request. <see cref="AllowToolUse"/> false keeps the same tools declared but tells the
/// model not to call them (changing the tool list mid-conversation would invalidate earlier reasoning).
/// </summary>
public sealed record ModelRequest(
    string SystemPrompt,
    IReadOnlyList<ModelTurn> Messages,
    IReadOnlyList<ModelToolDefinition> Tools,
    bool AllowToolUse);

/// <summary>A conversation entry sent to the model.</summary>
public abstract record ModelTurn;

/// <summary>Text from the user (a question or an instruction from the application).</summary>
public sealed record UserTextTurn(string Text) : ModelTurn;

/// <summary>An earlier final answer, replayed as plain text from the conversation history.</summary>
public sealed record AssistantTextTurn(string Text) : ModelTurn;

/// <summary>A model response from the current question, replayed exactly as the provider returned it.</summary>
public sealed record AssistantResponseTurn(ModelResponse Response) : ModelTurn;

/// <summary>Results for every tool call of the preceding assistant response.</summary>
public sealed record ToolResultsTurn(IReadOnlyList<ToolResultItem> Results) : ModelTurn;

public sealed record ToolResultItem(string ToolCallId, string Content, bool IsError);

public enum ModelStopReason
{
    EndTurn,
    ToolUse,
    MaxTokens,
    Refusal,
    Other,
}

/// <summary>A tool call requested by the model. <see cref="Input"/> is null when the arguments were not valid JSON.</summary>
public sealed record ModelToolCall(string Id, string Name, JsonElement? Input);

/// <summary>
/// A complete model response. <see cref="ProviderContent"/> holds the provider's own representation of
/// the response (including reasoning blocks) so it can be sent back unchanged on the next request.
/// </summary>
public sealed record ModelResponse(
    ModelStopReason StopReason,
    string Text,
    IReadOnlyList<ModelToolCall> ToolCalls,
    object? ProviderContent = null);

/// <summary>The model provider failed, timed out at the network level, or is not configured.</summary>
public sealed class ChatModelUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
