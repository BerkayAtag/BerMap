using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using BerMap.Contracts;

namespace BerMap.Collector.Services;

public interface ICollectorClient
{
    Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken = default);
    Task<bool> SubmitDiscoveryBatchAsync(DiscoveryResult batch, CancellationToken cancellationToken = default);
    Task DrainOfflineQueueAsync(CancellationToken cancellationToken = default);
}

public class CollectorClient : ICollectorClient
{
    private readonly HttpClient _httpClient;
    private readonly IOfflineQueue _offlineQueue;
    private readonly ILogger<CollectorClient> _logger;
    private readonly Guid _collectorId;
    private readonly string _hostname;

    public CollectorClient(
        HttpClient httpClient,
        IOfflineQueue offlineQueue,
        ILogger<CollectorClient> logger,
        Guid? collectorId = null)
    {
        _httpClient = httpClient;
        _offlineQueue = offlineQueue;
        _logger = logger;
        _collectorId = collectorId ?? Guid.Parse("99999999-9999-9999-9999-999999999999");
        _hostname = Environment.MachineName;
    }

    public async Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var heartbeat = new CollectorHeartbeat
        {
            CollectorId = _collectorId,
            Hostname = _hostname,
            Version = "1.0.0",
            CpuUsagePercent = GetCurrentCpuUsage(),
            MemoryUsagePercent = GetCurrentMemoryUsage(),
            QueuedMessagesCount = _offlineQueue.GetQueueDepth("discovery_batches"),
            Timestamp = DateTime.UtcNow
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/collectors/heartbeat", heartbeat, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Heartbeat sent successfully from {Hostname}", _hostname);
                return true;
            }

            _logger.LogWarning("Heartbeat response code: {StatusCode}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Heartbeat failed (Server unreachable, operating offline): {Message}", ex.Message);
            return false;
        }
    }

    public async Task<bool> SubmitDiscoveryBatchAsync(DiscoveryResult batch, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/collectors/submit-batch", batch, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Discovered batch with {NodeCount} nodes submitted successfully", batch.Nodes.Count);
                return true;
            }

            _logger.LogWarning("Batch submission returned {StatusCode}. Buffering to local offline queue.", response.StatusCode);
            await _offlineQueue.EnqueueAsync("discovery_batches", batch, cancellationToken);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Network failure during batch submit ({Message}). Buffering to local offline queue.", ex.Message);
            await _offlineQueue.EnqueueAsync("discovery_batches", batch, cancellationToken);
            return false;
        }
    }

    public async Task DrainOfflineQueueAsync(CancellationToken cancellationToken = default)
    {
        var depth = _offlineQueue.GetQueueDepth("discovery_batches");
        if (depth == 0) return;

        _logger.LogInformation("Draining offline queue ({Depth} batches pending)...", depth);

        while (!cancellationToken.IsCancellationRequested)
        {
            var item = await _offlineQueue.DequeueAsync<DiscoveryResult>("discovery_batches", cancellationToken);
            if (item == null) break;

            try
            {
                var response = await _httpClient.PostAsJsonAsync("/api/collectors/submit-batch", item.Payload, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    // Re-enqueue if server rejected or is still offline
                    await _offlineQueue.EnqueueAsync("discovery_batches", item.Payload, cancellationToken);
                    break;
                }

                _logger.LogInformation("Successfully drained backlog batch {Id}", item.Id);
            }
            catch
            {
                // Re-enqueue and stop draining until next cycle
                await _offlineQueue.EnqueueAsync("discovery_batches", item.Payload, cancellationToken);
                break;
            }
        }
    }

    private static double GetCurrentCpuUsage()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            return Math.Round(process.TotalProcessorTime.TotalMilliseconds / (Environment.ProcessorCount * 1000.0) % 100, 1);
        }
        catch
        {
            return 12.5;
        }
    }

    private static double GetCurrentMemoryUsage()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            return Math.Round(process.WorkingSet64 / (1024.0 * 1024.0), 1);
        }
        catch
        {
            return 85.0;
        }
    }
}
