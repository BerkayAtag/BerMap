using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Application.Services.Integrations;
using BerMap.Contracts;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BerMap.Infrastructure.Services;

/// <summary>
/// Executes a queued discovery job end-to-end: decrypt credentials, call the
/// adapter, persist progress, ingest the graph and record the final job state.
/// </summary>
public sealed class DiscoveryJobRunner : IDiscoveryJobRunner
{
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> ActiveJobs = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IIntegrationAdapterRegistry _registry;
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<DiscoveryJobRunner> _logger;

    public DiscoveryJobRunner(
        IServiceScopeFactory scopeFactory,
        IIntegrationAdapterRegistry registry,
        ICredentialStore credentialStore,
        ILogger<DiscoveryJobRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public bool RequestCancellation(Guid jobId)
    {
        if (ActiveJobs.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return true;
        }

        return false;
    }

    public async Task RunJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BerMapDbContext>();
        var ingestion = scope.ServiceProvider.GetRequiredService<IDiscoveryIngestionService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();

        var job = await context.DiscoveryJobs
            .Include(j => j.Integration)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        if (job == null)
        {
            _logger.LogWarning("Discovery job {JobId} no longer exists", jobId);
            return;
        }

        if (job.Status is DiscoveryJobStatus.Completed or DiscoveryJobStatus.Failed or
            DiscoveryJobStatus.Cancelled or DiscoveryJobStatus.PartialSuccess)
        {
            _logger.LogDebug("Discovery job {JobId} is already terminal ({Status})", jobId, job.Status);
            return;
        }

        var integration = job.Integration;
        var adapter = _registry.GetAdapter(integration.AdapterType);

        if (adapter == null)
        {
            await FailJobAsync(context, audit, job, integration,
                "ADAPTER_NOT_FOUND", $"Adapter '{integration.AdapterType}' is not registered.",
                cancellationToken);
            return;
        }

        job.Status = DiscoveryJobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        job.ProgressPercent = 1;
        job.CurrentPhase = "Starting";
        job.CurrentMessage = "Opening a read-only connection to the source system.";
        job.ErrorsJson = "[]";
        job.WarningsJson = "[]";
        job.StatisticsJson = "{}";
        job.CompletedAt = null;
        integration.Status = IntegrationStatus.Testing;
        await context.SaveChangesAsync(cancellationToken);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ActiveJobs[job.Id] = linkedCts;

        // The integration's operation timeout bounds the whole run, so a hung
        // source system cannot keep the job (and the integration) busy forever.
        var operationTimeout = TimeSpan.FromSeconds(Math.Max(30, integration.TimeoutSeconds));
        using var timeoutCts = new CancellationTokenSource(operationTimeout);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, timeoutCts.Token);

        var progressChannel = Channel.CreateBounded<DiscoveryProgress>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var progressTask = ConsumeProgressAsync(_scopeFactory, progressChannel.Reader, job.Id, runCts.Token);
        var progress = new Progress<DiscoveryProgress>(p => progressChannel.Writer.TryWrite(p));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var secret = string.IsNullOrWhiteSpace(integration.CredentialReference)
                ? null
                : await _credentialStore.RetrieveSecretAsync(integration.CredentialReference, runCts.Token);

            var executionContext = new AdapterExecutionContext
            {
                IntegrationId = integration.Id,
                IntegrationName = integration.Name,
                ConfigurationJson = integration.ConfigurationJson,
                Secret = secret,
                ConnectionTimeout = TimeSpan.FromSeconds(30),
                OperationTimeout = operationTimeout,
                MaxConcurrency = Math.Max(1, adapter.GetCapabilities().MaxConcurrency)
            };

            var discovery = await adapter.DiscoverAsync(executionContext, progress, runCts.Token);
            discovery.IntegrationId = integration.Id;
            stopwatch.Stop();

            // A failed run (connection/authentication error, nothing collected)
            // is not ingested at all; a partial run is ingested without marking
            // absent objects stale.
            var nothingCollected = discovery.Nodes.Count == 0 && discovery.Relationships.Count == 0;
            if (!discovery.Success && nothingCollected)
            {
                var reason = discovery.Errors.FirstOrDefault() ?? "Discovery returned no data.";
                await FailJobAsync(context, audit, job, integration, "DISCOVERY_FAILED", reason, CancellationToken.None,
                    discovery.Errors, discovery.Warnings);
                return;
            }

            var isCompleteSnapshot = discovery.Success &&
                                     discovery.Errors.Count == 0 &&
                                     string.Equals(discovery.Status, "Completed", StringComparison.OrdinalIgnoreCase);
            var ingestionResult = await ingestion.IngestAsync(
                integration.Id, job.Id, discovery, isCompleteSnapshot, runCts.Token);

            var errors = discovery.Errors.Distinct().ToList();
            var warnings = discovery.Warnings.Concat(ingestionResult.Warnings).Distinct().ToList();

            job.EntitiesDiscovered = discovery.Nodes.Count;
            job.RelationshipsDiscovered = discovery.Relationships.Count;
            job.ProgressPercent = 100;
            job.CurrentPhase = "Completed";
            job.CurrentMessage = discovery.Success
                ? "Discovery completed and the graph was reconciled."
                : "Discovery completed with partial data. Review the warnings and errors.";
            job.DurationMs = (long)stopwatch.Elapsed.TotalMilliseconds;
            job.CompletedAt = DateTime.UtcNow;
            job.ErrorsJson = JsonSerializer.Serialize(errors);
            job.WarningsJson = JsonSerializer.Serialize(warnings);

            var statistics = new Dictionary<string, int>(discovery.Statistics)
            {
                ["NodesCreated"] = ingestionResult.NodesCreated,
                ["NodesUpdated"] = ingestionResult.NodesUpdated,
                ["NodesUnchanged"] = ingestionResult.NodesUnchanged,
                ["RelationshipsCreated"] = ingestionResult.RelationshipsCreated,
                ["RelationshipsUpdated"] = ingestionResult.RelationshipsUpdated,
                ["ReviewsCreated"] = ingestionResult.ReviewsCreated,
                ["SourceLinksStale"] = ingestionResult.SourceLinksStale,
                ["NodesMarkedStale"] = ingestionResult.NodesMarkedStale
            };
            job.StatisticsJson = JsonSerializer.Serialize(statistics);

            if (discovery.Success && errors.Count == 0)
            {
                job.Status = DiscoveryJobStatus.Completed;
                integration.Status = IntegrationStatusRules.AfterRun(integration.IsEnabled, IntegrationStatus.Active);
                integration.LastError = string.Empty;
                integration.LastErrorAt = null;
            }
            else if (discovery.Nodes.Count > 0 || discovery.Relationships.Count > 0)
            {
                job.Status = DiscoveryJobStatus.PartialSuccess;
                integration.Status = IntegrationStatusRules.AfterRun(integration.IsEnabled, IntegrationStatus.Degraded);
                integration.LastError = errors.FirstOrDefault() ?? "Discovery returned partial data.";
                integration.LastErrorAt = DateTime.UtcNow;
            }
            else
            {
                job.Status = DiscoveryJobStatus.Failed;
                integration.Status = IntegrationStatusRules.AfterRun(integration.IsEnabled, IntegrationStatus.Error);
                integration.LastError = errors.FirstOrDefault() ?? "Discovery returned no data.";
                integration.LastErrorAt = DateTime.UtcNow;
            }

            integration.LastSyncAt = DateTime.UtcNow;
            integration.LastJobId = job.Id;
            integration.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);

            await audit.LogAsync(
                "system", "DiscoveryWorker",
                job.Status == DiscoveryJobStatus.Completed ? "DiscoveryCompleted" : "DiscoveryPartiallyCompleted",
                nameof(DiscoveryJob), job.Id.ToString(),
                JsonSerializer.Serialize(new
                {
                    integration.Id,
                    integration.AdapterType,
                    job.EntitiesDiscovered,
                    job.RelationshipsDiscovered,
                    ingestionResult.NodesCreated,
                    ingestionResult.NodesUpdated,
                    ingestionResult.ReviewsCreated,
                    warnings = warnings.Count,
                    errors = errors.Count
                }),
                string.Empty,
                job.Status == DiscoveryJobStatus.Completed,
                CancellationToken.None);

            _logger.LogInformation(
                "Discovery job {JobId} finished with {Status}: {Nodes} nodes, {Relationships} relationships, {Errors} errors",
                job.Id, job.Status, job.EntitiesDiscovered, job.RelationshipsDiscovered, errors.Count);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !linkedCts.IsCancellationRequested)
        {
            stopwatch.Stop();
            await FailJobAsync(context, audit, job, integration, "DISCOVERY_TIMEOUT",
                $"Discovery did not finish within {operationTimeout.TotalSeconds:0} seconds.", CancellationToken.None);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            stopwatch.Stop();
            (job, integration) = await ReloadAsync(context, job, integration);
            job.Status = DiscoveryJobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            job.DurationMs = (long)stopwatch.Elapsed.TotalMilliseconds;
            job.CurrentPhase = "Cancelled";
            job.CurrentMessage = "Discovery was cancelled.";
            // A cancelled run proves nothing; the previous health stays.
            integration.Status = IntegrationStatusRules.Resting(integration.IsEnabled, integration.LastError);
            integration.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(CancellationToken.None);

            await audit.LogAsync(
                "system", "DiscoveryWorker", "DiscoveryCancelled", nameof(DiscoveryJob), job.Id.ToString(),
                JsonSerializer.Serialize(new { integration.Id, integration.AdapterType }),
                string.Empty, true, CancellationToken.None);
        }
        catch (AdapterConfigurationException ex)
        {
            stopwatch.Stop();
            await FailJobAsync(context, audit, job, integration, ex.Code, ex.Message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Discovery job {JobId} failed for integration {IntegrationId}", job.Id, integration.Id);
            await FailJobAsync(context, audit, job, integration, "DISCOVERY_FAILED", ex.Message, CancellationToken.None);
        }
        finally
        {
            progressChannel.Writer.TryComplete();
            try
            {
                await progressTask;
            }
            catch (OperationCanceledException)
            {
                // The runner already recorded the cancellation.
            }

            ActiveJobs.TryRemove(job.Id, out _);
        }
    }

    private static async Task ConsumeProgressAsync(
        IServiceScopeFactory scopeFactory,
        ChannelReader<DiscoveryProgress> reader,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            DiscoveryProgress? latest = null;
            while (reader.TryRead(out var update))
                latest = update;

            if (latest == null)
                continue;

            try
            {
                using var progressScope = scopeFactory.CreateScope();
                var context = progressScope.ServiceProvider.GetRequiredService<BerMapDbContext>();
                // A single conditional UPDATE, so a late progress report can never
                // overwrite the phase/message of a job that already finished.
                var percent = Math.Clamp(latest.Percent, 0, 100);
                var now = DateTime.UtcNow;
                await context.DiscoveryJobs
                    .Where(j => j.Id == jobId &&
                                (j.Status == DiscoveryJobStatus.Queued || j.Status == DiscoveryJobStatus.Running))
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(j => j.ProgressPercent, percent)
                        .SetProperty(j => j.CurrentPhase, latest.Phase)
                        .SetProperty(j => j.CurrentMessage, latest.Message)
                        .SetProperty(j => j.UpdatedAt, now), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Progress persistence is best-effort and must never fail discovery.
            }

            await Task.Delay(750, cancellationToken);
        }
    }

    /// <summary>
    /// Drops whatever a failed ingestion left in the change tracker (half-built
    /// graph, entities that violated a constraint) so recording the terminal
    /// job state cannot fail for the same reason, or persist a partial graph.
    /// </summary>
    private static async Task<(DiscoveryJob Job, Integration Integration)> ReloadAsync(
        BerMapDbContext context, DiscoveryJob job, Integration integration)
    {
        context.ChangeTracker.Clear();
        var reloadedJob = await context.DiscoveryJobs.FirstAsync(j => j.Id == job.Id, CancellationToken.None);
        var reloadedIntegration = await context.Integrations.FirstAsync(i => i.Id == integration.Id, CancellationToken.None);
        return (reloadedJob, reloadedIntegration);
    }

    private static async Task FailJobAsync(
        BerMapDbContext context,
        IAuditService audit,
        DiscoveryJob job,
        Integration integration,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken,
        IEnumerable<string>? errors = null,
        IEnumerable<string>? warnings = null)
    {
        (job, integration) = await ReloadAsync(context, job, integration);

        job.Status = DiscoveryJobStatus.Failed;
        job.CompletedAt = DateTime.UtcNow;
        job.ProgressPercent = 100;
        job.CurrentPhase = "Failed";
        job.CurrentMessage = errorMessage;
        job.ErrorsJson = JsonSerializer.Serialize(
            new[] { $"{errorCode}: {errorMessage}" }.Concat(errors ?? Enumerable.Empty<string>()).Distinct());
        job.WarningsJson = JsonSerializer.Serialize(warnings ?? Enumerable.Empty<string>());
        job.DurationMs = job.CompletedAt.HasValue
            ? (long)(job.CompletedAt.Value - job.StartedAt).TotalMilliseconds
            : null;

        integration.Status = IntegrationStatusRules.AfterRun(integration.IsEnabled, IntegrationStatus.Error);
        integration.LastError = $"{errorCode}: {errorMessage}";
        integration.LastErrorAt = DateTime.UtcNow;
        integration.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(
            "system", "DiscoveryWorker", "DiscoveryFailed", nameof(DiscoveryJob), job.Id.ToString(),
            JsonSerializer.Serialize(new { integration.Id, errorCode, errorMessage }),
            string.Empty, false, CancellationToken.None);
    }
}
