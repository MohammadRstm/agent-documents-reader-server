using DocumentReader.Api.Sessions;
using DocumentReader.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace DocumentReader.Tests.Sessions;

public sealed class InMemorySessionStoreTests
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemorySessionStore _store;

    public InMemorySessionStoreTests()
    {
        var options = Options.Create(new SessionLifetimeOptions { IdleTimeoutMinutes = 30 });
        _store = new InMemorySessionStore(options, _clock);
    }

    [Fact]
    public void CreateSession_ReturnsSessionThatCanBeRetrieved()
    {
        var created = _store.CreateSession();

        Assert.True(_store.TryGetSession(created.Id, out var found));
        Assert.Same(created, found);
        Assert.Equal(_clock.GetUtcNow(), created.CreatedAt);
    }

    [Fact]
    public void TryGetSession_ReturnsFalseForUnknownId()
    {
        Assert.False(_store.TryGetSession(SessionIdGenerator.Create(), out var session));
        Assert.Null(session);
    }

    [Fact]
    public void TryGetSession_ReturnsFalseOnceIdleTimeoutHasPassed()
    {
        var created = _store.CreateSession();

        _clock.Advance(IdleTimeout);

        Assert.False(_store.TryGetSession(created.Id, out _));
    }

    [Fact]
    public void TryGetSession_ExtendsExpiryOnEachAccess()
    {
        var created = _store.CreateSession();

        _clock.Advance(IdleTimeout - TimeSpan.FromMinutes(1));
        Assert.True(_store.TryGetSession(created.Id, out _));

        // Without sliding expiry the session would now be past its timeout.
        _clock.Advance(IdleTimeout - TimeSpan.FromMinutes(1));
        Assert.True(_store.TryGetSession(created.Id, out _));
    }

    [Fact]
    public void TryGetSession_ExpiredSessionStaysGoneAfterClockWouldAllowIt()
    {
        var created = _store.CreateSession();
        _clock.Advance(IdleTimeout);
        Assert.False(_store.TryGetSession(created.Id, out _));

        // An expired session is removed, not merely hidden.
        Assert.Equal(0, _store.RemoveExpiredSessions());
        Assert.False(_store.TryGetSession(created.Id, out _));
    }

    [Fact]
    public void RemoveExpiredSessions_RemovesOnlyExpiredSessions()
    {
        var idle = _store.CreateSession();
        _clock.Advance(IdleTimeout - TimeSpan.FromMinutes(5));
        var active = _store.CreateSession();
        _clock.Advance(TimeSpan.FromMinutes(5));

        var removed = _store.RemoveExpiredSessions();

        Assert.Equal(1, removed);
        Assert.False(_store.TryGetSession(idle.Id, out _));
        Assert.True(_store.TryGetSession(active.Id, out _));
    }

    [Fact]
    public void CreateSession_CreatesIndependentSessions()
    {
        var first = _store.CreateSession();
        var second = _store.CreateSession();

        Assert.NotEqual(first.Id, second.Id);
        Assert.True(_store.TryGetSession(first.Id, out var foundFirst));
        Assert.True(_store.TryGetSession(second.Id, out var foundSecond));
        Assert.NotSame(foundFirst, foundSecond);
    }
}
