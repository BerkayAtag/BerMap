using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BerMap.Collector.Services;

namespace BerMap.Collector;

/// <summary>
/// Collector main loop: register → heartbeat → drain the offline queue. Discovery runs
/// are started by <see cref="CollectorDiscoveryScheduler"/>; their batches reach the
/// offline queue drained here when the server is unreachable. Failures back off
/// exponentially and never escape the loop, so the host keeps running while the server
/// is unreachable.
/// </summary>
public class Worker : BackgroundService
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly ICollectorClient _collectorClient;
    private readonly ILogger<Worker> _logger;
    private readonly TimeSpan _heartbeatInterval;

    public Worker(ICollectorClient collectorClient, CollectorOptions options, ILogger<Worker> logger)
    {
        _collectorClient = collectorClient;
        _logger = logger;
        _heartbeatInterval = options.HeartbeatInterval > TimeSpan.Zero ? options.HeartbeatInterval : TimeSpan.FromSeconds(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BerMap Independent Collector service started at: {time}", DateTimeOffset.Now);

        var connectionFailures = 0;
        var drainFailures = 0;
        var nextDrainAt = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var online = false;
            try
            {
                // 1. Register (once; the id is persisted) and send a heartbeat.
                online = await _collectorClient.EnsureRegisteredAsync(stoppingToken)
                    && await _collectorClient.SendHeartbeatAsync(stoppingToken);

                // 2. If online, drain the backlog, backing off independently of the heartbeat.
                if (online && DateTimeOffset.UtcNow >= nextDrainAt)
                {
                    if (await _collectorClient.DrainOfflineQueueAsync(stoppingToken))
                    {
                        drainFailures = 0;
                        nextDrainAt = DateTimeOffset.MinValue;
                    }
                    else
                    {
                        drainFailures++;
                        var drainDelay = Backoff(drainFailures);
                        nextDrainAt = DateTimeOffset.UtcNow + drainDelay;
                        _logger.LogWarning("Offline queue drain failed; next attempt in {Delay}.", drainDelay);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in collector background worker loop");
            }

            connectionFailures = online ? 0 : connectionFailures + 1;
            var delay = online ? _heartbeatInterval : Backoff(connectionFailures);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("BerMap Collector service stopped cleanly.");
    }

    /// <summary>5s, 10s, 20s, ... capped at 5 minutes, with up to 20% jitter.</summary>
    private static TimeSpan Backoff(int failures)
    {
        var exponent = Math.Min(Math.Max(failures - 1, 0), 10);
        var seconds = Math.Min(MinBackoff.TotalSeconds * Math.Pow(2, exponent), MaxBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(seconds * (1 + Random.Shared.NextDouble() * 0.2));
    }
}
