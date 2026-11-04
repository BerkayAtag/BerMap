using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Application.Services.Integrations;
using BerMap.Contracts;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using BerMap.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BerMap.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CollectorsController : ControllerBase
{
    private readonly BerMapDbContext _context;
    private readonly IDiscoveryIngestionService _ingestionService;
    private readonly IConfiguration _configuration;
    private readonly IAuditService _auditService;

    // In-memory heartbeat cache for sub-second status retrieval. Durable state
    // is persisted on the collector row, so a restart loses only the last few
    // seconds of resource metrics.
    private static readonly ConcurrentDictionary<Guid, CollectorHeartbeat> Heartbeats = new();

    public CollectorsController(
        BerMapDbContext context,
        IDiscoveryIngestionService ingestionService,
        IConfiguration configuration,
        IAuditService auditService)
    {
        _context = context;
        _ingestionService = ingestionService;
        _configuration = configuration;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<IActionResult> GetCollectors(CancellationToken cancellationToken)
    {
        var collectors = await _context.Collectors
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

        var result = collectors.Select(c =>
        {
            Heartbeats.TryGetValue(c.Id, out var heartbeat);
            var isOnline = (DateTime.UtcNow - c.LastHeartbeatAt).TotalSeconds < 90;

            var adapters = ParseStringList(c.SupportedAdaptersJson);
            var metrics = ParseMetrics(c.QueueMetricsJson);

            return new
            {
                id = c.Id,
                name = c.Name,
                hostname = c.Hostname,
                ipAddress = c.IPAddress,
                status = isOnline ? "Online" : "Offline",
                version = c.Version,
                lastHeartbeatAt = c.LastHeartbeatAt,
                supportedAdapters = adapters,
                // A collector that never sent a heartbeat has no measured
                // metrics; null keeps the console from showing 0% as a value.
                metrics = heartbeat != null
                    ? new
                    {
                        cpuUsagePercent = heartbeat.CpuUsagePercent,
                        memoryUsagePercent = heartbeat.MemoryUsagePercent,
                        queuedMessages = heartbeat.QueuedMessagesCount
                    }
                    : metrics.ContainsKey("cpuUsagePercent")
                        ? new
                        {
                            cpuUsagePercent = metrics.GetValueOrDefault("cpuUsagePercent"),
                            memoryUsagePercent = metrics.GetValueOrDefault("memoryUsagePercent"),
                            queuedMessages = (int)metrics.GetValueOrDefault("queuedMessages")
                        }
                        : null
            };
        });

        return Ok(result);
    }

    /// <summary>
    /// Removes a retired or re-enrolled collector. Its discovered data stays:
    /// it belongs to the integrations the collector fed, not to the collector.
    /// </summary>
    [Authorize(Policy = AuthorizationPolicies.ManageIntegrations)]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteCollector(Guid id, CancellationToken cancellationToken)
    {
        var collector = await _context.Collectors.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (collector == null)
            return NotFound();

        _context.Collectors.Remove(collector);
        await _context.SaveChangesAsync(cancellationToken);
        Heartbeats.TryRemove(id, out _);

        await _auditService.LogAsync(
            this.GetActor(), this.GetActor(), "Collector.Deleted", nameof(Collector), id.ToString(),
            JsonSerializer.Serialize(new { collector.Name, collector.Hostname }),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty, true, cancellationToken);

        return NoContent();
    }

    [AllowAnonymous]
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(
        [FromBody] CollectorHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
        if (RejectCollectorRequest() is { } rejected)
            return rejected;

        if (heartbeat.CollectorId == Guid.Empty)
            return BadRequest(new { message = "CollectorId is required." });

        var collector = await _context.Collectors
            .FirstOrDefaultAsync(c => c.Id == heartbeat.CollectorId, cancellationToken);

        if (collector == null)
        {
            return NotFound(new
            {
                message = "Collector is not registered. Register the collector before sending heartbeats."
            });
        }

        Heartbeats[heartbeat.CollectorId] = heartbeat;

        collector.LastHeartbeatAt = DateTime.UtcNow;
        collector.Status = CollectorStatus.Online;
        if (!string.IsNullOrWhiteSpace(heartbeat.Version))
            collector.Version = heartbeat.Version;
        collector.QueueMetricsJson = JsonSerializer.Serialize(new Dictionary<string, double>
        {
            ["cpuUsagePercent"] = heartbeat.CpuUsagePercent,
            ["memoryUsagePercent"] = heartbeat.MemoryUsagePercent,
            ["queuedMessages"] = heartbeat.QueuedMessagesCount
        });

        await _context.SaveChangesAsync(cancellationToken);

        return Ok(new { acknowledged = true, serverTime = DateTime.UtcNow });
    }

    [AllowAnonymous]
    [HttpPost("submit-batch")]
    public async Task<IActionResult> SubmitBatch(
        [FromBody] DiscoveryResult batch,
        [FromQuery] Guid? jobId,
        [FromQuery] bool final,
        CancellationToken cancellationToken)
    {
        if (RejectCollectorRequest() is { } rejected)
            return rejected;

        // Collections sent as JSON null would otherwise surface as 500s deep in ingestion.
        batch.Relationships ??= new();
        batch.Errors ??= new();
        batch.Warnings ??= new();
        batch.Statistics ??= new();
        foreach (var node in batch.Nodes ?? new())
            node.Properties ??= new();
        foreach (var relationship in batch.Relationships)
            relationship.Properties ??= new();

        if (batch.Nodes == null || batch.Nodes.Count == 0)
            return BadRequest(new { message = "Discovery batch contains no nodes." });

        if (!batch.IntegrationId.HasValue || batch.IntegrationId.Value == Guid.Empty)
        {
            return BadRequest(new
            {
                message = "DiscoveryResult.integrationId is required for collector-submitted batches."
            });
        }

        var integrationId = batch.IntegrationId.Value;
        var integrationExists = await _context.Integrations
            .AsNoTracking()
            .AnyAsync(i => i.Id == integrationId && i.DeletedAt == null, cancellationToken);

        if (!integrationExists)
            return NotFound(new { message = $"Integration '{integrationId}' was not found." });

        // Protocol: a batch without jobId opens a collector job. final=true
        // completes it (a single-batch snapshot sends final=true right away);
        // otherwise the job stays open and further batches carry ?jobId=.
        DiscoveryJob job;
        if (jobId.HasValue)
        {
            job = await _context.DiscoveryJobs
                .FirstOrDefaultAsync(j => j.Id == jobId.Value && j.IntegrationId == integrationId, cancellationToken)
                ?? throw new KeyNotFoundException($"Discovery job '{jobId}' was not found for this integration.");

            if (job.JobType != "Collector")
                return Conflict(new { message = $"Discovery job '{job.Id}' is not a collector job." });
            if (job.Status != DiscoveryJobStatus.Running)
                return Conflict(new { message = $"Discovery job '{job.Id}' is already {job.Status}." });
        }
        else
        {
            job = new DiscoveryJob
            {
                Id = Guid.NewGuid(),
                IntegrationId = integrationId,
                JobType = "Collector",
                Status = DiscoveryJobStatus.Running,
                StartedAt = DateTime.UtcNow,
                CurrentPhase = "Ingesting",
                CurrentMessage = "Collector batch received.",
                RequestedBy = "collector"
            };
            _context.DiscoveryJobs.Add(job);
            await _context.SaveChangesAsync(cancellationToken);
        }

        // Errors and warnings accumulate over every batch of the job: a final
        // batch that is clean does not make an earlier failed batch complete.
        var errors = ParseStringList(job.ErrorsJson).Concat(batch.Errors).ToList();
        if (!batch.Success && batch.Errors.Count == 0)
            errors.Add("A collector batch reported failure without details.");
        var isCompleteSnapshot = final && errors.Count == 0;

        // Ingestion is not tied to the HTTP request: a collector timing out must
        // not abort a half-written graph or leave the job open.
        IngestionResult ingestion;
        try
        {
            ingestion = await _ingestionService.IngestAsync(
                integrationId, job.Id, batch, isCompleteSnapshot, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _context.ChangeTracker.Clear();
            var failed = await _context.DiscoveryJobs.FirstAsync(j => j.Id == job.Id, CancellationToken.None);
            failed.Status = DiscoveryJobStatus.Failed;
            failed.CompletedAt = DateTime.UtcNow;
            failed.CurrentPhase = "Failed";
            failed.CurrentMessage = "Collector batch could not be ingested.";
            failed.ErrorsJson = JsonSerializer.Serialize(errors.Append($"INGESTION_FAILED: {ex.Message}"));
            await _context.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        var statistics = ParseCounts(job.StatisticsJson);
        foreach (var (key, value) in batch.Statistics)
            statistics[key] = statistics.GetValueOrDefault(key) + value;

        job.EntitiesDiscovered += batch.Nodes.Count;
        job.RelationshipsDiscovered += batch.Relationships.Count;
        job.ErrorsJson = JsonSerializer.Serialize(errors);
        job.WarningsJson = JsonSerializer.Serialize(
            ParseStringList(job.WarningsJson).Concat(batch.Warnings).Concat(ingestion.Warnings).Distinct());
        job.StatisticsJson = JsonSerializer.Serialize(statistics);
        job.UpdatedAt = DateTime.UtcNow;

        if (final)
        {
            job.Status = errors.Count == 0 ? DiscoveryJobStatus.Completed : DiscoveryJobStatus.PartialSuccess;
            job.ProgressPercent = 100;
            job.CurrentPhase = "Completed";
            job.CurrentMessage = errors.Count == 0 ? "Collector snapshot ingested." : "Collector snapshot ingested with errors.";
            job.CompletedAt = DateTime.UtcNow;
            job.DurationMs = (long)(job.CompletedAt.Value - job.StartedAt).TotalMilliseconds;

            // Collector-fed integrations report health the same way as
            // server-side discovery does, otherwise they stay "Inactive" forever.
            var integration = await _context.Integrations.FirstAsync(i => i.Id == integrationId, CancellationToken.None);
            integration.Status = IntegrationStatusRules.AfterRun(
                integration.IsEnabled, errors.Count == 0 ? IntegrationStatus.Active : IntegrationStatus.Degraded);
            integration.LastError = errors.FirstOrDefault() ?? string.Empty;
            integration.LastErrorAt = errors.Count > 0 ? DateTime.UtcNow : null;
            integration.LastJobId = job.Id;
            integration.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            job.CurrentPhase = "Ingesting";
            job.CurrentMessage = "Collector batch ingested; waiting for further batches.";
        }

        await _context.SaveChangesAsync(CancellationToken.None);

        return Ok(new
        {
            success = batch.Success,
            jobId = job.Id,
            jobCompleted = final,
            nodesCreated = ingestion.NodesCreated,
            nodesUpdated = ingestion.NodesUpdated,
            relationshipsCreated = ingestion.RelationshipsCreated,
            reviewsCreated = ingestion.ReviewsCreated,
            warnings = ingestion.Warnings,
            timestamp = DateTime.UtcNow
        });
    }

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> RegisterCollector(
        [FromBody] CollectorRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        if (RejectCollectorRequest() is { } rejected)
            return rejected;

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Hostname))
            return BadRequest(new { message = "Collector name and hostname are required." });

        // A collector that lost its persisted id re-enrolls under the same
        // name and host; reuse that row instead of listing it twice.
        var name = request.Name.Trim();
        var hostname = request.Hostname.Trim();
        var existing = await _context.Collectors
            .FirstOrDefaultAsync(c => c.Name == name && c.Hostname == hostname, cancellationToken);
        if (existing != null)
        {
            existing.IPAddress = request.IpAddress?.Trim() ?? existing.IPAddress;
            existing.Version = string.IsNullOrWhiteSpace(request.Version) ? existing.Version : request.Version.Trim();
            existing.SupportedAdaptersJson = JsonSerializer.Serialize(request.SupportedAdapters ?? new List<string>());
            existing.LastHeartbeatAt = DateTime.UtcNow;
            existing.Status = CollectorStatus.Online;
            existing.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return Ok(new { registered = true, collectorId = existing.Id });
        }

        var registration = new Collector
        {
            Id = Guid.NewGuid(),
            Name = name,
            Hostname = hostname,
            IPAddress = request.IpAddress?.Trim() ?? string.Empty,
            Version = string.IsNullOrWhiteSpace(request.Version) ? "1.0.0" : request.Version.Trim(),
            SupportedAdaptersJson = JsonSerializer.Serialize(request.SupportedAdapters ?? new List<string>()),
            LastHeartbeatAt = DateTime.UtcNow,
            Status = CollectorStatus.Online
        };

        _context.Collectors.Add(registration);
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            registered = true,
            collectorId = registration.Id
        });
    }

    public sealed record CollectorRegistrationRequest(
        string Name,
        string Hostname,
        string? IpAddress,
        string? Version,
        List<string>? SupportedAdapters);

    /// <summary>
    /// Collector protocol calls are authenticated with the shared enrollment
    /// token (compared in constant time). Returns the error response to send,
    /// or null when the request may proceed.
    /// </summary>
    private IActionResult? RejectCollectorRequest()
    {
        var enrollmentToken = _configuration["BerMap:Collectors:EnrollmentToken"];
        if (string.IsNullOrWhiteSpace(enrollmentToken))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Collector enrollment is disabled. Configure BerMap:Collectors:EnrollmentToken."
            });
        }

        Request.Headers.TryGetValue("X-BerMap-Enrollment-Token", out var provided);
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(enrollmentToken));
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided.ToString()));
        return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash)
            ? null
            : Unauthorized(new { message = "Invalid collector enrollment token." });
    }

    private static List<string> ParseStringList(string json)
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

    private static Dictionary<string, int> ParseCounts(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static Dictionary<string, double> ParseMetrics(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
