namespace DocumentReader.Api.Sessions;

/// <summary>
/// Counts the upload requests of one session that are still being processed. Thread-safe; several
/// uploads may run at the same time.
/// </summary>
public sealed class UploadTracker
{
    private int _inProgress;

    public int InProgress => Volatile.Read(ref _inProgress);

    /// <summary>Marks an upload as started; disposing the result marks it as finished (exactly once).</summary>
    public IDisposable Begin()
    {
        Interlocked.Increment(ref _inProgress);
        return new UploadScope(this);
    }

    private sealed class UploadScope(UploadTracker tracker) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref tracker._inProgress);
            }
        }
    }
}
