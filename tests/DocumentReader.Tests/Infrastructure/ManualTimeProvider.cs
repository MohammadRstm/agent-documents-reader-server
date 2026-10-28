namespace DocumentReader.Tests.Infrastructure;

/// <summary>A clock that only moves when a test advances it.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _lock = new();
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan amount)
    {
        lock (_lock)
        {
            _now += amount;
        }
    }
}
