using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.Interfaces;
using BerMap.Contracts;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using BerMap.Infrastructure.Integrations;
using BerMap.Infrastructure.Security;
using BerMap.Infrastructure.Services;
using BerMap.Integrations.ActiveDirectory;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BerMap.Tests.Unit;

public sealed class DiscoverySchedulerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 4, 10, 20, 0, DateTimeKind.Utc);

    private readonly string _vault = Path.Combine(Path.GetTempPath(), "bermap-sched-" + Guid.NewGuid().ToString("N"));
    private readonly BerMapDbContext _context = new(new DbContextOptionsBuilder<BerMapDbContext>()
        .UseInMemoryDatabase($"scheduler-{Guid.NewGuid():N}")
        .Options);
    private readonly DiscoveryScheduleSettings _settings = new() { TimeZone = TimeZoneInfo.Utc };

    public void Dispose()
    {
        _context.Dispose();
        if (Directory.Exists(_vault))
            Directory.Delete(_vault, recursive: true);
    }

    private DiscoveryScheduler CreateScheduler()
    {
        var integrations = new IntegrationService(
            _context,
            new IntegrationAdapterRegistry(new IIntegrationAdapter[] { new ActiveDirectoryAdapter() }),
            new CredentialStore(_vault),
            new AuditService(_context, new HttpContextAccessor()),
            new NoopJobRunner(),
            NullLogger<IntegrationService>.Instance,
            _settings);

        return new DiscoveryScheduler(_context, integrations, _settings, NullLogger<DiscoveryScheduler>.Instance);
    }

    private async Task<Integration> AddIntegrationAsync(string cron, DateTime createdAt, bool enabled = true)
    {
        var integration = new Integration
        {
            Name = "AD " + Guid.NewGuid().ToString("N")[..6],
            AdapterType = "ActiveDirectory",
            ScheduleCron = cron,
            IsEnabled = enabled,
            Status = IntegrationStatus.Active,
            CreatedAt = createdAt
        };
        _context.Integrations.Add(integration);
        await _context.SaveChangesAsync();
        return integration;
    }

    private async Task AddJobAsync(Guid integrationId, DateTime startedAt, DiscoveryJobStatus status = DiscoveryJobStatus.Completed)
    {
        _context.DiscoveryJobs.Add(new DiscoveryJob
        {
            IntegrationId = integrationId,
            StartedAt = startedAt,
            Status = status,
            JobType = "Full"
        });
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task DueSchedule_QueuesOneScheduledJob()
    {
        var integration = await AddIntegrationAsync("0 * * * *", Now.AddDays(-2));
        await AddJobAsync(integration.Id, Now.AddHours(-1).AddMinutes(-30)); // 08:50, before the 09:00 and 10:00 runs

        var queued = await CreateScheduler().RunOnceAsync(Now);

        var job = await _context.DiscoveryJobs.SingleAsync(j => j.Id == Assert.Single(queued));
        Assert.Equal(DiscoveryJobStatus.Queued, job.Status);
        Assert.Equal(DiscoveryScheduler.Actor, job.RequestedBy);
    }

    [Fact]
    public async Task MissedRunsAfterDowntime_AreCaughtUpOnlyOnce()
    {
        var integration = await AddIntegrationAsync("*/5 * * * *", Now.AddDays(-10));
        await AddJobAsync(integration.Id, Now.AddDays(-3)); // dozens of missed occurrences since

        var scheduler = CreateScheduler();
        Assert.Single(await scheduler.RunOnceAsync(Now));

        // The queued job is the run for this slot; the next pass queues nothing more.
        Assert.Empty(await scheduler.RunOnceAsync(Now.AddSeconds(30)));
        Assert.Equal(2, await _context.DiscoveryJobs.CountAsync(j => j.IntegrationId == integration.Id));
    }

    [Fact]
    public async Task RecentDiscovery_PostponesUntilTheNextOccurrence()
    {
        var integration = await AddIntegrationAsync("0 */6 * * *", Now.AddDays(-2));
        await AddJobAsync(integration.Id, Now.AddHours(-3)); // 07:20, after the 06:00 run; next is 12:00

        Assert.Empty(await CreateScheduler().RunOnceAsync(Now));
    }

    [Fact]
    public async Task NewIntegration_DoesNotRunBeforeItsFirstOccurrence()
    {
        await AddIntegrationAsync("0 * * * *", Now.AddMinutes(-15)); // created 10:05, first run 11:00

        Assert.Empty(await CreateScheduler().RunOnceAsync(Now));
    }

    [Fact]
    public async Task DisabledInvalidOrBusyIntegrations_AreSkipped()
    {
        await AddIntegrationAsync("0 * * * *", Now.AddDays(-1), enabled: false);
        await AddIntegrationAsync("not a cron", Now.AddDays(-1));
        var busy = await AddIntegrationAsync("*/5 * * * *", Now.AddDays(-1));
        await AddJobAsync(busy.Id, Now.AddHours(-2), DiscoveryJobStatus.Running);

        Assert.Empty(await CreateScheduler().RunOnceAsync(Now));
    }

    [Fact]
    public void NextRun_UsesTheLaterOfCreationAndLastDiscovery()
    {
        var schedule = BerMap.Application.Services.Scheduling.CronSchedule.Parse("0 */6 * * *");

        Assert.Equal(Now.Date.AddHours(12), _settings.NextRun(schedule, Now.AddDays(-5), Now.AddHours(-1)));
        Assert.Equal(Now.Date.AddHours(12), _settings.NextRun(schedule, Now.AddHours(-1), null));
        Assert.Equal(Now.Date.AddHours(6), _settings.NextRun(schedule, Now.AddDays(-5), Now.AddHours(-5)));
    }

    private sealed class NoopJobRunner : IDiscoveryJobRunner
    {
        public Task RunJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public bool RequestCancellation(Guid jobId) => false;
    }
}
