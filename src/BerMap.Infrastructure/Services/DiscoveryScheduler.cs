using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Application.Services.Scheduling;
using BerMap.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BerMap.Infrastructure.Services;

/// <summary>Scheduler settings shared by the scheduler and the integration API.</summary>
public sealed class DiscoveryScheduleSettings
{
    public bool Enabled { get; init; } = true;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Time zone the cron expressions are evaluated in (server local time by default).</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    public static DiscoveryScheduleSettings FromConfiguration(IConfiguration configuration)
    {
        var timeZone = TimeZoneInfo.Local;
        var timeZoneId = configuration["BerMap:DiscoveryScheduler:TimeZone"];
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            // Accepts IANA ("Europe/Istanbul") and Windows ("Turkey Standard Time") ids on every OS.
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
        }

        return new DiscoveryScheduleSettings
        {
            Enabled = configuration.GetValue("BerMap:DiscoveryScheduler:Enabled", true),
            PollInterval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("BerMap:DiscoveryScheduler:PollSeconds", 30), 5, 600)),
            TimeZone = timeZone
        };
    }

    /// <summary>
    /// When the integration's next scheduled discovery is due: the first cron
    /// occurrence after its last discovery started (or after it was created).
    /// A value in the past means the run is due and will be queued shortly.
    /// </summary>
    public DateTime? NextRun(CronSchedule schedule, DateTime integrationCreatedAt, DateTime? lastJobStartedAt)
    {
        var anchor = lastJobStartedAt.HasValue && lastJobStartedAt.Value > integrationCreatedAt
            ? lastJobStartedAt.Value
            : integrationCreatedAt;
        return schedule.GetNextOccurrence(DateTime.SpecifyKind(anchor, DateTimeKind.Utc), TimeZone);
    }
}

/// <summary>
/// Queues scheduled discovery jobs. One pass looks at every enabled
/// integration and queues a full discovery for each whose schedule is due.
/// Only one occurrence is ever caught up after downtime, so a restart never
/// produces a burst of missed runs; a manual discovery counts as the run and
/// pushes the next scheduled one to the following occurrence.
/// </summary>
public sealed class DiscoveryScheduler
{
    public const string Actor = "scheduler";

    // Arbitrary constant shared by all BerMap instances on the same database.
    private const long AdvisoryLockKey = 0x42_65_72_4D_61_70_53_63; // "BerMapSc"

    private readonly BerMapDbContext _context;
    private readonly IIntegrationService _integrations;
    private readonly DiscoveryScheduleSettings _settings;
    private readonly ILogger<DiscoveryScheduler> _logger;

    public DiscoveryScheduler(
        BerMapDbContext context,
        IIntegrationService integrations,
        DiscoveryScheduleSettings settings,
        ILogger<DiscoveryScheduler> logger)
    {
        _context = context;
        _integrations = integrations;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Runs one scheduling pass and returns the ids of the jobs it queued.</summary>
    public async Task<IReadOnlyList<Guid>> RunOnceAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        var queued = new List<Guid>();

        // Several API instances may share the database: the transaction-scoped
        // advisory lock lets exactly one of them run a pass at a time.
        await using var transaction = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (transaction != null)
        {
            var acquired = await _context.Database
                .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({AdvisoryLockKey}) AS \"Value\"")
                .SingleAsync(cancellationToken);
            if (!acquired)
                return queued;
        }

        var integrations = await _context.Integrations
            .AsNoTracking()
            // Collector-managed integrations are discovered by their collector,
            // never by the server.
            .Where(i => i.IsEnabled && !i.CollectorManaged && i.DeletedAt == null && i.ScheduleCron != string.Empty)
            .Select(i => new { i.Id, i.Name, i.ScheduleCron, i.CreatedAt })
            .ToListAsync(cancellationToken);

        if (integrations.Count == 0)
            return queued;

        var ids = integrations.Select(i => i.Id).ToList();
        var lastStarts = await _context.DiscoveryJobs
            .AsNoTracking()
            .Where(j => ids.Contains(j.IntegrationId))
            .GroupBy(j => j.IntegrationId)
            .Select(g => new { IntegrationId = g.Key, LastStartedAt = g.Max(j => j.StartedAt) })
            .ToDictionaryAsync(x => x.IntegrationId, x => x.LastStartedAt, cancellationToken);

        foreach (var integration in integrations)
        {
            if (!CronSchedule.TryParse(integration.ScheduleCron, out var schedule, out var error))
            {
                _logger.LogWarning("Integration {IntegrationName} ({IntegrationId}) has an invalid schedule '{Schedule}': {Error}",
                    integration.Name, integration.Id, integration.ScheduleCron, error);
                continue;
            }

            var nextRun = _settings.NextRun(schedule, integration.CreatedAt,
                lastStarts.TryGetValue(integration.Id, out var started) ? started : null);
            if (nextRun == null || nextRun > utcNow)
                continue;

            try
            {
                var job = await _integrations.StartDiscoveryAsync(
                    integration.Id, new StartDiscoveryRequest { JobType = "Full" }, Actor, cancellationToken);
                if (job == null)
                    continue;

                queued.Add(job.Id);
                _logger.LogInformation("Scheduled discovery {JobId} queued for {IntegrationName} (schedule '{Schedule}', due {DueAt:u}).",
                    job.Id, integration.Name, integration.ScheduleCron, nextRun);
            }
            catch (IntegrationValidationException ex) when (ex.Code == "DISCOVERY_ALREADY_ACTIVE")
            {
                // The running job counts as this occurrence; once it finishes the
                // next occurrence after its start decides the following run.
                _logger.LogDebug("Scheduled discovery for {IntegrationName} skipped: a job is already active.", integration.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Scheduled discovery for {IntegrationName} ({IntegrationId}) could not be queued.",
                    integration.Name, integration.Id);
            }
        }

        if (transaction != null)
            await transaction.CommitAsync(cancellationToken);

        return queued;
    }
}

/// <summary>Runs <see cref="DiscoveryScheduler"/> on a fixed interval.</summary>
public sealed class DiscoverySchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DiscoveryScheduleSettings _settings;
    private readonly ILogger<DiscoverySchedulerService> _logger;

    public DiscoverySchedulerService(
        IServiceScopeFactory scopeFactory,
        DiscoveryScheduleSettings settings,
        ILogger<DiscoverySchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Scheduled discovery is disabled by configuration.");
            return;
        }

        _logger.LogInformation("Discovery scheduler started (every {Interval}s, time zone {TimeZone}).",
            _settings.PollInterval.TotalSeconds, _settings.TimeZone.Id);

        using var timer = new PeriodicTimer(_settings.PollInterval);
        do
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var scheduler = scope.ServiceProvider.GetRequiredService<DiscoveryScheduler>();
                await scheduler.RunOnceAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A database outage must not stop scheduling for good.
                _logger.LogError(ex, "Discovery scheduler pass failed; retrying on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
