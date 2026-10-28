using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace DocumentReader.Api.Llm;

/// <summary>
/// <see cref="IChatModelClient"/> for the Claude API using the official Anthropic C# SDK. Each request
/// is streamed so text reaches the user as it is generated; the streamed events are assembled into
/// the complete response, and that response (including thinking blocks and their signatures) is sent
/// back unchanged on the next request of the same question.
/// </summary>
public sealed class AnthropicChatModelClient : IChatModelClient
{
    private readonly AnthropicOptions _options;
    private readonly AnthropicClient? _client;
    private readonly ILogger<AnthropicChatModelClient> _logger;

    public AnthropicChatModelClient(IOptions<AnthropicOptions> options, ILogger<AnthropicChatModelClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _client = new AnthropicClient { ApiKey = _options.ApiKey };
        }
    }

    public bool IsConfigured => _client is not null;

    public async Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        Func<string, CancellationToken, Task> onTextDelta,
        CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            throw new ChatModelUnavailableException("The Claude API key is not configured.");
        }

        var parameters = BuildParameters(request);
        var blocks = new SortedDictionary<long, StreamedBlock>();
        var stopReason = ModelStopReason.Other;

        try
        {
            await foreach (var streamEvent in _client.Messages.CreateStreaming(parameters, cancellationToken))
            {
                if (streamEvent.TryPickContentBlockStart(out var start))
                {
                    blocks[start.Index] = StreamedBlock.From(start.ContentBlock);
                }
                else if (streamEvent.TryPickContentBlockDelta(out var delta) && blocks.TryGetValue(delta.Index, out var block))
                {
                    var text = block.Apply(delta.Delta);
                    if (text is not null)
                    {
                        await onTextDelta(text, cancellationToken);
                    }
                }
                else if (streamEvent.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason is { } reason)
                {
                    stopReason = MapStopReason(reason.Raw());
                }
            }
        }
        catch (Exception exception) when (exception is AnthropicException or HttpRequestException or JsonException)
        {
            // Provider details are logged server-side only; callers get a generic, safe message.
            _logger.LogWarning(exception, "Claude API request failed.");
            throw new ChatModelUnavailableException("The AI provider request failed.", exception);
        }

        return BuildResponse(stopReason, blocks.Values.ToList());
    }

    private MessageCreateParams BuildParameters(ModelRequest request) => new()
    {
        Model = _options.Model,
        MaxTokens = _options.MaxOutputTokens,
        // Stable system prompt and tools first, cached; the question and tool results come after.
        CacheControl = new CacheControlEphemeral(),
        System = new List<TextBlockParam> { new() { Text = request.SystemPrompt } },
        Tools = request.Tools.Select(ToTool).ToList(),
        // Thinking is adaptive by default on this model; effort sets its depth.
        OutputConfig = new OutputConfig { Effort = ToEffort(_options.Effort) },
        ToolChoice = request.AllowToolUse ? new ToolChoiceAuto() : new ToolChoiceNone(),
        Messages = ToMessages(request.Messages),
    };

    private static ToolUnion ToTool(ModelToolDefinition definition) => new Tool
    {
        Name = definition.Name,
        Description = definition.Description,
        InputSchema = InputSchema.FromRawUnchecked(
            definition.InputSchema.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone())),
    };

    private static List<MessageParam> ToMessages(IReadOnlyList<ModelTurn> turns)
    {
        var messages = new List<MessageParam>();
        foreach (var turn in turns)
        {
            switch (turn)
            {
                case UserTextTurn user:
                    messages.Add(new UserMessageParam { Content = user.Text });
                    break;
                case AssistantTextTurn assistant:
                    messages.Add(new AssistantMessageParam { Content = assistant.Text });
                    break;
                case AssistantResponseTurn { Response.ProviderContent: List<ContentBlockParam> content }:
                    messages.Add(new AssistantMessageParam { Content = content });
                    break;
                case AssistantResponseTurn response:
                    // A response that did not come from this client (should not happen): replay its text only.
                    messages.Add(new AssistantMessageParam { Content = response.Response.Text });
                    break;
                case ToolResultsTurn results:
                    var resultBlocks = results.Results
                        .Select(result => (ContentBlockParam)new ToolResultBlockParam
                        {
                            ToolUseID = result.ToolCallId,
                            Content = result.Content,
                            IsError = result.IsError,
                        })
                        .ToList();
                    messages.Add(new UserMessageParam { Content = resultBlocks });
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(turns), turn.GetType().Name, "Unknown conversation turn.");
            }
        }

        return messages;
    }

    private static ModelResponse BuildResponse(ModelStopReason stopReason, List<StreamedBlock> blocks)
    {
        var content = new List<ContentBlockParam>();
        var text = new StringBuilder();
        var toolCalls = new List<ModelToolCall>();

        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case StreamedBlockKind.Text:
                    text.Append(block.Text);
                    content.Add(new TextBlockParam { Text = block.Text.ToString() });
                    break;
                case StreamedBlockKind.Thinking:
                    // Reasoning must be sent back unchanged, signature included.
                    content.Add(new ThinkingBlockParam { Thinking = block.Thinking.ToString(), Signature = block.Signature });
                    break;
                case StreamedBlockKind.RedactedThinking:
                    content.Add(new RedactedThinkingBlockParam { Data = block.RedactedData });
                    break;
                case StreamedBlockKind.ToolUse:
                    var input = ParseToolInput(block.InputJson.ToString());
                    toolCalls.Add(new ModelToolCall(block.ToolId, block.ToolName, input));
                    content.Add(new ToolUseBlockParam
                    {
                        ID = block.ToolId,
                        Name = block.ToolName,
                        Input = input is { ValueKind: JsonValueKind.Object } inputObject
                            ? inputObject.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone())
                            : new Dictionary<string, JsonElement>(),
                    });
                    break;
            }
        }

        return new ModelResponse(stopReason, text.ToString(), toolCalls, content);
    }

    /// <summary>Parses streamed tool arguments; returns null when they are not a valid JSON object.</summary>
    private static JsonElement? ParseToolInput(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ModelStopReason MapStopReason(string reason) => reason switch
    {
        "end_turn" or "stop_sequence" => ModelStopReason.EndTurn,
        "tool_use" => ModelStopReason.ToolUse,
        "max_tokens" => ModelStopReason.MaxTokens,
        "refusal" => ModelStopReason.Refusal,
        _ => ModelStopReason.Other,
    };

    private static Effort ToEffort(string effort) => effort switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => throw new InvalidOperationException($"Unsupported effort level '{effort}'."),
    };

    private enum StreamedBlockKind
    {
        Text,
        Thinking,
        RedactedThinking,
        ToolUse,
        Other,
    }

    /// <summary>One content block assembled from stream events.</summary>
    private sealed class StreamedBlock
    {
        public StreamedBlockKind Kind { get; private init; }

        public StringBuilder Text { get; } = new();

        public StringBuilder Thinking { get; } = new();

        public string Signature { get; private set; } = string.Empty;

        public string RedactedData { get; private init; } = string.Empty;

        public string ToolId { get; private init; } = string.Empty;

        public string ToolName { get; private init; } = string.Empty;

        public StringBuilder InputJson { get; } = new();

        public static StreamedBlock From(RawContentBlockStartEventContentBlock contentBlock)
        {
            if (contentBlock.TryPickText(out var text))
            {
                var block = new StreamedBlock { Kind = StreamedBlockKind.Text };
                block.Text.Append(text.Text);
                return block;
            }

            if (contentBlock.TryPickThinking(out var thinking))
            {
                var block = new StreamedBlock { Kind = StreamedBlockKind.Thinking, Signature = thinking.Signature };
                block.Thinking.Append(thinking.Thinking);
                return block;
            }

            if (contentBlock.TryPickRedactedThinking(out var redacted))
            {
                return new StreamedBlock { Kind = StreamedBlockKind.RedactedThinking, RedactedData = redacted.Data };
            }

            if (contentBlock.TryPickToolUse(out var toolUse))
            {
                return new StreamedBlock { Kind = StreamedBlockKind.ToolUse, ToolId = toolUse.ID, ToolName = toolUse.Name };
            }

            return new StreamedBlock { Kind = StreamedBlockKind.Other };
        }

        /// <summary>Applies a delta; returns new visible answer text, if any.</summary>
        public string? Apply(RawContentBlockDelta delta)
        {
            if (delta.TryPickText(out var text))
            {
                Text.Append(text.Text);
                return text.Text;
            }

            if (delta.TryPickInputJson(out var inputJson))
            {
                InputJson.Append(inputJson.PartialJson);
            }
            else if (delta.TryPickThinking(out var thinking))
            {
                Thinking.Append(thinking.Thinking);
            }
            else if (delta.TryPickSignature(out var signature))
            {
                Signature = signature.Signature;
            }

            return null;
        }
    }
}
