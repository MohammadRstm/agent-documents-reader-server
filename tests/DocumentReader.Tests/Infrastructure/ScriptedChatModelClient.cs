using System.Text.Json;
using DocumentReader.Api.Llm;

namespace DocumentReader.Tests.Infrastructure;

/// <summary>
/// A fake chat model that replays scripted steps in order and records every request, so the agent
/// loop and the chat endpoint can be tested deterministically without network access or an API key.
/// </summary>
public sealed class ScriptedChatModelClient : IChatModelClient
{
    private readonly Lock _lock = new();
    private readonly Queue<Func<ModelRequest, Func<string, CancellationToken, Task>, CancellationToken, Task<ModelResponse>>> _steps = new();
    private readonly List<ModelRequest> _requests = [];

    public bool IsConfigured { get; set; } = true;

    public IReadOnlyList<ModelRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _steps.Clear();
            _requests.Clear();
        }

        IsConfigured = true;
    }

    /// <summary>Responds with final text, streamed as two deltas.</summary>
    public ScriptedChatModelClient ThenAnswer(string text, ModelStopReason stopReason = ModelStopReason.EndTurn) =>
        Then(async (_, onDelta, cancellationToken) =>
        {
            var half = text.Length / 2;
            await onDelta(text[..half], cancellationToken);
            await onDelta(text[half..], cancellationToken);
            return new ModelResponse(stopReason, text, []);
        });

    /// <summary>Requests one or more tool calls (each as name + JSON arguments; null JSON means invalid arguments).</summary>
    public ScriptedChatModelClient ThenCallTools(params (string Name, string? Json)[] calls) =>
        Then((_, _, _) => Task.FromResult(new ModelResponse(
            ModelStopReason.ToolUse,
            string.Empty,
            calls.Select((call, index) => new ModelToolCall($"call_{index}", call.Name, call.Json is null ? null : TestSessions.Json(call.Json))).ToList())));

    public ScriptedChatModelClient ThenThrow(Exception exception) =>
        Then((_, _, _) => Task.FromException<ModelResponse>(exception));

    /// <summary>Waits until the request is cancelled (simulates a slow provider).</summary>
    public ScriptedChatModelClient ThenHangUntilCancelled() =>
        Then(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });

    /// <summary>Signals <paramref name="started"/>, then waits for <paramref name="release"/> before answering.</summary>
    public ScriptedChatModelClient ThenWaitFor(TaskCompletionSource started, Task release, string answer) =>
        Then(async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            await release.WaitAsync(cancellationToken);
            return new ModelResponse(ModelStopReason.EndTurn, answer, []);
        });

    public ScriptedChatModelClient Then(
        Func<ModelRequest, Func<string, CancellationToken, Task>, CancellationToken, Task<ModelResponse>> step)
    {
        lock (_lock)
        {
            _steps.Enqueue(step);
        }

        return this;
    }

    public Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        Func<string, CancellationToken, Task> onTextDelta,
        CancellationToken cancellationToken)
    {
        Func<ModelRequest, Func<string, CancellationToken, Task>, CancellationToken, Task<ModelResponse>> step;
        lock (_lock)
        {
            _requests.Add(request);
            if (!_steps.TryDequeue(out step!))
            {
                throw new InvalidOperationException($"No scripted response for request {_requests.Count}.");
            }
        }

        return step(request, onTextDelta, cancellationToken);
    }

    /// <summary>Text of all tool results sent in a request, for assertions.</summary>
    public static IEnumerable<ToolResultItem> ToolResults(ModelRequest request) =>
        request.Messages.OfType<ToolResultsTurn>().SelectMany(turn => turn.Results);

    public static JsonElement ResultJson(ToolResultItem result)
    {
        var start = result.Content.IndexOf('\n') + 1;
        var end = result.Content.IndexOf("\n</document_data>", StringComparison.Ordinal);
        return TestSessions.Json(result.Content[start..end]);
    }
}
