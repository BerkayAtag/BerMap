namespace BerMap.Worker;

/// <summary>
/// Placeholder host for future server-side background jobs. It currently has no work:
/// discovery jobs run in DiscoveryBackgroundService hosted by BerMap.Api, and remote
/// collection runs in BerMap.Collector.
/// The service logs once and then idles until shutdown (the template's once-per-second
/// log loop was removed).
/// </summary>
public class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("BerMap.Worker started; no background jobs are configured.");
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
