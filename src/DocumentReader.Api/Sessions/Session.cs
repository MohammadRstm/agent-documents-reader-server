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

    public DateTimeOffset LastAccessedAt =>
        new(Interlocked.Read(ref _lastAccessedUtcTicks), TimeSpan.Zero);

    public bool IsExpired(DateTimeOffset now, TimeSpan idleTimeout) =>
        now - LastAccessedAt >= idleTimeout;

    internal void Touch(DateTimeOffset now) =>
        Interlocked.Exchange(ref _lastAccessedUtcTicks, now.UtcTicks);
}
