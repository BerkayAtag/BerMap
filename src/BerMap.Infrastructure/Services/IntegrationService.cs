using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
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
    private static readonly TimeSpan AbandonedCollectorJobAge = TimeSpan.FromMinutes(30);

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
        var configuration = BuildConfiguration(request.Configuration, adapter, rejectSecrets: true);
        ValidateSchedule(request.ScheduleCron);

        var id = Guid.NewGuid();
        var integration = new Integration
        {
            Id = id,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            AdapterType = adapter.AdapterType,
            AdapterVersion = adapter.Version,
            Status = IntegrationStatus.Inactive,
            IsEnabled = request.IsEnabled,
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
            JsonSerializer.Serialize(new { integration.AdapterType, integration.Name }),
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

        if (request.Name != null)
        {
            ValidateName(request.Name);
            integration.Name = request.Name.Trim();
        }

        if (request.Description != null)
            integration.Description = request.Description.Trim();

        if (request.Configuration != null)
        {
            var configuration = BuildConfiguration(request.Configuration, adapter, rejectSecrets: true);
            integration.ConfigurationJson = configuration.ToJsonString();
            integration.ConfigurationVersion++;
        }

        if (request.ScheduleCron != null)
        {
            ValidateSchedule(request.ScheduleCron);
            integration.ScheduleCron = request.ScheduleCron.Trim();
        }

        if (request.TimeoutSeconds.HasValue)
            integration.TimeoutSeconds = ClampTimeout(request.TimeoutSeconds.Value);

        if (request.IsEnabled.HasValue)
        {
            integration.IsEnabled = request.IsEnabled.Value;
            if (!integration.IsEnabled)
                integration.Status = IntegrationStatus.Inactive;
            else if (integration.Status == IntegrationStatus.Inactive)
                integration.Status = IntegrationStatus.Active;
        }

        if (request.ClearSecret)
        {
            if (!string.IsNullOrWhiteSpace(integration.CredentialReference))
                await _credentialStore.DeleteSecretAsync(integration.CredentialReference, cancellationToken);
            integration.CredentialReference = string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(request.Secret))
        {
            integration.CredentialReference = string.IsNullOrWhiteSpace(integration.CredentialReference)
                ? CredentialKey(id)
                : integration.CredentialReference;
            await _credentialStore.StoreSecretAsync(integration.CredentialReference, request.Secret, cancellationToken);
        }

        integration.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync(
            actor, actor, "IntegrationUpdated", nameof(Integration), integration.Id.ToString(),
            JsonSerializer.Serialize(new { integration.Name, integration.ConfigurationVersion }),
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

        var queuedJobs = await _context.DiscoveryJobs
            .Where(j => j.IntegrationId == id && j.Status == DiscoveryJobStatus.Queued)
            .ToListAsync(cancellationToken);

        foreach (var job in queuedJobs)
        {
            job.Status = DiscoveryJobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            job.CurrentPhase = "Cancelled";
            job.CurrentMessage = "Integration was deleted before the job started.";
        }

        await _context.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync(
            actor, actor, "IntegrationDeleted", nameof(Integration), id.ToString(),
            JsonSerializer.Serialize(new { integration.Name, integration.AdapterType }),
            GetIpAddress(), true, cancellationToken);

        return true;
    }

    public async Task<ConnectionTestResult> TestDraftAsync(
        TestConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var adapter = RequireAdapter(request.AdapterType);
        var configuration = BuildConfiguration(request.Configuration, adapter, rejectSecrets: true);

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
        integration.Status = result.Success
            ? (integration.IsEnabled ? IntegrationStatus.Active : IntegrationStatus.Inactive)
            : IntegrationStatus.Error;
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

        if (!integration.IsEnabled)
            throw new IntegrationValidationException("INTEGRATION_DISABLED", "This integration is disabled.");

        var adapter = RequireAdapter(integration.AdapterType);

        // Collector jobs stay Running between batches. One that has not
        // received a batch for a long time was abandoned by its collector and
        // must not block discovery of the integration forever.
        var abandonedBefore = DateTime.UtcNow - AbandonedCollectorJobAge;
        var abandoned = await _context.DiscoveryJobs
            .Where(j => j.IntegrationId == id && j.JobType == "Collector" &&
                        j.Status == DiscoveryJobStatus.Running &&
                        (j.UpdatedAt ?? j.StartedAt) < abandonedBefore)
            .ToListAsync(cancellationToken);
        foreach (var stale in abandoned)
        {
            stale.Status = DiscoveryJobStatus.Failed;
            stale.CompletedAt = DateTime.UtcNow;
            stale.CurrentPhase = "Failed";
            stale.CurrentMessage = "The collector stopped sending batches before the snapshot was completed.";
        }
        if (abandoned.Count > 0)
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

    public async Task<bool> CancelJobAsync(Guid jobId, string actor, CancellationToken cancellationToken = default)
    {
        var job = await _context.DiscoveryJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        if (job == null)
            return false;

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
                return false;
            }
        }
        else
        {
            return false;
        }

        await _context.SaveChangesAsync(cancellationToken);
        await _auditService.LogAsync(
            actor, actor, "DiscoveryCancelled", nameof(DiscoveryJob), jobId.ToString(),
            JsonSerializer.Serialize(new { job.IntegrationId }),
            GetIpAddress(), true, cancellationToken);

        return true;
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
        if (integration.IsEnabled && CronSchedule.TryParse(integration.ScheduleCron, out var schedule, out _))
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
