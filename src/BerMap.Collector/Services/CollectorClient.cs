using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using BerMap.Contracts;

namespace BerMap.Collector.Services;

public interface ICollectorClient
{
    bool IsRegistered { get; }

    /// <summary>Loads the persisted collector id or registers with the server. Returns false if not registered.</summary>
    Task<bool> EnsureRegisteredAsync(CancellationToken cancellationToken = default);

    Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a batch, or buffers it in the offline queue when the server is unreachable or a backlog
    /// exists (to preserve ordering). All batches of one snapshot pass the same
    /// <paramref name="snapshotId"/>; <paramref name="final"/> marks the batch that completes it.
    /// A single-batch snapshot passes final=true and needs no snapshot id.
    /// </summary>
    Task<bool> SubmitDiscoveryBatchAsync(DiscoveryResult batch, Guid? snapshotId = null, bool final = false, CancellationToken cancellationToken = default);

    /// <summary>Delivers buffered batches in order. Returns false when a delivery failed and should be retried later.</summary>
    Task<bool> DrainOfflineQueueAsync(CancellationToken cancellationToken = default);
}

/// <summary>Offline queue payload: the batch plus the submit-batch query parameters.</summary>
public sealed class QueuedBatch
{
    public DiscoveryResult Batch { get; set; } = new();

    /// <summary>Server job id, when already known at enqueue time.</summary>
    public Guid? JobId { get; set; }

    /// <summary>
    /// Client-side id shared by all batches of one snapshot. The server's job
    /// id is only known after the first batch is delivered (which may happen
    /// later, from the offline queue), so later batches are linked through it.
    /// </summary>
    public Guid? SnapshotId { get; set; }

    public bool Final { get; set; }
}

public class CollectorClient : ICollectorClient
{
    public const string BatchQueue = "discovery_batches";

    private readonly HttpClient _httpClient;
    private readonly IOfflineQueue _offlineQueue;
    private readonly ILogger<CollectorClient> _logger;
    private readonly CollectorOptions _options;
    private readonly string _hostname;
    private readonly string _version;
    private readonly SemaphoreSlim _registrationLock = new(1, 1);
    private Guid? _collectorId;

    // Server job id of each open multi-batch snapshot, learned from the
    // response to its first delivered batch.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid> _snapshotJobs = new();

    // CPU sampling state: process CPU time and wall clock at the previous heartbeat.
    private TimeSpan? _lastCpuTime;
    private long _lastCpuSampleTimestamp;

    private enum DeliveryOutcome { Accepted, Transient, Rejected }

    public CollectorClient(
        HttpClient httpClient,
        IOfflineQueue offlineQueue,
        ILogger<CollectorClient> logger,
        CollectorOptions options)
    {
        _httpClient = httpClient;
        _offlineQueue = offlineQueue;
        _logger = logger;
        _options = options;
        _hostname = Environment.MachineName;
        _version = typeof(CollectorClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? "1.0.0";
        _collectorId = LoadPersistedId();
    }

    public bool IsRegistered => _collectorId.HasValue;

    public async Task<bool> EnsureRegisteredAsync(CancellationToken cancellationToken = default)
    {
        if (_collectorId.HasValue)
            return true;

        await _registrationLock.WaitAsync(cancellationToken);
        try
        {
            if (_collectorId.HasValue)
                return true;

            if (string.IsNullOrWhiteSpace(_options.EnrollmentToken))
            {
                _logger.LogError("BerMap:Collector:EnrollmentToken is not configured; the collector cannot register.");
                return false;
            }

            var request = new
            {
                name = _options.Name,
                hostname = _hostname,
                ipAddress = GetPrimaryIpAddress(),
                version = _version,
                supportedAdapters = _options.SupportedAdapters
            };

            using var response = await _httpClient.PostAsJsonAsync("/api/collectors/register", request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Collector registration returned {StatusCode}.", (int)response.StatusCode);
                return false;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = doc.RootElement;
            var registered = root.TryGetProperty("registered", out var r) && r.ValueKind == JsonValueKind.True;
            if (!registered ||
                !root.TryGetProperty("collectorId", out var idElement) ||
                !Guid.TryParse(idElement.GetString(), out var collectorId) ||
                collectorId == Guid.Empty)
            {
                _logger.LogWarning("Collector registration response did not contain a collector id.");
                return false;
            }

            PersistId(collectorId);
            _collectorId = collectorId;
            _logger.LogInformation("Collector registered with id {CollectorId}.", collectorId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Collector registration failed (server unreachable?): {Message}", ex.Message);
            return false;
        }
        finally
        {
            _registrationLock.Release();
        }
    }

    public async Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        if (_collectorId is not { } collectorId)
            return false;

        var heartbeat = new CollectorHeartbeat
        {
            CollectorId = collectorId,
            Hostname = _hostname,
            Version = _version,
            CpuUsagePercent = GetCurrentCpuUsage(),
            MemoryUsagePercent = GetCurrentMemoryUsage(),
            QueuedMessagesCount = _offlineQueue.GetQueueDepth(BatchQueue),
            Timestamp = DateTime.UtcNow
        };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/api/collectors/heartbeat", heartbeat, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Heartbeat sent successfully from {Hostname}", _hostname);
                return true;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // The server no longer knows this id (e.g. collector deleted): register again.
                _logger.LogWarning("Server does not recognise collector {CollectorId}; re-registering.", collectorId);
                ForgetId();
                return false;
            }

            _logger.LogWarning("Heartbeat response code: {StatusCode}", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Heartbeat failed (Server unreachable, operating offline): {Message}", ex.Message);
            return false;
        }
    }

    public async Task<bool> SubmitDiscoveryBatchAsync(DiscoveryResult batch, Guid? snapshotId = null, bool final = false, CancellationToken cancellationToken = default)
    {
        var queued = new QueuedBatch { Batch = batch, SnapshotId = snapshotId, Final = final };

        // Never overtake buffered batches: a later batch must not be ingested before an earlier one.
        if (_offlineQueue.GetQueueDepth(BatchQueue) > 0)
        {
            await BufferAsync(queued, "older batches are still buffered");
            return false;
        }

        DeliveryOutcome outcome;
        try
        {
            outcome = await PostBatchAsync(queued, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Network failure during batch submit ({Message}).", ex.Message);
            outcome = DeliveryOutcome.Transient;
        }

        switch (outcome)
        {
            case DeliveryOutcome.Accepted:
                _logger.LogInformation("Discovered batch with {NodeCount} nodes submitted successfully", batch.Nodes.Count);
                return true;
            case DeliveryOutcome.Rejected:
                _logger.LogError("Server permanently rejected a discovery batch with {NodeCount} nodes; it was not buffered.", batch.Nodes.Count);
                return false;
            default:
                await BufferAsync(queued, "server unavailable");
                return false;
        }
    }

    public async Task<bool> DrainOfflineQueueAsync(CancellationToken cancellationToken = default)
    {
        var depth = _offlineQueue.GetQueueDepth(BatchQueue);
        if (depth == 0)
            return true;

        _logger.LogInformation("Draining offline queue ({Depth} batches pending)...", depth);

        while (!cancellationToken.IsCancellationRequested)
        {
            var item = await _offlineQueue.DequeueAsync<QueuedBatch>(BatchQueue, cancellationToken);
            if (item == null)
                return true;

            if (item.Payload.Batch == null)
            {
                await _offlineQueue.DeadLetterAsync(BatchQueue, item.Id, "no batch payload", CancellationToken.None);
                continue;
            }

            DeliveryOutcome outcome;
            try
            {
                outcome = await PostBatchAsync(item.Payload, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown: give the lease back without counting it as a delivery failure.
                await _offlineQueue.ReleaseAsync(BatchQueue, item.Id, countAsFailure: false, CancellationToken.None);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Backlog batch {Id} delivery failed: {Message}", item.Id, ex.Message);
                outcome = DeliveryOutcome.Transient;
            }

            // Local bookkeeping must complete even if shutdown was requested meanwhile.
            switch (outcome)
            {
                case DeliveryOutcome.Accepted:
                    await _offlineQueue.AckAsync(BatchQueue, item.Id, CancellationToken.None);
                    _logger.LogInformation("Successfully drained backlog batch {Id}", item.Id);
                    break;
                case DeliveryOutcome.Rejected:
                    await _offlineQueue.DeadLetterAsync(BatchQueue, item.Id, "rejected by server", CancellationToken.None);
                    _logger.LogError("Backlog batch {Id} was rejected by the server and moved to dead-letter.", item.Id);
                    break;
                default:
                    var requeued = await _offlineQueue.ReleaseAsync(BatchQueue, item.Id, countAsFailure: true, CancellationToken.None);
                    if (!requeued)
                        _logger.LogError("Backlog batch {Id} exceeded the retry limit and was moved to dead-letter.", item.Id);
                    return false;
            }
        }

        return false;
    }

    private async Task<DeliveryOutcome> PostBatchAsync(QueuedBatch queued, CancellationToken cancellationToken)
    {
        var jobId = queued.JobId;
        if (jobId == null && queued.SnapshotId is { } pendingSnapshot &&
            _snapshotJobs.TryGetValue(pendingSnapshot, out var knownJob))
            jobId = knownJob;

        var query = $"?final={(queued.Final ? "true" : "false")}";
        if (jobId.HasValue)
            query += $"&jobId={jobId.Value:D}";

        using var response = await _httpClient.PostAsJsonAsync("/api/collectors/submit-batch" + query, queued.Batch, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            if (queued.SnapshotId is { } snapshotId)
            {
                if (queued.Final)
                {
                    _snapshotJobs.TryRemove(snapshotId, out _);
                }
                else if (jobId == null && await ReadJobIdAsync(response, cancellationToken) is { } openedJob)
                {
                    _snapshotJobs[snapshotId] = openedJob;
                }
            }

            return DeliveryOutcome.Accepted;
        }

        var status = (int)response.StatusCode;
        _logger.LogWarning("Batch submission returned {StatusCode}.", status);

        // 401/403 are configuration problems (token) and 408/429 are throttling: keep the batch.
        // Any other 4xx means the server will never accept this payload.
        if (status is >= 400 and < 500 and not (401 or 403 or 408 or 429))
            return DeliveryOutcome.Rejected;
        return DeliveryOutcome.Transient;
    }

    private static async Task<Guid?> ReadJobIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return doc.RootElement.TryGetProperty("jobId", out var id) && Guid.TryParse(id.GetString(), out var jobId)
                ? jobId
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task BufferAsync(QueuedBatch queued, string reason)
    {
        // Buffering is local bookkeeping and must not be skipped because of cancellation.
        var accepted = await _offlineQueue.EnqueueAsync(BatchQueue, queued, CancellationToken.None);
        if (accepted)
            _logger.LogWarning("Discovery batch buffered to local offline queue ({Reason}).", reason);
        else
            _logger.LogError("Discovery batch dropped: offline queue disk quota exceeded.");
    }

    // ---------------------------------------------------------------------
    // Identity persistence
    // ---------------------------------------------------------------------

    private Guid? LoadPersistedId()
    {
        try
        {
            if (File.Exists(_options.CollectorIdFile) &&
                Guid.TryParse(File.ReadAllText(_options.CollectorIdFile).Trim(), out var id) &&
                id != Guid.Empty)
                return id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not read collector id file {File}: {Message}", _options.CollectorIdFile, ex.Message);
        }
        return null;
    }

    private void PersistId(Guid id)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        var temp = _options.CollectorIdFile + ".tmp";
        File.WriteAllText(temp, id.ToString("D"));
        File.Move(temp, _options.CollectorIdFile, overwrite: true);
    }

    private void ForgetId()
    {
        _collectorId = null;
        try
        {
            File.Delete(_options.CollectorIdFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete collector id file: {Message}", ex.Message);
        }
    }

    // ---------------------------------------------------------------------
    // Host metrics
    // ---------------------------------------------------------------------

    /// <summary>Process CPU % since the previous sample (or process start), normalised to all cores.</summary>
    private double GetCurrentCpuUsage()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var cpuTime = process.TotalProcessorTime;
            var now = Stopwatch.GetTimestamp();

            TimeSpan cpuDelta;
            TimeSpan wallDelta;
            if (_lastCpuTime is { } lastCpu)
            {
                cpuDelta = cpuTime - lastCpu;
                wallDelta = Stopwatch.GetElapsedTime(_lastCpuSampleTimestamp, now);
            }
            else
            {
                cpuDelta = cpuTime;
                wallDelta = DateTime.Now - process.StartTime;
            }

            _lastCpuTime = cpuTime;
            _lastCpuSampleTimestamp = now;

            if (wallDelta <= TimeSpan.Zero)
                return 0;
            var percent = 100.0 * cpuDelta.TotalMilliseconds / (wallDelta.TotalMilliseconds * Environment.ProcessorCount);
            return Math.Round(Math.Clamp(percent, 0, 100), 1);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Process working set as a percentage of the memory available to the runtime.</summary>
    private static double GetCurrentMemoryUsage()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (available <= 0)
                return 0;
            return Math.Round(Math.Clamp(100.0 * process.WorkingSet64 / available, 0, 100), 1);
        }
        catch
        {
            return 0;
        }
    }

    private static string? GetPrimaryIpAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                ?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
