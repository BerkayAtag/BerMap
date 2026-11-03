using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.Interfaces;
using BerMap.Contracts;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
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
                        memoryUsagePercent = metrics.GetValueOrDefault("memoryUsagePercent") > 0
                            ? metrics.GetValueOrDefault("memoryUsagePercent")
                            : metrics.GetValueOrDefault("memoryUsageMb"),
                        queuedMessages = (int)metrics.GetValueOrDefault("queuedMessages")
                    }
            };
        });

        return Ok(result);
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(
        [FromBody] CollectorHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
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

    [HttpPost("submit-batch")]
    public async Task<IActionResult> SubmitBatch(
        [FromBody] DiscoveryResult batch,
        [FromQuery] Guid? jobId,
        CancellationToken cancellationToken)
    {
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

        var ingestion = await _ingestionService.IngestAsync(integrationId, job.Id, batch, cancellationToken);

        job.EntitiesDiscovered = batch.Nodes.Count;
        job.RelationshipsDiscovered = batch.Relationships.Count;
        job.ProgressPercent = 100;
        job.CurrentPhase = "Completed";
        job.CurrentMessage = batch.Success ? "Collector batch ingested." : "Collector batch ingested with errors.";
        job.CompletedAt = DateTime.UtcNow;
        job.DurationMs = (long)(job.CompletedAt.Value - job.StartedAt).TotalMilliseconds;
        job.Status = batch.Success ? DiscoveryJobStatus.Completed : DiscoveryJobStatus.PartialSuccess;
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

    [HttpPost("register")]
    public async Task<IActionResult> RegisterCollector(
        [FromBody] Collector registration,
        CancellationToken cancellationToken)
    {
        var enrollmentToken = _configuration["BerMap:Collectors:EnrollmentToken"];
        if (string.IsNullOrWhiteSpace(enrollmentToken))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Collector enrollment is disabled. Configure BerMap:Collectors:EnrollmentToken."
            });
        }

        if (!Request.Headers.TryGetValue("X-BerMap-Enrollment-Token", out var provided) ||
            !string.Equals(provided.ToString(), enrollmentToken, StringComparison.Ordinal))
        {
            return Unauthorized(new { message = "Invalid collector enrollment token." });
        }

        if (string.IsNullOrWhiteSpace(registration.Name) || string.IsNullOrWhiteSpace(registration.Hostname))
            return BadRequest(new { message = "Collector name and hostname are required." });

        registration.Id = Guid.NewGuid();
        registration.LastHeartbeatAt = DateTime.UtcNow;
        registration.Status = CollectorStatus.Online;

        _context.Collectors.Add(registration);
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            registered = true,
            collectorId = registration.Id
        });
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
