using DocumentReader.Api.Configuration;
using Microsoft.Extensions.Options;

namespace DocumentReader.Api.Ingestion;

/// <summary>
/// Server-wide bound on how many files are parsed at the same time, shared by all sessions and
/// requests. The effective limit is <see cref="LimitsOptions.MaxParallelExtractions"/>, capped at
/// the number of processors because parsing is CPU-bound.
/// </summary>
public sealed class ExtractionLimiter : IDisposable
{
    private readonly SemaphoreSlim _slots;

    public ExtractionLimiter(IOptions<LimitsOptions> limitsOptions)
    {
        MaxConcurrency = Math.Max(1, Math.Min(limitsOptions.Value.MaxParallelExtractions, Environment.ProcessorCount));
        _slots = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
    }

    public int MaxConcurrency { get; }

    /// <summary>Extractions currently holding a slot.</summary>
    public int ActiveCount => MaxConcurrency - _slots.CurrentCount;

    /// <summary>
    /// Waits for a slot, then starts <paramref name="extraction"/> on the thread pool. The slot is
    /// released when the extraction really ends, even if the caller stopped waiting for it (for
    /// example after a timeout), so abandoned work still counts against the limit.
    /// </summary>
    public async Task<Task<T>> StartAsync<T>(Func<T> extraction, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        Task<T> running;
        try
        {
            running = Task.Run(extraction, CancellationToken.None);
        }
        catch
        {
            _slots.Release();
            throw;
        }

        _ = running.ContinueWith(
            static (_, slots) => ((SemaphoreSlim)slots!).Release(),
            _slots,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return running;
    }

    public void Dispose() => _slots.Dispose();
}
