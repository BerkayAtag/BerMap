using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BerMap.Collector.Services;

namespace BerMap.Collector;

public class Worker : BackgroundService
{
    private readonly ICollectorClient _collectorClient;
    private readonly ILogger<Worker> _logger;
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(30);

    public Worker(ICollectorClient collectorClient, ILogger<Worker> logger)
    {
        _collectorClient = collectorClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BerMap Independent Collector service started at: {time}", DateTimeOffset.Now);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Send Heartbeat to Central BerMap Server
                var isOnline = await _collectorClient.SendHeartbeatAsync(stoppingToken);

                // 2. If online, attempt to drain any backlogged offline queue
                if (isOnline)
                {
                    await _collectorClient.DrainOfflineQueueAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in collector background worker loop");
            }

            await Task.Delay(_heartbeatInterval, stoppingToken);
        }

        _logger.LogInformation("BerMap Collector service stopped cleanly.");
    }
}
