using Microsoft.Extensions.Options;

namespace DocumentReader.Api.Sessions;

/// <summary>
/// Periodically removes expired sessions so idle sessions do not keep their data in memory.
/// </summary>
public sealed class SessionCleanupService(
    ISessionStore sessionStore,
    IOptions<SessionLifetimeOptions> options,
    TimeProvider timeProvider,
    ILogger<SessionCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CleanupInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                RemoveExpiredSessions();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void RemoveExpiredSessions()
    {
        try
        {
            var removed = sessionStore.RemoveExpiredSessions();
            if (removed > 0)
            {
                logger.LogInformation("Removed {RemovedCount} expired session(s).", removed);
            }
        }
        catch (Exception exception)
        {
            // Keep the cleanup loop alive; a failed pass is retried on the next tick.
            logger.LogError(exception, "Expired session cleanup failed.");
        }
    }
}
