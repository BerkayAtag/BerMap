using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Contracts;
using Microsoft.Extensions.Logging;

namespace BerMap.Collector.Services;

/// <summary>How a collector discovery run ended, as far as the collector can tell.</summary>
public enum DiscoveryRunStatus
{
    /// <summary>
    /// The adapter succeeded and every batch was delivered or buffered (also when the service
    /// started stopping during the upload: the rest is sent from the offline queue).
    /// </summary>
    Completed,

    /// <summary>The adapter returned partial data (Success=false); it was sent.</summary>
    Partial,

    /// <summary>The run failed and a failure batch was sent (or buffered).</summary>
    Failed,

    /// <summary>
    /// A batch was refused or lost; the snapshot is incomplete and its job is closed with a
    /// failure report.
    /// </summary>
    Rejected,

    /// <summary>The service stopped during discovery; nothing was reported.</summary>
    Cancelled
}

public sealed record DiscoveryRunOutcome(DiscoveryRunStatus Status, int Nodes, int Relationships, int Batches);

public interface ICollectorDiscoveryRunner
{
    Task<DiscoveryRunOutcome> RunAsync(CollectorIntegration integration, CancellationToken cancellationToken);
}

/// <summary>
/// One discovery run inside the collector, mirroring the server's
/// DiscoveryJobRunner: read the secret, call the adapter with the
/// integration's timeout, and send the result as one multi-batch snapshot.
/// A failed run is reported too (one final batch, Success=false, no nodes) so
/// the server marks the integration Error instead of showing stale health.
/// </summary>
public sealed class CollectorDiscoveryRunner : ICollectorDiscoveryRunner
{
    private readonly IReadOnlyDictionary<string, IIntegrationAdapter> _adapters;
    private readonly ICollectorClient _client;
    private readonly IIntegrationSecretReader _secretReader;
    private readonly CollectorOptions _options;
    private readonly ILogger<CollectorDiscoveryRunner> _logger;

    public CollectorDiscoveryRunner(
        IEnumerable<IIntegrationAdapter> adapters,
        ICollectorClient client,
        IIntegrationSecretReader secretReader,
        CollectorOptions options,
        ILogger<CollectorDiscoveryRunner> logger)
    {
        _adapters = adapters.ToDictionary(a => a.AdapterType, StringComparer.OrdinalIgnoreCase);
        _client = client;
        _secretReader = secretReader;
        _options = options;
        _logger = logger;
    }

    public async Task<DiscoveryRunOutcome> RunAsync(CollectorIntegration integration, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Discovery started for {AdapterType} integration {IntegrationId}.",
            integration.AdapterType, integration.IntegrationId);

        var stopwatch = Stopwatch.StartNew();
        DiscoveryResult result;
        using var timeoutCts = new CancellationTokenSource(integration.Timeout);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            if (!_adapters.TryGetValue(integration.AdapterType, out var adapter))
                return await ReportFailureAsync(integration, "ADAPTER_NOT_FOUND", $"Adapter '{integration.AdapterType}' is not available in this collector.");

            var secret = await _secretReader.ReadSecretAsync(integration, runCts.Token);
            var context = new AdapterExecutionContext
            {
                IntegrationId = integration.IntegrationId,
                IntegrationName = $"{integration.AdapterType} {integration.IntegrationId}",
                ConfigurationJson = integration.ConfigurationJson,
                Secret = secret,
                ConnectionTimeout = TimeSpan.FromSeconds(30),
                OperationTimeout = integration.Timeout,
                MaxConcurrency = Math.Max(1, adapter.GetCapabilities().MaxConcurrency)
            };

            result = await adapter.DiscoverAsync(context, new LoggingProgress(_logger, integration.IntegrationId), runCts.Token)
                     ?? throw new InvalidOperationException("The adapter returned no result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A run cut short by shutdown proves nothing about the source system.
            _logger.LogInformation("Discovery for integration {IntegrationId} was cancelled because the service is stopping.", integration.IntegrationId);
            return new DiscoveryRunOutcome(DiscoveryRunStatus.Cancelled, 0, 0, 0);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return await ReportFailureAsync(integration, "DISCOVERY_TIMEOUT",
                $"Discovery did not finish within {integration.Timeout.TotalSeconds:0} seconds.");
        }
        catch (CollectorSecretException ex)
        {
            return await ReportFailureAsync(integration, "SECRET_UNAVAILABLE", ex.Message);
        }
        catch (AdapterConfigurationException ex)
        {
            return await ReportFailureAsync(integration, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            // The message only: adapter stack traces stay out of the server's job history.
            return await ReportFailureAsync(integration, "DISCOVERY_FAILED", ex.Message);
        }

        stopwatch.Stop();
        result.IntegrationId = integration.IntegrationId;
        if (result.Duration <= TimeSpan.Zero)
            result.Duration = stopwatch.Elapsed;

        // Nothing collected: report a failure. A "successful" empty result is
        // treated the same way, since ingesting it would mark the whole
        // inventory stale (and the server refuses empty successful batches).
        if (result.Nodes.Count == 0)
        {
            var reason = result.Errors.FirstOrDefault() ?? "Discovery returned no data.";
            return await ReportFailureAsync(integration, "DISCOVERY_FAILED", reason, result.Errors, result.Warnings, result.Statistics);
        }

        var batches = BuildBatches(result, integration.IntegrationId, _options.BatchSize,
            _options.RelationshipBatchSize, _options.MaxBatchBytes);
        var snapshotId = Guid.NewGuid();
        var buffered = 0;
        for (var i = 0; i < batches.Count; i++)
        {
            var final = i == batches.Count - 1;
            // The stopping token only changes how the batch travels: once the service is
            // stopping, the client buffers this and every later batch instead of sending it, so
            // the snapshot is finished from the offline queue after the restart.
            var delivery = await _client.SubmitSnapshotBatchAsync(batches[i], snapshotId, i, final, cancellationToken);
            if (delivery == BatchDelivery.Buffered && cancellationToken.IsCancellationRequested)
                buffered++;
            if (delivery != BatchDelivery.Rejected)
                continue;

            // The client closes the server job of the incomplete snapshot with a failure report
            // (COLLECTOR_BATCH_REJECTED / COLLECTOR_BATCH_LOST); nothing else of it is sent.
            _logger.LogError("Batch {Batch} of {Total} for integration {IntegrationId} was not accepted; the snapshot is incomplete and is reported as failed.",
                i + 1, batches.Count, integration.IntegrationId);
            return new DiscoveryRunOutcome(DiscoveryRunStatus.Rejected, result.Nodes.Count, result.Relationships.Count, i + 1);
        }

        if (buffered > 0)
        {
            _logger.LogInformation(
                "The service is stopping: {Buffered} of {Total} batches of integration {IntegrationId} were buffered and are sent after the restart.",
                buffered, batches.Count, integration.IntegrationId);
        }

        var status = result.Success && result.Errors.Count == 0 ? DiscoveryRunStatus.Completed : DiscoveryRunStatus.Partial;
        _logger.LogInformation(
            "Discovery for integration {IntegrationId} finished ({Status}): {Nodes} nodes, {Relationships} relationships in {Batches} batches, {Errors} errors.",
            integration.IntegrationId, status, result.Nodes.Count, result.Relationships.Count, batches.Count, result.Errors.Count);
        return new DiscoveryRunOutcome(status, result.Nodes.Count, result.Relationships.Count, batches.Count);
    }

    /// <summary>Envelope of a batch (integration id, status, timestamps) plus JSON punctuation slack.</summary>
    private const long BatchEnvelopeBytes = 4096;

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Splits a result into submit-batch payloads: the nodes first, then the relationships (their
    /// endpoints are then known to the server), packed in order. A batch holds at most
    /// <paramref name="batchSize"/> nodes, <paramref name="relationshipBatchSize"/> relationships
    /// and about <paramref name="maxBatchBytes"/> of JSON, so no request comes near the server's
    /// body limit however large the source is (an item larger than the limit travels alone). The
    /// last batch is the final one and carries the run outcome: Success, Status, errors, warnings
    /// and statistics. The server accumulates these over the batches of a job, so they are sent
    /// once. Earlier batches are plain slices (Success=true, Status "InProgress").
    /// </summary>
    public static IReadOnlyList<DiscoveryResult> BuildBatches(
        DiscoveryResult result,
        Guid integrationId,
        int batchSize,
        int relationshipBatchSize = CollectorOptions.DefaultRelationshipBatchSize,
        long maxBatchBytes = CollectorOptions.DefaultMaxBatchMb * 1024L * 1024L)
    {
        batchSize = Math.Max(1, batchSize);
        relationshipBatchSize = Math.Max(1, relationshipBatchSize);

        // Every batch keeps room for the outcome, which only the final one carries.
        var outcomeBytes = Size(result.Errors) + Size(result.Warnings) + Size(result.Statistics) + Size(result.SourceVersion);
        var itemBudget = Math.Max(maxBatchBytes / 2, maxBatchBytes - BatchEnvelopeBytes - outcomeBytes);

        var chunks = new List<(List<DiscoveredNodeContract> Nodes, List<DiscoveredRelationshipContract> Relationships)>();
        var nodes = new List<DiscoveredNodeContract>();
        var relationships = new List<DiscoveredRelationshipContract>();
        long bytes = 0;

        void Flush()
        {
            chunks.Add((nodes, relationships));
            nodes = new List<DiscoveredNodeContract>();
            relationships = new List<DiscoveredRelationshipContract>();
            bytes = 0;
        }

        foreach (var node in result.Nodes)
        {
            var size = Size(node) + 1;
            if (nodes.Count + relationships.Count > 0 && (nodes.Count >= batchSize || bytes + size > itemBudget))
                Flush();
            nodes.Add(node);
            bytes += size;
        }
        foreach (var relationship in result.Relationships)
        {
            var size = Size(relationship) + 1;
            if (nodes.Count + relationships.Count > 0 && (relationships.Count >= relationshipBatchSize || bytes + size > itemBudget))
                Flush();
            relationships.Add(relationship);
            bytes += size;
        }
        if (chunks.Count == 0 || nodes.Count + relationships.Count > 0)
            Flush();

        var batches = new List<DiscoveryResult>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var final = i == chunks.Count - 1;
            batches.Add(new DiscoveryResult
            {
                IntegrationId = integrationId,
                SourceVersion = result.SourceVersion,
                Nodes = chunks[i].Nodes,
                Relationships = chunks[i].Relationships,
                Success = !final || result.Success,
                Status = final ? result.Status : "InProgress",
                Errors = final ? result.Errors.ToList() : new List<string>(),
                Warnings = final ? result.Warnings.ToList() : new List<string>(),
                Statistics = final ? new Dictionary<string, int>(result.Statistics) : new Dictionary<string, int>(),
                Duration = final ? result.Duration : TimeSpan.Zero,
                CompletedAt = result.CompletedAt
            });
        }
        return batches;
    }

    /// <summary>Serialized size as the client sends it (System.Net.Http.Json web defaults).</summary>
    private static long Size<T>(T value) => value == null ? 0 : JsonSerializer.SerializeToUtf8Bytes(value, WireOptions).LongLength;

    /// <summary>The single final batch that reports a failed run: Success=false, the errors, no nodes.</summary>
    public static DiscoveryResult BuildFailureBatch(
        Guid integrationId,
        IReadOnlyList<string> errors,
        IEnumerable<string>? warnings = null,
        IReadOnlyDictionary<string, int>? statistics = null) => new()
    {
        IntegrationId = integrationId,
        Success = false,
        Status = "Failed",
        Errors = errors.Count > 0 ? errors.Distinct().ToList() : new List<string> { "Discovery failed." },
        Warnings = warnings?.ToList() ?? new List<string>(),
        Statistics = statistics != null ? new Dictionary<string, int>(statistics) : new Dictionary<string, int>(),
        CompletedAt = DateTime.UtcNow
    };

    private async Task<DiscoveryRunOutcome> ReportFailureAsync(
        CollectorIntegration integration,
        string code,
        string message,
        IEnumerable<string>? errors = null,
        IEnumerable<string>? warnings = null,
        IReadOnlyDictionary<string, int>? statistics = null)
    {
        _logger.LogWarning("Discovery for integration {IntegrationId} failed ({Code}): {Message}",
            integration.IntegrationId, code, message);

        var allErrors = new[] { $"{code}: {message}" }.Concat(errors ?? Enumerable.Empty<string>()).ToList();
        var batch = BuildFailureBatch(integration.IntegrationId, allErrors, warnings, statistics);
        var delivery = await _client.SubmitSnapshotBatchAsync(batch, Guid.NewGuid(), 0, final: true, CancellationToken.None);
        if (delivery == BatchDelivery.Rejected)
            _logger.LogError("The server refused the failure report for integration {IntegrationId}.", integration.IntegrationId);
        return new DiscoveryRunOutcome(
            delivery == BatchDelivery.Rejected ? DiscoveryRunStatus.Rejected : DiscoveryRunStatus.Failed, 0, 0, 1);
    }

    /// <summary>Adapter progress goes to the debug log only; there is no job row to update locally.</summary>
    private sealed class LoggingProgress : IProgress<DiscoveryProgress>
    {
        private readonly ILogger _logger;
        private readonly Guid _integrationId;
        private string? _lastPhase;

        public LoggingProgress(ILogger logger, Guid integrationId)
        {
            _logger = logger;
            _integrationId = integrationId;
        }

        public void Report(DiscoveryProgress value)
        {
            if (value == null || value.Phase == _lastPhase)
                return;
            _lastPhase = value.Phase;
            _logger.LogDebug("Integration {IntegrationId}: {Phase} ({Percent}%) {Message}",
                _integrationId, value.Phase, value.Percent, value.Message);
        }
    }
}
