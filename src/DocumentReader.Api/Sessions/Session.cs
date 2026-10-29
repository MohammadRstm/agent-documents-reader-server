namespace DocumentReader.Api.Sessions;

/// <summary>
/// An anonymous, in-memory document session. Later steps attach the session's documents,
/// evidence and conversation here so that everything is scoped to one session.
/// </summary>
public sealed class Session
{
    // Stored as UTC ticks so concurrent requests can update it atomically.
    private long _lastAccessedUtcTicks;

    public Session(string id, DateTimeOffset createdAt)
    {
        Id = id;
        CreatedAt = createdAt;
        _lastAccessedUtcTicks = createdAt.UtcTicks;
    }

    public string Id { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Uploaded documents and their evidence; only reachable through this session.</summary>
    public SessionDocuments Documents { get; } = new();

    /// <summary>Upload requests of this session that are still being processed.</summary>
    public UploadTracker Uploads { get; } = new();

    /// <summary>Chat history and the one-question-at-a-time guard.</summary>
    public SessionConversation Conversation { get; } = new();

    public DateTimeOffset LastAccessedAt =>
        new(Interlocked.Read(ref _lastAccessedUtcTicks), TimeSpan.Zero);

    public bool IsExpired(DateTimeOffset now, TimeSpan idleTimeout) =>
        now - LastAccessedAt >= idleTimeout;

    internal void Touch(DateTimeOffset now) =>
        Interlocked.Exchange(ref _lastAccessedUtcTicks, now.UtcTicks);
}
