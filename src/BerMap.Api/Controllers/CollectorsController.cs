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

    // In-memory heartbeat cache for sub-second status retrieval. Durable state
    // is persisted on the collector row, so a restart loses only the last few
    // seconds of resource metrics.
    private static readonly ConcurrentDictionary<Guid, CollectorHeartbeat> Heartbeats = new();

    public CollectorsController(
        BerMapDbContext context,
        IDiscoveryIngestionService ingestionService,
        IConfiguration configuration)
    {
        _context = context;
        _ingestionService = ingestionService;
        _configuration = configuration;
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
                metrics = heartbeat != null
                    ? new
                    {
                        cpuUsagePercent = heartbeat.CpuUsagePercent,
                        memoryUsagePercent = heartbeat.MemoryUsagePercent,
                        queuedMessages = heartbeat.QueuedMessagesCount
                    }
                    : new
                    {
                        cpuUsagePercent = metrics.GetValueOrDefault("cpuUsagePercent"),
                        memoryUsagePercent = metrics.GetValueOrDefault("memoryUsagePercent"),
                        queuedMessages = (int)metrics.GetValueOrDefault("queuedMessages")
                    }
            };
        });

        return Ok(result);
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

        DiscoveryJob job;
        if (jobId.HasValue)
        {
            job = await _context.DiscoveryJobs
                .FirstOrDefaultAsync(j => j.Id == jobId.Value && j.IntegrationId == integrationId, cancellationToken)
                ?? throw new KeyNotFoundException($"Discovery job '{jobId}' was not found for this integration.");
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
                RequestedBy = this.GetActor()
            };
            _context.DiscoveryJobs.Add(job);
            await _context.SaveChangesAsync(cancellationToken);
        }

        if (job.Status is not (DiscoveryJobStatus.Queued or DiscoveryJobStatus.Running))
            return Conflict(new { message = $"Discovery job '{job.Id}' is already {job.Status}." });

        // A collector sends one source in several batches; only the batch that
        // completes a successful full snapshot may mark unseen objects stale.
        var isCompleteSnapshot = final && batch.Success && batch.Errors.Count == 0;

        IngestionResult ingestion;
        try
        {
            ingestion = await _ingestionService.IngestAsync(
                integrationId, job.Id, batch, isCompleteSnapshot, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _context.ChangeTracker.Clear();
            var failed = await _context.DiscoveryJobs.FirstAsync(j => j.Id == job.Id, CancellationToken.None);
            failed.Status = DiscoveryJobStatus.Failed;
            failed.CompletedAt = DateTime.UtcNow;
            failed.CurrentPhase = "Failed";
            failed.CurrentMessage = "Collector batch could not be ingested.";
            failed.ErrorsJson = JsonSerializer.Serialize(new[] { $"INGESTION_FAILED: {ex.Message}" });
            await _context.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        job.EntitiesDiscovered += batch.Nodes.Count;
        job.RelationshipsDiscovered += batch.Relationships.Count;
        job.ProgressPercent = 100;
        job.CurrentPhase = "Completed";
        job.CurrentMessage = batch.Success ? "Collector batch ingested." : "Collector batch ingested with errors.";
        job.CompletedAt = DateTime.UtcNow;
        job.DurationMs = (long)(job.CompletedAt.Value - job.StartedAt).TotalMilliseconds;
        var batchComplete = final || !jobId.HasValue;
        job.Status = !batchComplete
            ? DiscoveryJobStatus.Running
            : batch.Success ? DiscoveryJobStatus.Completed : DiscoveryJobStatus.PartialSuccess;
        if (!batchComplete)
        {
            job.CompletedAt = null;
            job.DurationMs = null;
            job.CurrentPhase = "Ingesting";
            job.CurrentMessage = "Collector batch ingested; waiting for further batches.";
        }
        job.ErrorsJson = JsonSerializer.Serialize(batch.Errors);
        job.WarningsJson = JsonSerializer.Serialize(batch.Warnings.Concat(ingestion.Warnings));
        job.StatisticsJson = JsonSerializer.Serialize(batch.Statistics);

        await _context.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            success = batch.Success,
            jobId = job.Id,
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

        var registration = new Collector
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Hostname = request.Hostname.Trim(),
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
