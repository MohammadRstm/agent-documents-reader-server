namespace DocumentReader.Api.Agent;

/// <summary>Receives progress while the agent works (implemented by the SSE writer).</summary>
public interface IAgentEventSink
{
    Task StatusAsync(string message, string? tool, CancellationToken cancellationToken);

    Task DeltaAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// The agent's raw answer, before citation validation. <see cref="RetrievedEvidenceIds"/> are the
/// evidence IDs that tools returned while answering; only those (and earlier cited ones) may be cited.
/// </summary>
public sealed record AgentAnswer(string Text, bool Incomplete, IReadOnlySet<string> RetrievedEvidenceIds);

/// <summary>The question ran out of time before a final answer could be produced.</summary>
public sealed class AgentTimeoutException() : Exception("The question could not be answered within the time limit.");
