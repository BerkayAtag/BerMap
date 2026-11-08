using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Sends batch <paramref name="sequence"/> (0-based) of a snapshot, or buffers it. The batches
    /// of a snapshot reach the server in sequence or not at all: when one is lost (refused by the
    /// server, evicted from the offline queue, dead-lettered, or missing when a later one is due)
    /// the snapshot's remaining batches are dropped and its server job is closed with a failure
    /// report (COLLECTOR_BATCH_REJECTED / COLLECTOR_BATCH_LOST), sent now or once the server is
    /// reachable again. An incomplete snapshot therefore never completes and never marks the
    /// integration's inventory stale. Once <paramref name="cancellationToken"/> is cancelled
    /// (service stopping) the batch is buffered instead of sent, and an upload in progress is
    /// abandoned and buffered, so the snapshot is finished after the restart.
    /// </summary>
    Task<BatchDelivery> SubmitSnapshotBatchAsync(DiscoveryResult batch, Guid snapshotId, int sequence, bool final, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends pending failure reports, then delivers buffered batches in order. Returns false when a
    /// delivery failed and should be retried later.
    /// </summary>
    Task<bool> DrainOfflineQueueAsync(CancellationToken cancellationToken = default);
}

/// <summary>What happened to a submitted batch.</summary>
public enum BatchDelivery
{
    /// <summary>The server accepted it.</summary>
    Delivered,

    /// <summary>Kept in the offline queue; it is delivered in order later.</summary>
    Buffered,

    /// <summary>
    /// The server (or the snapshot protocol) refused it for good; it was not kept. The rest of its
    /// snapshot is not sent either: the snapshot's server job is closed with a failure report.
    /// </summary>
    Rejected
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

    /// <summary>
    /// 0-based position inside the snapshot; null for batches queued before
    /// sequences existed (or sent through the sequence-less overload).
    /// </summary>
    public int? Sequence { get; set; }
}

public class CollectorClient : ICollectorClient
{
    public const string BatchQueue = "discovery_batches";

    /// <summary>Server-side request paths, relative to the base address so a path prefix is kept.</summary>
    public const string RegisterPath = "api/collectors/register";
    public const string HeartbeatPath = "api/collectors/heartbeat";
    public const string SubmitBatchPath = "api/collectors/submit-batch";

    /// <summary>Error codes of the failure report that closes an incomplete snapshot.</summary>
    public const string BatchLostCode = "COLLECTOR_BATCH_LOST";
    public const string BatchRejectedCode = "COLLECTOR_BATCH_REJECTED";

    /// <summary>Open snapshots older than this are forgotten (the server has failed their jobs long ago).</summary>
    private static readonly TimeSpan SnapshotJobRetention = TimeSpan.FromDays(7);

    /// <summary>Closed snapshots are remembered this long, so a batch delivered twice (a crash before its ack) is recognised.</summary>
    private static readonly TimeSpan ClosedSnapshotRetention = TimeSpan.FromDays(1);

    private readonly HttpClient _httpClient;
    private readonly IOfflineQueue _offlineQueue;
    private readonly ILogger<CollectorClient> _logger;
    private readonly CollectorOptions _options;
    private readonly string _hostname;
    private readonly string _version;
    private readonly SemaphoreSlim _registrationLock = new(1, 1);
    private readonly SemaphoreSlim _reportLock = new(1, 1);
    private Guid? _collectorId;

    // State of each multi-batch snapshot: the server job its first delivered
    // batch opened, the next sequence due, and whether it was lost. Persisted
    // (snapshot-jobs.json), so batches still in the offline queue reach the
    // right job, in sequence, after a service restart.
    private readonly Dictionary<Guid, SnapshotState> _snapshots = new();
    private readonly object _snapshotLock = new();

    // CPU sampling state: process CPU time and wall clock at the previous heartbeat.
    private TimeSpan? _lastCpuTime;
    private long _lastCpuSampleTimestamp;

    private enum DeliveryOutcome
    {
        Accepted,
        Transient,
        Rejected,

        /// <summary>Not sent and never will be: its snapshot is lost (or it was already delivered).</summary>
        Dropped
    }

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
        LoadSnapshotJobs();
        _offlineQueue.ItemsDiscarded += OnQueueItemsDiscarded;
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

            using var response = await _httpClient.PostAsJsonAsync(RegisterPath, request, cancellationToken);
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
            using var response = await _httpClient.PostAsJsonAsync(HeartbeatPath, heartbeat, cancellationToken);
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

    public async Task<bool> SubmitDiscoveryBatchAsync(DiscoveryResult batch, Guid? snapshotId = null, bool final = false, CancellationToken cancellationToken = default) =>
        await SubmitAsync(new QueuedBatch { Batch = batch, SnapshotId = snapshotId, Final = final }, cancellationToken) == BatchDelivery.Delivered;

    public Task<BatchDelivery> SubmitSnapshotBatchAsync(DiscoveryResult batch, Guid snapshotId, int sequence, bool final, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        return SubmitAsync(new QueuedBatch { Batch = batch, SnapshotId = snapshotId, Final = final, Sequence = sequence }, cancellationToken);
    }

    private async Task<BatchDelivery> SubmitAsync(QueuedBatch queued, CancellationToken cancellationToken)
    {
        var batch = queued.Batch;

        if (IsSequenced(queued, out var snapshotId, out var sequence) && LostReason(snapshotId) is { } lost)
        {
            _logger.LogWarning("Batch {Batch} of snapshot {SnapshotId} is not sent: the snapshot is incomplete ({Reason}).",
                sequence + 1, snapshotId, lost);
            return BatchDelivery.Rejected;
        }

        // The service is stopping: keep the batch for the next start instead of losing it.
        if (cancellationToken.IsCancellationRequested)
            return await BufferAsync(queued, "the service is stopping");

        // Never overtake buffered batches: a later batch must not be ingested before an earlier one.
        if (_offlineQueue.GetQueueDepth(BatchQueue) > 0)
            return await BufferAsync(queued, "older batches are still buffered");

        DeliveryOutcome outcome;
        try
        {
            outcome = await PostBatchAsync(queued, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping mid-upload: the server either never got the body or ingested it; the
            // batch is sent again after the restart (the server upserts by external id).
            return await BufferAsync(queued, "the service is stopping");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Network failure during batch submit ({Message}).", ex.Message);
            outcome = DeliveryOutcome.Transient;
        }

        switch (outcome)
        {
            case DeliveryOutcome.Accepted:
                _logger.LogInformation("Discovered batch with {NodeCount} nodes and {RelationshipCount} relationships submitted successfully",
                    batch.Nodes.Count, batch.Relationships.Count);
                return BatchDelivery.Delivered;
            case DeliveryOutcome.Rejected:
            case DeliveryOutcome.Dropped:
                _logger.LogError("Discovery batch with {NodeCount} nodes was not accepted; it was not buffered.", batch.Nodes.Count);
                // The server answered, so the failure report closing the snapshot can go out now.
                await ReportLostSnapshotsAsync(CancellationToken.None);
                return BatchDelivery.Rejected;
            default:
                return await BufferAsync(queued, "server unavailable");
        }
    }

    public async Task<bool> DrainOfflineQueueAsync(CancellationToken cancellationToken = default)
    {
        // Snapshots lost while the server was unreachable are closed first.
        if (!await ReportLostSnapshotsAsync(cancellationToken))
            return false;

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
                // Its snapshot (if any) is caught by the sequence check of its next batch.
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
                case DeliveryOutcome.Dropped:
                    await _offlineQueue.AckAsync(BatchQueue, item.Id, CancellationToken.None);
                    await ReportLostSnapshotsAsync(CancellationToken.None);
                    break;
                case DeliveryOutcome.Rejected:
                    await _offlineQueue.DeadLetterAsync(BatchQueue, item.Id, "rejected by server", CancellationToken.None);
                    _logger.LogError("Backlog batch {Id} was rejected by the server and moved to dead-letter.", item.Id);
                    await ReportLostSnapshotsAsync(CancellationToken.None);
                    break;
                default:
                    var requeued = await _offlineQueue.ReleaseAsync(BatchQueue, item.Id, countAsFailure: true, CancellationToken.None);
                    if (!requeued)
                    {
                        _logger.LogError("Backlog batch {Id} exceeded the retry limit and was moved to dead-letter.", item.Id);
                        if (IsSequenced(item.Payload, out var snapshotId, out var sequence))
                        {
                            MarkLost(snapshotId, item.Payload.Batch.IntegrationId,
                                $"{BatchLostCode}: Batch {sequence + 1} of the snapshot could not be delivered and was moved to the offline queue's dead-letter folder; the snapshot is incomplete and was not applied.");
                        }
                    }
                    return false;
            }
        }

        return false;
    }

    private async Task<DeliveryOutcome> PostBatchAsync(QueuedBatch queued, CancellationToken cancellationToken)
    {
        var sequenced = IsSequenced(queued, out var snapshotId, out var sequence);
        var state = queued.SnapshotId is { } knownSnapshot ? GetSnapshot(knownSnapshot) : null;
        var isFailureReport = queued.Final && !queued.Batch.Success;

        if (sequenced)
        {
            if (state?.Lost != null)
            {
                _logger.LogWarning("Batch {Batch} of snapshot {SnapshotId} is dropped: the snapshot is incomplete ({Reason}).",
                    sequence + 1, snapshotId, state.Lost);
                return DeliveryOutcome.Dropped;
            }
            if (state?.Closed == true)
            {
                _logger.LogInformation("Batch {Batch} of snapshot {SnapshotId} was already delivered; it is dropped.", sequence + 1, snapshotId);
                return DeliveryOutcome.Dropped;
            }

            // A snapshot nobody knows starts at 0; entries saved before sequences were tracked accept any.
            int? expected = state == null ? 0 : state.NextSequence;
            if (expected is { } next && sequence < next)
            {
                // Delivered before a crash, but not yet acknowledged in the queue.
                _logger.LogInformation("Batch {Batch} of snapshot {SnapshotId} was already delivered; it is dropped.", sequence + 1, snapshotId);
                return DeliveryOutcome.Dropped;
            }
            if (expected is { } due && sequence > due)
            {
                MarkLost(snapshotId, queued.Batch.IntegrationId, due == sequence - 1
                    ? $"{BatchLostCode}: Batch {due + 1} of the snapshot never reached the server (evicted from the offline queue or abandoned); the snapshot is incomplete and was not applied."
                    : $"{BatchLostCode}: Batches {due + 1}-{sequence} of the snapshot never reached the server (evicted from the offline queue or abandoned); the snapshot is incomplete and was not applied.");
                return DeliveryOutcome.Dropped;
            }
            if (sequence > 0 && queued.JobId == null && state?.JobId == null)
            {
                // The first batch was delivered, but its job id is unknown: on its own this batch
                // would open a new job, and a final batch would then mark the rest of the
                // integration's inventory stale.
                MarkLost(snapshotId, queued.Batch.IntegrationId,
                    $"{BatchLostCode}: Batch {sequence + 1} of the snapshot has no server job to join; the snapshot is incomplete and was not applied.");
                return DeliveryOutcome.Dropped;
            }
        }

        var jobId = queued.JobId ?? state?.JobId;
        var query = $"?final={(queued.Final ? "true" : "false")}";
        if (jobId.HasValue)
            query += $"&jobId={jobId.Value:D}";

        using var response = await _httpClient.PostAsJsonAsync(SubmitBatchPath + query, queued.Batch, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            if (queued.SnapshotId is { } delivered)
            {
                var openedJob = jobId ?? (queued.Final ? null : await ReadJobIdAsync(response, CancellationToken.None));
                RecordDelivery(delivered, queued.Batch.IntegrationId, openedJob, queued.Sequence, queued.Final);
            }
            return DeliveryOutcome.Accepted;
        }

        var status = (int)response.StatusCode;
        var detail = await ReadErrorAsync(response);
        _logger.LogWarning("Batch submission returned {StatusCode}: {Detail}", status, detail);

        // 401/403 are configuration problems (token) and 408/429 are throttling: keep the batch.
        // Any other 4xx (413: body over the server's limit included) means the server will never
        // accept this payload.
        if (status is >= 400 and < 500 and not (401 or 403 or 408 or 429))
        {
            if (sequenced && !isFailureReport)
            {
                MarkLost(snapshotId, queued.Batch.IntegrationId,
                    $"{BatchRejectedCode}: The server refused batch {sequence + 1} of the snapshot ({status}{(detail.Length > 0 ? ": " + detail : string.Empty)}); the snapshot is incomplete and was not applied.");
            }
            else if (sequenced)
            {
                // A refused failure report cannot be reported again.
                UpdateSnapshot(snapshotId, s => (s ?? NewState(queued.Batch.IntegrationId)) with { Closed = true, RecordedAt = DateTime.UtcNow });
            }
            return DeliveryOutcome.Rejected;
        }
        return DeliveryOutcome.Transient;
    }

    private static bool IsSequenced(QueuedBatch queued, out Guid snapshotId, out int sequence)
    {
        snapshotId = queued.SnapshotId ?? Guid.Empty;
        sequence = queued.Sequence ?? -1;
        return queued.SnapshotId.HasValue && queued.Sequence is >= 0;
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

    /// <summary>The server's message (problem details "detail"/"title" or { message }), shortened.</summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            foreach (var name in new[] { "message", "detail", "title" })
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    text = value.GetString() ?? string.Empty;
                    break;
                }
            }
        }
        catch (JsonException)
        {
            // Plain text body.
        }

        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length > 200 ? text[..200] + "..." : text;
    }

    private async Task<BatchDelivery> BufferAsync(QueuedBatch queued, string reason)
    {
        var sequenced = IsSequenced(queued, out var snapshotId, out var sequence);

        // Buffering is local bookkeeping and must not be skipped because of cancellation.
        var accepted = sequenced
            ? await _offlineQueue.EnqueueAsync(BatchQueue, queued, QueueTag(queued.Batch.IntegrationId, snapshotId, sequence), CancellationToken.None)
            : await _offlineQueue.EnqueueAsync(BatchQueue, queued, CancellationToken.None);
        if (!accepted)
        {
            _logger.LogError("Discovery batch dropped: offline queue disk quota exceeded.");
            if (sequenced)
            {
                MarkLost(snapshotId, queued.Batch.IntegrationId,
                    $"{BatchLostCode}: Batch {sequence + 1} of the snapshot did not fit into the offline queue's disk quota ({_options.QueueMaxDiskMb} MB); the snapshot is incomplete and was not applied.");
            }
            return BatchDelivery.Rejected;
        }

        _logger.LogWarning("Discovery batch buffered to local offline queue ({Reason}).", reason);
        // Making room for it may have evicted an earlier batch of the same snapshot.
        return sequenced && LostReason(snapshotId) != null ? BatchDelivery.Rejected : BatchDelivery.Buffered;
    }

    // ---------------------------------------------------------------------
    // Snapshot state: job id, next sequence, losses (snapshot-jobs.json)
    // ---------------------------------------------------------------------

    private sealed record SnapshotState
    {
        public Guid IntegrationId { get; init; }

        /// <summary>Server job opened by the first delivered batch.</summary>
        public Guid? JobId { get; init; }

        /// <summary>Sequence of the next batch due; null in entries saved before sequences were tracked.</summary>
        public int? NextSequence { get; init; }

        public DateTime RecordedAt { get; init; }

        /// <summary>The final batch (or the failure report) was delivered.</summary>
        public bool Closed { get; init; }

        /// <summary>Failure report error of a lost snapshot; null while the snapshot is intact.</summary>
        public string? Lost { get; init; }

        public bool FailureReported { get; init; }
    }

    private static SnapshotState NewState(Guid? integrationId) => new()
    {
        IntegrationId = integrationId ?? Guid.Empty,
        NextSequence = 0,
        RecordedAt = DateTime.UtcNow
    };

    /// <summary>"{integration}-{snapshot}-{sequence}": what a discarded queue item belonged to.</summary>
    private static string QueueTag(Guid? integrationId, Guid snapshotId, int sequence) =>
        $"{integrationId ?? Guid.Empty:N}-{snapshotId:N}-{sequence}";

    private void OnQueueItemsDiscarded(object? sender, DiscardedQueueItem item)
    {
        if (item.QueueName != BatchQueue || item.Tag is not { } tag)
            return;

        var parts = tag.Split('-');
        if (parts.Length != 3 ||
            !Guid.TryParseExact(parts[0], "N", out var integrationId) ||
            !Guid.TryParseExact(parts[1], "N", out var snapshotId) ||
            !int.TryParse(parts[2], out var sequence))
            return;

        MarkLost(snapshotId, integrationId == Guid.Empty ? null : integrationId,
            $"{BatchLostCode}: Batch {sequence + 1} of the snapshot was lost from the offline queue ({item.Reason}) while the server was unreachable; the snapshot is incomplete and was not applied.");
    }

    private SnapshotState? GetSnapshot(Guid snapshotId)
    {
        lock (_snapshotLock)
            return _snapshots.GetValueOrDefault(snapshotId);
    }

    private string? LostReason(Guid snapshotId) => GetSnapshot(snapshotId)?.Lost;

    /// <summary>Applies <paramref name="update"/> (null removes the entry) and saves the file.</summary>
    private void UpdateSnapshot(Guid snapshotId, Func<SnapshotState?, SnapshotState?> update)
    {
        lock (_snapshotLock)
        {
            var updated = update(_snapshots.GetValueOrDefault(snapshotId));
            if (updated == null)
                _snapshots.Remove(snapshotId);
            else
                _snapshots[snapshotId] = updated;
            SaveSnapshotJobs();
        }
    }

    private void RecordDelivery(Guid snapshotId, Guid? integrationId, Guid? jobId, int? sequence, bool final)
    {
        UpdateSnapshot(snapshotId, current =>
        {
            var state = current ?? NewState(integrationId);
            return state with
            {
                IntegrationId = state.IntegrationId == Guid.Empty ? integrationId ?? Guid.Empty : state.IntegrationId,
                JobId = state.JobId ?? jobId,
                NextSequence = sequence is { } delivered ? delivered + 1 : state.NextSequence,
                Closed = state.Closed || final,
                RecordedAt = DateTime.UtcNow
            };
        });
    }

    /// <summary>
    /// Marks a snapshot incomplete: its remaining batches are dropped and its job is closed with a
    /// failure report carrying <paramref name="error"/> (see <see cref="ReportLostSnapshotsAsync"/>).
    /// The first loss wins; a closed snapshot is left alone.
    /// </summary>
    private void MarkLost(Guid snapshotId, Guid? integrationId, string error)
    {
        var marked = false;
        UpdateSnapshot(snapshotId, current =>
        {
            var state = current ?? NewState(integrationId);
            if (state.Lost != null || state.Closed)
                return current;
            marked = true;
            return state with
            {
                IntegrationId = state.IntegrationId == Guid.Empty ? integrationId ?? Guid.Empty : state.IntegrationId,
                Lost = error,
                FailureReported = false,
                RecordedAt = DateTime.UtcNow
            };
        });
        if (marked)
        {
            _logger.LogError(
                "Snapshot {SnapshotId} of integration {IntegrationId} is incomplete; its remaining batches are dropped and the server job is closed as failed. {Error}",
                snapshotId, integrationId, error);
        }
    }

    /// <summary>
    /// Sends the failure report of every lost snapshot not reported yet: a final batch with
    /// Success=false to the snapshot's job (or, when no batch reached the server, on a new job),
    /// so the job fails, the integration shows the error and nothing is marked stale. Returns
    /// false when the server could not be reached; the reports are retried on the next drain.
    /// </summary>
    private async Task<bool> ReportLostSnapshotsAsync(CancellationToken cancellationToken)
    {
        List<KeyValuePair<Guid, SnapshotState>> pending;
        lock (_snapshotLock)
            pending = _snapshots.Where(p => p.Value.Lost != null && !p.Value.FailureReported).ToList();
        if (pending.Count == 0)
            return true;

        await _reportLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var (snapshotId, state) in pending)
            {
                if (GetSnapshot(snapshotId) is not { Lost: not null, FailureReported: false })
                    continue;

                if (state.IntegrationId == Guid.Empty)
                {
                    _logger.LogError("Snapshot {SnapshotId} is incomplete but its integration is unknown; no failure report is sent.", snapshotId);
                    UpdateSnapshot(snapshotId, s => s == null ? null : s with { FailureReported = true });
                    continue;
                }

                var report = CollectorDiscoveryRunner.BuildFailureBatch(state.IntegrationId, new[] { state.Lost! });
                var query = "?final=true" + (state.JobId is { } jobId ? $"&jobId={jobId:D}" : string.Empty);
                int status;
                try
                {
                    using var response = await _httpClient.PostAsJsonAsync(SubmitBatchPath + query, report, cancellationToken);
                    status = (int)response.StatusCode;
                    if (!response.IsSuccessStatusCode)
                        _logger.LogWarning("Failure report for snapshot {SnapshotId} returned {StatusCode}: {Detail}", snapshotId, status, await ReadErrorAsync(response));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Failure report for snapshot {SnapshotId} could not be sent ({Message}); it is retried later.", snapshotId, ex.Message);
                    return false;
                }

                if (status is (>= 200 and < 300) or (>= 400 and < 500 and not (401 or 403 or 408 or 429)))
                {
                    // Sent, or refused for good (for example the server already failed the job).
                    UpdateSnapshot(snapshotId, s => s == null ? null : s with { FailureReported = true, RecordedAt = DateTime.UtcNow });
                    if (status < 300)
                        _logger.LogInformation("Reported incomplete snapshot {SnapshotId} of integration {IntegrationId} to the server.", snapshotId, state.IntegrationId);
                    continue;
                }
                return false;
            }
            return true;
        }
        finally
        {
            _reportLock.Release();
        }
    }

    private string SnapshotJobsFile => Path.Combine(_options.DataDirectory, "snapshot-jobs.json");

    private void LoadSnapshotJobs()
    {
        try
        {
            if (!File.Exists(SnapshotJobsFile))
                return;
            var saved = JsonSerializer.Deserialize<Dictionary<Guid, SnapshotState>>(File.ReadAllText(SnapshotJobsFile));
            lock (_snapshotLock)
            {
                foreach (var (snapshotId, state) in saved ?? new())
                {
                    if (state != null && !Expired(state, DateTime.UtcNow))
                        _snapshots[snapshotId] = state;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("Could not read the open snapshot list {File}: {Message}", SnapshotJobsFile, ex.Message);
        }
    }

    private static bool Expired(SnapshotState state, DateTime now)
    {
        // A loss not reported yet is kept until it is.
        if (state.Lost != null && !state.FailureReported)
            return false;
        var retention = state.Closed || state.FailureReported ? ClosedSnapshotRetention : SnapshotJobRetention;
        return state.RecordedAt < now - retention;
    }

    /// <summary>Caller holds <see cref="_snapshotLock"/>.</summary>
    private void SaveSnapshotJobs()
    {
        var now = DateTime.UtcNow;
        foreach (var expired in _snapshots.Where(p => Expired(p.Value, now)).Select(p => p.Key).ToList())
            _snapshots.Remove(expired);

        try
        {
            Directory.CreateDirectory(_options.DataDirectory);
            var temp = SnapshotJobsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_snapshots));
            File.Move(temp, SnapshotJobsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory map still serves this process; only a restart would lose it.
            _logger.LogWarning("Could not save the open snapshot list {File}: {Message}", SnapshotJobsFile, ex.Message);
        }
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
