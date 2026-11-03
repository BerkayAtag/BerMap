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

        var secret = string.IsNullOrWhiteSpace(integration.CredentialReference)
            ? null
            : await _credentialStore.RetrieveSecretAsync(integration.CredentialReference, cancellationToken);

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

        var progressChannel = Channel.CreateBounded<DiscoveryProgress>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var progressTask = ConsumeProgressAsync(_scopeFactory, progressChannel.Reader, job.Id, linkedCts.Token);
        var progress = new Progress<DiscoveryProgress>(p => progressChannel.Writer.TryWrite(p));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var executionContext = new AdapterExecutionContext
            {
                IntegrationId = integration.Id,
                IntegrationName = integration.Name,
                ConfigurationJson = integration.ConfigurationJson,
                Secret = secret,
                ConnectionTimeout = TimeSpan.FromSeconds(30),
                OperationTimeout = TimeSpan.FromSeconds(integration.TimeoutSeconds),
                MaxConcurrency = Math.Max(1, adapter.GetCapabilities().MaxConcurrency)
            };

            var discovery = await adapter.DiscoverAsync(executionContext, progress, linkedCts.Token);
            discovery.IntegrationId = integration.Id;
            stopwatch.Stop();

            var ingestionResult = await ingestion.IngestAsync(integration.Id, job.Id, discovery, linkedCts.Token);

            var errors = discovery.Errors.Concat(ingestionResult.Warnings).Distinct().ToList();
            var warnings = discovery.Warnings.Distinct().ToList();

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
                integration.Status = IntegrationStatus.Active;
                integration.LastError = string.Empty;
                integration.LastErrorAt = null;
            }
            else if (discovery.Nodes.Count > 0 || discovery.Relationships.Count > 0)
            {
                job.Status = DiscoveryJobStatus.PartialSuccess;
                integration.Status = IntegrationStatus.Degraded;
                integration.LastError = errors.FirstOrDefault() ?? "Discovery returned partial data.";
                integration.LastErrorAt = DateTime.UtcNow;
            }
            else
            {
                job.Status = DiscoveryJobStatus.Failed;
                integration.Status = IntegrationStatus.Error;
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
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            job.Status = DiscoveryJobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            job.DurationMs = (long)stopwatch.Elapsed.TotalMilliseconds;
            job.CurrentPhase = "Cancelled";
            job.CurrentMessage = "Discovery was cancelled.";
            integration.Status = integration.IsEnabled ? IntegrationStatus.Active : IntegrationStatus.Inactive;
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
                var job = await context.DiscoveryJobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
                if (job == null || job.Status is not (DiscoveryJobStatus.Queued or DiscoveryJobStatus.Running))
                    continue;

                job.ProgressPercent = Math.Clamp(latest.Percent, 0, 100);
                job.CurrentPhase = latest.Phase;
                job.CurrentMessage = latest.Message;
                job.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
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

    private static async Task FailJobAsync(
        BerMapDbContext context,
        IAuditService audit,
        DiscoveryJob job,
        Integration integration,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        job.Status = DiscoveryJobStatus.Failed;
        job.CompletedAt = DateTime.UtcNow;
        job.ProgressPercent = 100;
        job.CurrentPhase = "Failed";
        job.CurrentMessage = errorMessage;
        job.ErrorsJson = JsonSerializer.Serialize(new[] { $"{errorCode}: {errorMessage}" });
        job.DurationMs = job.CompletedAt.HasValue
            ? (long)(job.CompletedAt.Value - job.StartedAt).TotalMilliseconds
            : null;

        integration.Status = IntegrationStatus.Error;
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
