using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Application.Services.Integrations;
using BerMap.Application.Services.Reconciliation;
using BerMap.Application.Services.Scheduling;
using BerMap.Contracts;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BerMap.Infrastructure.Services;

public sealed class IntegrationService : IIntegrationService
{
    private static readonly HashSet<string> CommonSecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "secret", "token", "apikey", "api_key", "clientsecret", "client_secret",
        "privatekey", "private_key", "credential", "credentials", "bearer"
    };

    private readonly BerMapDbContext _context;
    private readonly IIntegrationAdapterRegistry _registry;
    private readonly ICredentialStore _credentialStore;
    private readonly IAuditService _auditService;
    private readonly IDiscoveryJobRunner _jobRunner;
    private readonly ILogger<IntegrationService> _logger;
    private readonly DiscoveryScheduleSettings _schedule;

    public IntegrationService(
        BerMapDbContext context,
        IIntegrationAdapterRegistry registry,
        ICredentialStore credentialStore,
        IAuditService auditService,
        IDiscoveryJobRunner jobRunner,
        ILogger<IntegrationService> logger,
        DiscoveryScheduleSettings schedule)
    {
        _context = context;
        _registry = registry;
        _credentialStore = credentialStore;
        _auditService = auditService;
        _jobRunner = jobRunner;
        _logger = logger;
        _schedule = schedule;
    }

    public async Task<List<IntegrationDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var integrations = await _context.Integrations
            .AsNoTracking()
            .Where(i => i.DeletedAt == null)
            .OrderBy(i => i.Name)
            .ToListAsync(cancellationToken);

        var lastStarts = await LastJobStartsAsync(integrations.Select(i => i.Id).ToList(), cancellationToken);
        return integrations.Select(i => MapIntegration(i, lastStarts.GetValueOrDefault(i.Id))).ToList();
    }

    public async Task<IntegrationDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var integration = await _context.Integrations
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id && i.DeletedAt == null, cancellationToken);

        if (integration == null)
            return null;

        var lastStarts = await LastJobStartsAsync(new List<Guid> { integration.Id }, cancellationToken);
        return MapIntegration(integration, lastStarts.GetValueOrDefault(integration.Id));
    }

    public async Task<IntegrationDto> CreateAsync(
        CreateIntegrationRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ValidateName(request.Name);
        var adapter = RequireAdapter(request.AdapterType);
        var configuration = BuildConfiguration(request.Configuration ?? new(), adapter, rejectSecrets: true);
        ValidateServer(configuration);

        // A collector-managed integration is configured on the collector: the
        // server never connects to the source, so it needs neither the
        // connection fields nor the credential.
        if (!request.CollectorManaged)
        {
            ValidateRequiredConfiguration(configuration, adapter);
            if (adapter.ConfigurationSchema.SecretFields.Count > 0 && string.IsNullOrWhiteSpace(request.Secret))
            {
                throw new IntegrationValidationException(
                    "SECRET_REQUIRED",
                    $"The {adapter.DisplayName} adapter needs a credential ({string.Join(", ", adapter.ConfigurationSchema.SecretFields)}).");
            }
        }

        ValidateSchedule(request.ScheduleCron);

        var id = Guid.NewGuid();
        var integration = new Integration
        {
            Id = id,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            AdapterType = adapter.AdapterType,
            AdapterVersion = adapter.Version,
            Status = IntegrationStatusRules.Resting(request.IsEnabled, null),
            IsEnabled = request.IsEnabled,
            CollectorManaged = request.CollectorManaged,
            ConfigurationJson = configuration.ToJsonString(),
            ConfigurationVersion = 1,
            CredentialReference = string.Empty,
            ScheduleCron = request.ScheduleCron.Trim(),
            TimeoutSeconds = ClampTimeout(request.TimeoutSeconds),
            CreatedBy = actor,
            CreatedAt = DateTime.UtcNow
        };

        if (!string.IsNullOrWhiteSpace(request.Secret))
        {
            integration.CredentialReference = CredentialKey(id);
            await _credentialStore.StoreSecretAsync(integration.CredentialReference, request.Secret, cancellationToken);
        }

        _context.Integrations.Add(integration);
        await _context.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync(
            actor, actor, "IntegrationCreated", nameof(Integration), integration.Id.ToString(),
            JsonSerializer.Serialize(new { integration.AdapterType, integration.Name, integration.CollectorManaged, integration.IsEnabled }),
            GetIpAddress(), true, cancellationToken);

        return MapIntegration(integration, null);
    }

    public async Task<IntegrationDto?> UpdateAsync(
        Guid id,
        UpdateIntegrationRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var integration = await _context.Integrations
            .FirstOrDefaultAsync(i => i.Id == id && i.DeletedAt == null, cancellationToken);

        if (integration == null)
            return null;

        var adapter = RequireAdapter(integration.AdapterType);

        // Audit details record what changed. Configuration values are
        // non-secret by construction; secrets are only reported as rotated or
        // cleared, never with a value.
        var changes = new Dictionary<string, object?>();
        void Track<T>(string field, T from, T to)
        {
            if (!EqualityComparer<T>.Default.Equals(from, to))
                changes[field] = new { from, to };
        }

        var collectorManaged = request.CollectorManaged ?? integration.CollectorManaged;

        if (request.Name != null)
        {
            ValidateName(request.Name);
            Track("name", integration.Name, request.Name.Trim());
            integration.Name = request.Name.Trim();
        }

        if (request.Description != null)
        {
            Track("description", integration.Description, request.Description.Trim());
            integration.Description = request.Description.Trim();
        }

        List<string> configurationKeysChanged = new();
        if (request.Configuration != null)
        {
            var configuration = BuildConfiguration(request.Configuration, adapter, rejectSecrets: true);
            ValidateServer(configuration);
            if (!collectorManaged)
                ValidateRequiredConfiguration(configuration, adapter);

            configurationKeysChanged = ChangedKeys(integration.ConfigurationJson, configuration);
            if (configurationKeysChanged.Count > 0)
            {
                integration.ConfigurationJson = configuration.ToJsonString();
                integration.ConfigurationVersion++;
            }
        }

        if (request.ScheduleCron != null)
        {
            ValidateSchedule(request.ScheduleCron);
            Track("scheduleCron", integration.ScheduleCron, request.ScheduleCron.Trim());
            integration.ScheduleCron = request.ScheduleCron.Trim();
        }

        if (request.TimeoutSeconds.HasValue)
        {
            var timeout = ClampTimeout(request.TimeoutSeconds.Value);
            Track("timeoutSeconds", integration.TimeoutSeconds, timeout);
            integration.TimeoutSeconds = timeout;
        }

        var secretRotated = !request.ClearSecret && !string.IsNullOrWhiteSpace(request.Secret);
        var secretCleared = request.ClearSecret && !string.IsNullOrWhiteSpace(integration.CredentialReference);

        if (collectorManaged != integration.CollectorManaged)
        {
            // Switching back to server-side discovery needs a credential the
            // server can use.
            if (!collectorManaged &&
                adapter.ConfigurationSchema.SecretFields.Count > 0 &&
                !secretRotated &&
                (request.ClearSecret || string.IsNullOrWhiteSpace(integration.CredentialReference)))
            {
                throw new IntegrationValidationException(
                    "SECRET_REQUIRED",
                    $"The {adapter.DisplayName} adapter needs a credential before the server can discover it.");
            }

            if (!collectorManaged)
                ValidateRequiredConfiguration(ParseConfiguration(integration.ConfigurationJson), adapter);

            Track("collectorManaged", integration.CollectorManaged, collectorManaged);
            integration.CollectorManaged = collectorManaged;

            // Errors of server-side tests say nothing about collector-fed data
            // (and vice versa); the next test or batch reports fresh health.
            integration.LastError = string.Empty;
            integration.LastErrorAt = null;
            integration.LastTestSuccess = null;
            integration.Status = IntegrationStatusRules.Resting(integration.IsEnabled, lastOutcome: null);
        }

        if (request.IsEnabled.HasValue && request.IsEnabled.Value != integration.IsEnabled)
        {
            Track("isEnabled", integration.IsEnabled, request.IsEnabled.Value);
            integration.IsEnabled = request.IsEnabled.Value;

            // Re-enabling must not hide a failed test or discovery, nor turn
            // a partial (Degraded) run into an Error.
            integration.Status = await IntegrationHealth.RestingStatusAsync(_context, integration, cancellationToken);
        }

        if (request.ClearSecret)
        {
            if (!string.IsNullOrWhiteSpace(integration.CredentialReference))
                await _credentialStore.DeleteSecretAsync(integration.CredentialReference, cancellationToken);
            integration.CredentialReference = string.Empty;
        }
        else if (secretRotated)
        {
            integration.CredentialReference = string.IsNullOrWhiteSpace(integration.CredentialReference)
                ? CredentialKey(id)
                : integration.CredentialReference;
            await _credentialStore.StoreSecretAsync(integration.CredentialReference, request.Secret!, cancellationToken);
        }

        integration.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync(
            actor, actor, "IntegrationUpdated", nameof(Integration), integration.Id.ToString(),
            JsonSerializer.Serialize(new
            {
                integration.Name,
                integration.ConfigurationVersion,
                changes,
                configurationKeysChanged,
                secretRotated,
                secretCleared
            }),
            GetIpAddress(), true, cancellationToken);

        var lastStarts = await LastJobStartsAsync(new List<Guid> { integration.Id }, cancellationToken);
        return MapIntegration(integration, lastStarts.GetValueOrDefault(integration.Id));
    }

    public async Task<bool> DeleteAsync(Guid id, string actor, CancellationToken cancellationToken = default)
    {
        var integration = await _context.Integrations
            .FirstOrDefaultAsync(i => i.Id == id && i.DeletedAt == null, cancellationToken);

        if (integration == null)
            return false;

        integration.DeletedAt = DateTime.UtcNow;
        integration.IsEnabled = false;
        integration.Status = IntegrationStatus.Inactive;
        integration.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(integration.CredentialReference))
        {
            await _credentialStore.DeleteSecretAsync(integration.CredentialReference, cancellationToken);
            integration.CredentialReference = string.Empty;
        }

        var cancelledJobs = await CloseJobsOfDeletedIntegrationAsync(id, cancellationToken);
        var cleanup = await RetireDiscoveredDataAsync(id, actor, cancellationToken);
        integration.DiscoveredEntitiesCount = 0;

        await _context.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync(
            actor, actor, "IntegrationDeleted", nameof(Integration), id.ToString(),
            JsonSerializer.Serialize(new
            {
                integration.Name,
                integration.AdapterType,
                jobsClosed = cancelledJobs,
                sourceLinksStale = cleanup.Links,
                nodesMarkedStale = cleanup.Nodes,
                relationshipsMarkedStale = cleanup.Relationships,
                reviewsDismissed = cleanup.Reviews
            }),
            GetIpAddress(), true, cancellationToken);

        return true;
    }

    public async Task<ConnectionTestResult> TestDraftAsync(
        TestConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var adapter = RequireAdapter(request.AdapterType);
        var configuration = BuildConfiguration(request.Configuration ?? new(), adapter, rejectSecrets: true);
        ValidateServer(configuration);

        var context = new AdapterExecutionContext
        {
            IntegrationId = Guid.Empty,
            IntegrationName = "Draft integration",
            ConfigurationJson = configuration.ToJsonString(),
            Secret = request.Secret,
            ConnectionTimeout = TimeSpan.FromSeconds(ClampTimeout(request.TimeoutSeconds)),
            OperationTimeout = TimeSpan.FromSeconds(ClampTimeout(request.TimeoutSeconds)),
            MaxConcurrency = Math.Max(1, adapter.Capabilities.MaxConcurrency)
        };

        return await RunConnectionTestAsync(adapter, context, cancellationToken);
    }

    public async Task<ConnectionTestResult?> TestAsync(
        Guid id,
        string? secretOverride,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var integration = await _context.Integrations
            .FirstOrDefaultAsync(i => i.Id == id && i.DeletedAt == null, cancellationToken);

        if (integration == null)
            return null;

        RejectCollectorManaged(integration);

        var adapter = RequireAdapter(integration.AdapterType);
        var secret = secretOverride;
        if (string.IsNullOrWhiteSpace(secret) && !string.IsNullOrWhiteSpace(integration.CredentialReference))
            secret = await _credentialStore.RetrieveSecretAsync(integration.CredentialReference, cancellationToken);

        var context = BuildContext(integration, adapter, secret);
        var result = await RunConnectionTestAsync(adapter, context, cancellationToken);

        integration.LastTestAt = DateTime.UtcNow;
        integration.LastTestSuccess = result.Success;
        integration.LastError = result.Success ? string.Empty : $"{result.ErrorCode}: {result.ErrorDetail}";
        integration.LastErrorAt = result.Success ? null : DateTime.UtcNow;
        integration.Status = IntegrationStatusRules.AfterRun(
            integration.IsEnabled, result.Success ? IntegrationStatus.Active : IntegrationStatus.Error);
        integration.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync(
            actor, actor, "IntegrationConnectionTested", nameof(Integration), id.ToString(),
            JsonSerializer.Serialize(new
            {
                result.Success,
                result.ErrorCode,
                latencyMs = (int)result.Latency.TotalMilliseconds,
                warnings = result.Warnings
            }),
            GetIpAddress(), result.Success, cancellationToken);

        return result;
    }

    public async Task<DiscoveryJobDto?> StartDiscoveryAsync(
        Guid id,
        StartDiscoveryRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var integration = await _context.Integrations
            .FirstOrDefaultAsync(i => i.Id == id && i.DeletedAt == null, cancellationToken);

        if (integration == null)
            return null;

        RejectCollectorManaged(integration);

        if (!integration.IsEnabled)
            throw new IntegrationValidationException("INTEGRATION_DISABLED", "This integration is disabled.");

        var adapter = RequireAdapter(integration.AdapterType);

        // A collector job left behind when the integration was switched to
        // server-side discovery must not block discovery forever.
        if (await IntegrationHealth.FailAbandonedCollectorJobsAsync(_context, id, cancellationToken) > 0)
            await _context.SaveChangesAsync(cancellationToken);

        var runningJob = await _context.DiscoveryJobs
            .AsNoTracking()
            .AnyAsync(j => j.IntegrationId == id &&
                           (j.Status == DiscoveryJobStatus.Queued || j.Status == DiscoveryJobStatus.Running),
                cancellationToken);

        if (runningJob)
            throw new IntegrationValidationException("DISCOVERY_ALREADY_ACTIVE", "A discovery job is already queued or running for this integration.");

        var job = new DiscoveryJob
        {
            Id = Guid.NewGuid(),
            IntegrationId = integration.Id,
            JobType = string.IsNullOrWhiteSpace(request.JobType) ? "Full" : request.JobType.Trim(),
            Status = DiscoveryJobStatus.Queued,
            StartedAt = DateTime.UtcNow,
            RequestedBy = actor,
            CurrentPhase = "Queued",
            CurrentMessage = "Waiting for the discovery worker."
        };

        _context.DiscoveryJobs.Add(job);
        integration.LastJobId = job.Id;
        integration.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Discovery job {JobId} queued for integration {IntegrationId} ({AdapterType}) by {Actor}",
            job.Id, integration.Id, adapter.AdapterType, actor);

        await _auditService.LogAsync(
            actor, actor, "DiscoveryQueued", nameof(DiscoveryJob), job.Id.ToString(),
            JsonSerializer.Serialize(new { integration.Id, integration.Name, job.JobType }),
            GetIpAddress(), true, cancellationToken);

        return MapJob(job, integration);
    }

    public async Task<List<DiscoveryJobDto>> ListJobsAsync(
        Guid? integrationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = _context.DiscoveryJobs
            .AsNoTracking()
            .Include(j => j.Integration)
            .AsQueryable();

        if (integrationId.HasValue)
            query = query.Where(j => j.IntegrationId == integrationId.Value);

        var jobs = await query
            .OrderByDescending(j => j.StartedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

        return jobs.Select(j => MapJob(j, j.Integration)).ToList();
    }

    public async Task<DiscoveryJobDto?> GetJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await _context.DiscoveryJobs
            .AsNoTracking()
            .Include(j => j.Integration)
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        return job == null ? null : MapJob(job, job.Integration);
    }

    public async Task<CancelJobOutcome> CancelJobAsync(Guid jobId, string actor, CancellationToken cancellationToken = default)
    {
        var job = await _context.DiscoveryJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        if (job == null)
            return CancelJobOutcome.NotFound;

        if (job.Status == DiscoveryJobStatus.Queued)
        {
            job.Status = DiscoveryJobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            job.CurrentPhase = "Cancelled";
            job.CurrentMessage = "Cancelled before execution.";
        }
        else if (job.Status == DiscoveryJobStatus.Running)
        {
            if (_jobRunner.RequestCancellation(jobId))
            {
                job.CurrentMessage = "Cancellation requested.";
            }
            else if (job.JobType == "Collector")
            {
                // Collector jobs have no in-process runner; close them directly.
                job.Status = DiscoveryJobStatus.Cancelled;
                job.CompletedAt = DateTime.UtcNow;
                job.CurrentPhase = "Cancelled";
                job.CurrentMessage = "Cancelled while waiting for further collector batches.";
            }
            else
            {
                return CancelJobOutcome.NotCancellable;
            }
        }
        else
        {
            return CancelJobOutcome.NotCancellable;
        }

        await _context.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync(
            actor, actor, "DiscoveryCancelled", nameof(DiscoveryJob), jobId.ToString(),
            JsonSerializer.Serialize(new { job.IntegrationId }),
            GetIpAddress(), true, cancellationToken);

        return CancelJobOutcome.Cancelled;
    }

    /// <summary>
    /// Closes every open job of a deleted integration. In-process runs are
    /// asked to stop (the runner records the cancellation); collector jobs and
    /// runs without a runner in this process are closed directly, otherwise a
    /// collector job would stay Running forever.
    /// </summary>
    private async Task<int> CloseJobsOfDeletedIntegrationAsync(Guid integrationId, CancellationToken cancellationToken)
    {
        var openJobs = await _context.DiscoveryJobs
            .Where(j => j.IntegrationId == integrationId &&
                        (j.Status == DiscoveryJobStatus.Queued || j.Status == DiscoveryJobStatus.Running))
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var job in openJobs)
        {
            if (job.Status == DiscoveryJobStatus.Running && job.JobType != "Collector" &&
                _jobRunner.RequestCancellation(job.Id))
            {
                job.CurrentMessage = "Cancellation requested because the integration was deleted.";
                continue;
            }

            var wasQueued = job.Status == DiscoveryJobStatus.Queued;
            job.Status = wasQueued || job.JobType == "Collector" ? DiscoveryJobStatus.Cancelled : DiscoveryJobStatus.Failed;
            job.CompletedAt = now;
            job.UpdatedAt = now;
            job.CurrentPhase = job.Status.ToString();
            job.CurrentMessage = wasQueued
                ? "Integration was deleted before the job started."
                : "Integration was deleted while the job was running.";
        }

        return openJobs.Count;
    }

    /// <summary>
    /// A deleted integration can never refresh what it discovered, so its
    /// source links and relationships become stale, and so does every node
    /// that no other live integration still reports — the same rule the
    /// complete-snapshot sweep of ingestion applies. Its pending
    /// reconciliation reviews are dismissed: no ingestion can update them again.
    /// </summary>
    private async Task<(int Links, int Nodes, int Relationships, int Reviews)> RetireDiscoveredDataAsync(
        Guid integrationId,
        string actor,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var links = await _context.NodeSourceLinks
            .Where(l => l.IntegrationId == integrationId && !l.IsStale)
            .ToListAsync(cancellationToken);
        foreach (var link in links)
        {
            link.IsStale = true;
            link.UpdatedAt = now;
        }

        var nodeIds = await _context.NodeSourceLinks
            .Where(l => l.IntegrationId == integrationId)
            .Select(l => l.NodeId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var stillReported = await _context.NodeSourceLinks
            .Where(l => nodeIds.Contains(l.NodeId) &&
                        l.IntegrationId != integrationId &&
                        !l.IsStale &&
                        l.Integration.DeletedAt == null)
            .Select(l => l.NodeId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var orphanIds = nodeIds.Except(stillReported).ToList();

        // Nodes other sources still report show the worst status of those
        // sources only; the deleted integration's status no longer counts.
        var remainingLinks = await _context.NodeSourceLinks
            .AsNoTracking()
            .Where(l => stillReported.Contains(l.NodeId) &&
                        l.IntegrationId != integrationId &&
                        !l.IsStale &&
                        l.Integration.DeletedAt == null)
            .Select(l => new { l.NodeId, l.PropertiesJson })
            .ToListAsync(cancellationToken);
        var effectiveStatus = remainingLinks
            .GroupBy(l => l.NodeId)
            .ToDictionary(g => g.Key, g => NodeStatusRules.Effective(g.Select(l => NodeStatusRules.Reported(l.PropertiesJson))));
        var reportedNodes = await _context.InfrastructureNodes
            .Where(n => stillReported.Contains(n.Id))
            .ToListAsync(cancellationToken);
        foreach (var node in reportedNodes)
        {
            if (effectiveStatus.GetValueOrDefault(node.Id) is not { } status ||
                string.Equals(node.Status, status, StringComparison.Ordinal))
            {
                continue;
            }

            node.Status = status;
            node.StatusChangedAt = now;
            node.UpdatedAt = now;
        }

        var nodes = await _context.InfrastructureNodes
            .Where(n => orphanIds.Contains(n.Id) && !n.IsStale)
            .ToListAsync(cancellationToken);
        foreach (var node in nodes)
        {
            node.IsStale = true;
            node.StaleSince = now;
            node.UpdatedAt = now;
        }

        var relationships = await _context.InfrastructureRelationships
            .Where(r => r.IntegrationId == integrationId && !r.IsStale)
            .ToListAsync(cancellationToken);
        foreach (var relationship in relationships)
        {
            relationship.IsStale = true;
            relationship.StaleSince = now;
            relationship.UpdatedAt = now;
        }

        var reviews = await _context.ReconciliationReviews
            .Where(r => r.IntegrationId == integrationId && r.Status == ReconciliationReviewStatus.Pending)
            .ToListAsync(cancellationToken);
        foreach (var review in reviews)
        {
            review.Status = ReconciliationReviewStatus.Dismissed;
            review.ReviewedAt = now;
            review.ReviewedBy = actor;
            review.ResolutionNotes = "Integration deleted";
            review.UpdatedAt = now;
        }

        return (links.Count, nodes.Count, relationships.Count, reviews.Count);
    }

    private static void RejectCollectorManaged(Integration integration)
    {
        if (integration.CollectorManaged)
        {
            throw new IntegrationValidationException(
                "COLLECTOR_MANAGED",
                "This integration is fed by a collector. Test and discovery run on the collector, not on the server.");
        }
    }

    private AdapterDescriptorDto RequireAdapter(string adapterType)
    {
        var adapter = _registry.GetAdapter(adapterType);
        if (adapter == null)
            throw new IntegrationValidationException("ADAPTER_NOT_FOUND", $"Unknown adapter type '{adapterType}'.");

        return _registry.GetDescriptors().First(d =>
            d.AdapterType.Equals(adapter.AdapterType, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject BuildConfiguration(
        IReadOnlyDictionary<string, JsonElement> configuration,
        AdapterDescriptorDto adapter,
        bool rejectSecrets)
    {
        var secretKeys = new HashSet<string>(CommonSecretKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var field in adapter.ConfigurationSchema.SecretFields)
            secretKeys.Add(field);

        var result = new JsonObject();
        foreach (var (key, value) in configuration)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new IntegrationValidationException("CONFIG_KEY", "Configuration keys must not be empty.");

            if (rejectSecrets && secretKeys.Contains(key))
                throw new IntegrationValidationException(
                    "SECRET_IN_CONFIGURATION",
                    $"'{key}' is a secret field. Send it in the 'secret' property so it can be stored in the encrypted credential vault.");

            if (value.ValueKind is JsonValueKind.Undefined)
                continue;

            result[key] = value.ValueKind == JsonValueKind.Null
                ? null
                : JsonNode.Parse(value.GetRawText());
        }

        return result;
    }

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new IntegrationValidationException("NAME_REQUIRED", "Integration name is required.");
        if (name.Trim().Length > 200)
            throw new IntegrationValidationException("NAME_TOO_LONG", "Integration name must be 200 characters or fewer.");
    }

    private static void ValidateSchedule(string? schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule))
            throw new IntegrationValidationException("SCHEDULE_REQUIRED", "Discovery schedule is required.");

        if (!CronSchedule.TryParse(schedule, out _, out var error))
            throw new IntegrationValidationException("SCHEDULE_INVALID", $"Discovery schedule is not a valid cron expression: {error}");
    }

    /// <summary>
    /// Rejects a configuration that lacks a field the adapter's JSON schema
    /// lists as "required"; such an integration would fail on every run.
    /// </summary>
    internal static void ValidateRequiredConfiguration(JsonObject configuration, AdapterDescriptorDto adapter)
    {
        // Secret fields live in the credential vault and are checked separately.
        var missing = RequiredFields(adapter)
            .Where(field => !adapter.ConfigurationSchema.SecretFields.Contains(field, StringComparer.OrdinalIgnoreCase))
            .Where(field => configuration[field] switch
            {
                null => true,
                JsonValue value when value.TryGetValue<string>(out var text) => string.IsNullOrWhiteSpace(text),
                _ => false
            })
            .ToList();

        if (missing.Count > 0)
        {
            throw new IntegrationValidationException(
                "CONFIG_REQUIRED",
                $"Required configuration field(s) missing: {string.Join(", ", missing)}.");
        }
    }

    /// <summary>The server field takes a host name or IP address, not a URL.</summary>
    internal static void ValidateServer(JsonObject configuration)
    {
        if (configuration["server"] is JsonValue value &&
            value.TryGetValue<string>(out var server) &&
            (server.Contains("://", StringComparison.Ordinal) || server.Contains('/')))
        {
            throw new IntegrationValidationException(
                "CONFIG_INVALID_SERVER",
                "Enter the server as a host name or IP address without a scheme or path (for example vcenter.corp.local).");
        }
    }

    private static IReadOnlyList<string> RequiredFields(AdapterDescriptorDto adapter)
    {
        try
        {
            using var schema = JsonDocument.Parse(string.IsNullOrWhiteSpace(adapter.ConfigurationSchema.JsonSchema)
                ? "{}"
                : adapter.ConfigurationSchema.JsonSchema);
            if (schema.RootElement.ValueKind == JsonValueKind.Object &&
                schema.RootElement.TryGetProperty("required", out var required) &&
                required.ValueKind == JsonValueKind.Array)
            {
                return required.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // A malformed adapter schema must not block integration management.
        }

        return Array.Empty<string>();
    }

    private static JsonObject ParseConfiguration(string json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static List<string> ChangedKeys(string previousJson, JsonObject next)
    {
        var previous = ParseConfiguration(previousJson);
        return previous.Select(kv => kv.Key)
            .Union(next.Select(kv => kv.Key), StringComparer.Ordinal)
            .Where(key => !JsonNode.DeepEquals(previous[key], next[key]))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
    }

    private static int ClampTimeout(int seconds) => Math.Clamp(seconds, 30, 86_400);

    private static string CredentialKey(Guid integrationId) => $"integration:{integrationId:N}";

    private static AdapterExecutionContext BuildContext(Integration integration, AdapterDescriptorDto adapter, string? secret)
    {
        return new AdapterExecutionContext
        {
            IntegrationId = integration.Id,
            IntegrationName = integration.Name,
            ConfigurationJson = integration.ConfigurationJson,
            Secret = secret,
            ConnectionTimeout = TimeSpan.FromSeconds(30),
            OperationTimeout = TimeSpan.FromSeconds(integration.TimeoutSeconds),
            MaxConcurrency = Math.Max(1, adapter.Capabilities.MaxConcurrency)
        };
    }

    private async Task<ConnectionTestResult> RunConnectionTestAsync(
        AdapterDescriptorDto adapterDescriptor,
        AdapterExecutionContext context,
        CancellationToken cancellationToken)
    {
        var adapter = _registry.GetAdapter(adapterDescriptor.AdapterType)
            ?? throw new IntegrationValidationException("ADAPTER_NOT_FOUND", $"Adapter '{adapterDescriptor.AdapterType}' is not registered.");

        try
        {
            return await adapter.TestConnectionAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // An HTTP/LDAP timeout surfaces as a cancellation the caller did not request.
            return new ConnectionTestResult
            {
                Success = false,
                Message = "The source system did not respond within the connection timeout.",
                ErrorCode = "CONNECTION_TIMEOUT",
                ErrorDetail = ex.Message
            };
        }
        catch (AdapterConfigurationException ex)
        {
            return new ConnectionTestResult
            {
                Success = false,
                Message = "Connection test rejected the integration configuration.",
                ErrorCode = ex.Code,
                ErrorDetail = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected adapter failure while testing {AdapterType}", adapter.AdapterType);
            return new ConnectionTestResult
            {
                Success = false,
                Message = "Connection test failed unexpectedly.",
                ErrorCode = "ADAPTER_TEST_FAILED",
                ErrorDetail = ex.Message
            };
        }
    }

    private async Task<Dictionary<Guid, DateTime>> LastJobStartsAsync(List<Guid> integrationIds, CancellationToken cancellationToken)
    {
        if (integrationIds.Count == 0)
            return new Dictionary<Guid, DateTime>();

        return await _context.DiscoveryJobs
            .AsNoTracking()
            .Where(j => integrationIds.Contains(j.IntegrationId))
            .GroupBy(j => j.IntegrationId)
            .Select(g => new { IntegrationId = g.Key, LastStartedAt = g.Max(j => j.StartedAt) })
            .ToDictionaryAsync(x => x.IntegrationId, x => x.LastStartedAt, cancellationToken);
    }

    private IntegrationDto MapIntegration(Integration integration, DateTime? lastJobStartedAt)
    {
        DateTime? nextScheduledRunAt = null;
        if (integration.IsEnabled && !integration.CollectorManaged && CronSchedule.TryParse(integration.ScheduleCron, out var schedule, out _))
            nextScheduledRunAt = _schedule.NextRun(schedule, integration.CreatedAt, lastJobStartedAt);

        var descriptor = _registry.GetDescriptors()
            .FirstOrDefault(d => d.AdapterType.Equals(integration.AdapterType, StringComparison.OrdinalIgnoreCase));

        var configuration = SanitizeForOutput(integration.ConfigurationJson, descriptor);
        return new IntegrationDto
        {
            Id = integration.Id,
            Name = integration.Name,
            Description = integration.Description,
            AdapterType = integration.AdapterType,
            AdapterVersion = integration.AdapterVersion,
            Status = integration.Status.ToString(),
            IsEnabled = integration.IsEnabled,
            CollectorManaged = integration.CollectorManaged,
            ScheduleCron = integration.ScheduleCron,
            NextScheduledRunAt = nextScheduledRunAt,
            ScheduleTimeZone = _schedule.TimeZone.Id,
            TimeoutSeconds = integration.TimeoutSeconds,
            ConfigurationVersion = integration.ConfigurationVersion,
            Configuration = configuration,
            Endpoint = BuildEndpoint(configuration),
            LastSyncAt = integration.LastSyncAt,
            LastTestAt = integration.LastTestAt,
            LastTestSuccess = integration.LastTestSuccess,
            LastError = integration.LastError,
            LastErrorAt = integration.LastErrorAt,
            DiscoveredEntitiesCount = integration.DiscoveredEntitiesCount,
            LastJobId = integration.LastJobId,
            CreatedAt = integration.CreatedAt,
            UpdatedAt = integration.UpdatedAt,
            Adapter = descriptor
        };
    }

    private static JsonElement SanitizeForOutput(string configurationJson, AdapterDescriptorDto? descriptor)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(string.IsNullOrWhiteSpace(configurationJson) ? "{}" : configurationJson) as JsonObject
                ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        var secretKeys = new HashSet<string>(CommonSecretKeys, StringComparer.OrdinalIgnoreCase);
        if (descriptor != null)
        {
            foreach (var field in descriptor.ConfigurationSchema.SecretFields)
                secretKeys.Add(field);
        }

        foreach (var key in root.Select(kv => kv.Key).ToList())
        {
            if (secretKeys.Contains(key))
                root.Remove(key);
        }

        return JsonSerializer.SerializeToElement(root);
    }

    private static string BuildEndpoint(JsonElement configuration)
    {
        if (configuration.ValueKind != JsonValueKind.Object)
            return string.Empty;

        if (configuration.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            return url.GetString() ?? string.Empty;

        var server = GetString(configuration, "server")
            ?? GetString(configuration, "host")
            ?? GetString(configuration, "hostname")
            ?? string.Empty;

        var port = GetInt(configuration, "port");
        return port > 0 && server.Length > 0 ? $"{server}:{port}" : server;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return 0;
    }

    private static DiscoveryJobDto MapJob(DiscoveryJob job, Integration integration)
    {
        return new DiscoveryJobDto
        {
            Id = job.Id,
            IntegrationId = job.IntegrationId,
            IntegrationName = integration?.Name ?? string.Empty,
            AdapterType = integration?.AdapterType ?? string.Empty,
            JobType = job.JobType,
            Status = job.Status.ToString(),
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            EntitiesDiscovered = job.EntitiesDiscovered,
            RelationshipsDiscovered = job.RelationshipsDiscovered,
            ProgressPercent = job.ProgressPercent,
            CurrentPhase = job.CurrentPhase,
            CurrentMessage = job.CurrentMessage,
            RequestedBy = job.RequestedBy,
            DurationMs = job.DurationMs,
            Errors = ParseStringList(job.ErrorsJson),
            Warnings = ParseStringList(job.WarningsJson),
            Statistics = ParseIntDictionary(job.StatisticsJson)
        };
    }

    internal static List<string> ParseStringList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    internal static Dictionary<string, int> ParseIntDictionary(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new Dictionary<string, int>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, int>();
        }
    }

    private string GetIpAddress()
    {
        // Controllers pass the actor; the HTTP context is not available here.
        return string.Empty;
    }
}
