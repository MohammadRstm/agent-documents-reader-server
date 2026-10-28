using DocumentReader.Api.Configuration;
using DocumentReader.Api.Ingestion;
using DocumentReader.Api.Sessions;
using Microsoft.Extensions.Options;

namespace DocumentReader.Tests.Ingestion;

public sealed class ExtractionLimiterTests
{
    [Fact]
    public void MaxConcurrency_IsCappedAtProcessorCount()
    {
        using var limiter = new ExtractionLimiter(Options.Create(new LimitsOptions { MaxParallelExtractions = 64 }));

        Assert.Equal(Math.Min(64, Environment.ProcessorCount), limiter.MaxConcurrency);
    }

    [Fact]
    public async Task StartAsync_NeverRunsMoreThanTheLimit()
    {
        using var limiter = new ExtractionLimiter(Options.Create(new LimitsOptions { MaxParallelExtractions = 2 }));
        var active = 0;
        var maxActive = 0;

        var started = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => limiter.StartAsync(
            () =>
            {
                var now = Interlocked.Increment(ref active);
                lock (limiter)
                {
                    maxActive = Math.Max(maxActive, now);
                }

                Thread.Sleep(30);
                Interlocked.Decrement(ref active);
                return 1;
            },
            CancellationToken.None)));
        await Task.WhenAll(started);

        Assert.InRange(maxActive, 1, limiter.MaxConcurrency);
        SpinWait.SpinUntil(() => limiter.ActiveCount == 0, TimeSpan.FromSeconds(5));
        Assert.Equal(0, limiter.ActiveCount);
    }

    [Fact]
    public async Task StartAsync_SlotStaysTakenUntilAbandonedWorkReallyEnds()
    {
        using var limiter = new ExtractionLimiter(Options.Create(new LimitsOptions { MaxParallelExtractions = 1 }));
        using var release = new ManualResetEventSlim();

        var stuck = await limiter.StartAsync(() => release.Wait(TimeSpan.FromSeconds(10)), CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(() => stuck.WaitAsync(TimeSpan.FromMilliseconds(50)));

        Assert.Equal(1, limiter.ActiveCount);
        release.Set();
        await stuck;
        Assert.True(SpinWait.SpinUntil(() => limiter.ActiveCount == 0, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void UploadTracker_CountsBeginAndDisposeOnce()
    {
        var tracker = new UploadTracker();

        var first = tracker.Begin();
        using var second = tracker.Begin();
        Assert.Equal(2, tracker.InProgress);

        first.Dispose();
        first.Dispose();
        Assert.Equal(1, tracker.InProgress);
    }
}
