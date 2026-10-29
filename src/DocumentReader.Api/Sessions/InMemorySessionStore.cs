using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace DocumentReader.Api.Sessions;

/// <summary>
/// Thread-safe in-memory session store with sliding idle expiry.
/// All session data is lost when the process restarts.
/// </summary>
public sealed class InMemorySessionStore(IOptions<SessionLifetimeOptions> options, TimeProvider timeProvider) : ISessionStore
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _idleTimeout = options.Value.IdleTimeout;

    public Session CreateSession()
    {
        var now = timeProvider.GetUtcNow();
        while (true)
        {
            // A collision of 256-bit random IDs is practically impossible; retry rather than overwrite.
            var session = new Session(SessionIdGenerator.Create(), now);
            if (_sessions.TryAdd(session.Id, session))
            {
                return session;
            }
        }
    }

    public bool TryGetSession(string sessionId, [NotNullWhen(true)] out Session? session)
    {
        session = null;
        if (!_sessions.TryGetValue(sessionId, out var found))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        if (found.IsExpired(now, _idleTimeout))
        {
            // Remove only this exact instance, never a different session stored under the same key.
            _sessions.TryRemove(new KeyValuePair<string, Session>(sessionId, found));
            return false;
        }

        found.Touch(now);
        session = found;
        return true;
    }

    public int RemoveExpiredSessions()
    {
        var now = timeProvider.GetUtcNow();
        var removed = 0;
        foreach (var entry in _sessions)
        {
            if (entry.Value.IsExpired(now, _idleTimeout) && _sessions.TryRemove(entry))
            {
                removed++;
            }
        }

        return removed;
    }
}
