using System.Diagnostics.CodeAnalysis;

namespace DocumentReader.Api.Sessions;

/// <summary>
/// Stores sessions. The in-memory implementation is the only one for now; the interface
/// keeps callers independent of storage so a persistent store can be added later.
/// </summary>
public interface ISessionStore
{
    Session CreateSession();

    /// <summary>
    /// Returns the session if it exists and has not expired, and records the access
    /// (sliding expiry). Expired sessions are removed and reported as not found.
    /// </summary>
    bool TryGetSession(string sessionId, [NotNullWhen(true)] out Session? session);

    /// <summary>Removes all expired sessions and returns how many were removed.</summary>
    int RemoveExpiredSessions();
}
