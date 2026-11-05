using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.Interfaces;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BerMap.Infrastructure.Services;

/// <summary>
/// Polls the durable discovery queue and executes one job at a time per process.
/// The PostgreSQL claim uses FOR UPDATE SKIP LOCKED so multiple API/Worker
/// instances can share the same queue without executing a job twice.
/// </summary>
public sealed class DiscoveryBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly StartupReadiness _readiness;
    private readonly ILogger<DiscoveryBackgroundService> _logger;

    public DiscoveryBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        StartupReadiness readiness,
        ILogger<DiscoveryBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _readiness = readiness;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("BerMap:DiscoveryWorker:Enabled", true))
        {
            _logger.LogInformation("Discovery background worker is disabled by configuration.");
            return;
        }

        var pollSeconds = Math.Clamp(_configuration.GetValue("BerMap:DiscoveryWorker:PollSeconds", 2), 1, 60);
        var pollInterval = TimeSpan.FromSeconds(pollSeconds);

        // Jobs must not be claimed before the schema is current and
        // interrupted jobs have been recovered.
        try
        {
            await _readiness.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        _logger.LogInformation("Discovery background worker started (poll interval {PollInterval}s).", pollSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var jobId = await ClaimNextJobAsync(stoppingToken);
                if (jobId.HasValue)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var runner = scope.ServiceProvider.GetRequiredService<IDiscoveryJobRunner>();
                    await runner.RunJobAsync(jobId.Value, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discovery background worker loop failed; retrying after the poll interval.");
            }

            try
            {
                await Task.Delay(pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Discovery background worker stopped.");
    }

    private async Task<Guid?> ClaimNextJobAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BerMapDbContext>();

        if (!context.Database.IsNpgsql())
        {
            var queued = await context.DiscoveryJobs
                .Where(j => j.Status == DiscoveryJobStatus.Queued)
                .OrderBy(j => j.StartedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (queued == null)
                return null;

            queued.Status = DiscoveryJobStatus.Running;
            queued.StartedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return queued.Id;
        }

        var claimed = await context.Database.SqlQueryRaw<Guid>(
                """
                UPDATE "DiscoveryJobs"
                SET "Status" = {0}, "StartedAt" = {1}
                WHERE "Id" = (
                    SELECT "Id"
                    FROM "DiscoveryJobs"
                    WHERE "Status" = {2}
                    ORDER BY "StartedAt"
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING "Id" AS "Value"
                """,
                (int)DiscoveryJobStatus.Running,
                DateTime.UtcNow,
                (int)DiscoveryJobStatus.Queued)
            .ToListAsync(cancellationToken);

        return claimed.Count == 0 ? null : claimed[0];
    }
}
