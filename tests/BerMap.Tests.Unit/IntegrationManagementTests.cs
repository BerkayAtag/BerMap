using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using BerMap.Infrastructure.Integrations;
using BerMap.Infrastructure.Security;
using BerMap.Infrastructure.Services;
using BerMap.Integrations.ActiveDirectory;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BerMap.Tests.Unit;

/// <summary>Integration lifecycle: status semantics, validation, collector-managed mode and deletion.</summary>
public sealed class IntegrationManagementTests : IDisposable
{
    private readonly string _vault = Path.Combine(Path.GetTempPath(), "bermap-int-" + Guid.NewGuid().ToString("N"));
    private readonly BerMapDbContext _context = new(new DbContextOptionsBuilder<BerMapDbContext>()
        .UseInMemoryDatabase($"integrations-{Guid.NewGuid():N}")
        .Options);
    private readonly DiscoveryScheduleSettings _settings = new() { TimeZone = TimeZoneInfo.Utc };

    public void Dispose()
    {
        _context.Dispose();
        if (Directory.Exists(_vault))
            Directory.Delete(_vault, recursive: true);
    }

    private IntegrationService CreateService() => new(
        _context,
        new IntegrationAdapterRegistry(new IIntegrationAdapter[] { new ActiveDirectoryAdapter() }),
        new CredentialStore(_vault),
        new AuditService(_context, new HttpContextAccessor()),
        new NoopJobRunner(),
        NullLogger<IntegrationService>.Instance,
        _settings);

    private static Dictionary<string, JsonElement> Config(object values) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    private static CreateIntegrationRequest ValidRequest(bool isEnabled = true) => new()
    {
        Name = "AD " + Guid.NewGuid().ToString("N")[..6],
        AdapterType = "ActiveDirectory",
        Configuration = Config(new { server = "dc01.corp.local", port = 636, username = "svc@corp.local" }),
        Secret = "not-a-real-secret",
        IsEnabled = isEnabled
    };

    [Theory]
    [InlineData(true, null, IntegrationStatus.Active)]
    [InlineData(true, IntegrationStatus.Active, IntegrationStatus.Active)]
    [InlineData(true, IntegrationStatus.Error, IntegrationStatus.Error)]
    [InlineData(true, IntegrationStatus.Degraded, IntegrationStatus.Degraded)]
    [InlineData(false, null, IntegrationStatus.Inactive)]
    [InlineData(false, IntegrationStatus.Error, IntegrationStatus.Inactive)]
    public void RestingStatus_FollowsEnabledFlagAndLastOutcome(bool enabled, IntegrationStatus? lastOutcome, IntegrationStatus expected)
    {
        Assert.Equal(expected, IntegrationStatusRules.Resting(enabled, lastOutcome));
    }

    [Fact]
    public void AfterRun_DisabledIntegrationStaysInactive()
    {
        Assert.Equal(IntegrationStatus.Degraded, IntegrationStatusRules.AfterRun(true, IntegrationStatus.Degraded));
        Assert.Equal(IntegrationStatus.Inactive, IntegrationStatusRules.AfterRun(false, IntegrationStatus.Error));
    }

    [Fact]
    public async Task Create_EnabledIsActive_DisabledIsInactive()
    {
        var service = CreateService();

        Assert.Equal("Active", (await service.CreateAsync(ValidRequest(), "admin")).Status);
        Assert.Equal("Inactive", (await service.CreateAsync(ValidRequest(isEnabled: false), "admin")).Status);
    }

    [Fact]
    public async Task ReEnabling_KeepsAFailedIntegrationInError()
    {
        var service = CreateService();
        var created = await service.CreateAsync(ValidRequest(), "admin");
        var entity = await _context.Integrations.SingleAsync(i => i.Id == created.Id);
        entity.Status = IntegrationStatus.Error;
        entity.LastError = "LDAP_BIND: invalid credentials";
        await _context.SaveChangesAsync();

        var disabled = await service.UpdateAsync(created.Id, new UpdateIntegrationRequest { IsEnabled = false }, "admin");
        Assert.Equal("Inactive", disabled!.Status);

        var enabled = await service.UpdateAsync(created.Id, new UpdateIntegrationRequest { IsEnabled = true }, "admin");
        Assert.Equal("Error", enabled!.Status);

        entity.LastError = string.Empty;
        await _context.SaveChangesAsync();
        await service.UpdateAsync(created.Id, new UpdateIntegrationRequest { IsEnabled = false }, "admin");
        var healthy = await service.UpdateAsync(created.Id, new UpdateIntegrationRequest { IsEnabled = true }, "admin");
        Assert.Equal("Active", healthy!.Status);
    }

    [Fact]
    public async Task Create_RejectsMissingRequiredFieldsSecretAndUrlServer()
    {
        var service = CreateService();

        var missingConfig = ValidRequest();
        missingConfig.Configuration = new();
        var config = await Assert.ThrowsAsync<IntegrationValidationException>(() => service.CreateAsync(missingConfig, "admin"));
        Assert.Equal("CONFIG_REQUIRED", config.Code);
        Assert.Contains("server", config.Message);
        Assert.Contains("username", config.Message);

        var missingSecret = ValidRequest();
        missingSecret.Secret = null;
        Assert.Equal("SECRET_REQUIRED",
            (await Assert.ThrowsAsync<IntegrationValidationException>(() => service.CreateAsync(missingSecret, "admin"))).Code);

        var url = ValidRequest();
        url.Configuration = Config(new { server = "https://dc01.corp.local/", username = "svc@corp.local" });
        Assert.Equal("CONFIG_INVALID_SERVER",
            (await Assert.ThrowsAsync<IntegrationValidationException>(() => service.CreateAsync(url, "admin"))).Code);

        Assert.Empty(_context.Integrations);
    }

    [Fact]
    public async Task CollectorManaged_NeedsNoServerCredential_AndIsNeverRunByTheServer()
    {
        var service = CreateService();
        var created = await service.CreateAsync(new CreateIntegrationRequest
        {
            Name = "AD via collector",
            AdapterType = "ActiveDirectory",
            CollectorManaged = true,
            ScheduleCron = "*/5 * * * *"
        }, "admin");

        Assert.True(created.CollectorManaged);
        Assert.Equal("Active", created.Status);
        Assert.Null(created.NextScheduledRunAt);

        Assert.Equal("COLLECTOR_MANAGED", (await Assert.ThrowsAsync<IntegrationValidationException>(
            () => service.TestAsync(created.Id, null, "admin"))).Code);
        Assert.Equal("COLLECTOR_MANAGED", (await Assert.ThrowsAsync<IntegrationValidationException>(
            () => service.StartDiscoveryAsync(created.Id, new StartDiscoveryRequest(), "admin"))).Code);

        // A schedule that is long overdue still queues nothing.
        var entity = await _context.Integrations.SingleAsync(i => i.Id == created.Id);
        entity.CreatedAt = DateTime.UtcNow.AddDays(-2);
        await _context.SaveChangesAsync();
        var scheduler = new DiscoveryScheduler(_context, service, _settings, NullLogger<DiscoveryScheduler>.Instance);
        Assert.Empty(await scheduler.RunOnceAsync(DateTime.UtcNow));
        Assert.Empty(_context.DiscoveryJobs);
    }

    [Fact]
    public async Task Update_AuditRecordsChangedFields_ButNeverTheSecret()
    {
        var service = CreateService();
        var created = await service.CreateAsync(ValidRequest(), "admin");

        await service.UpdateAsync(created.Id, new UpdateIntegrationRequest
        {
            IsEnabled = false,
            ScheduleCron = "0 3 * * *",
            Configuration = Config(new { server = "dc02.corp.local", port = 636, username = "svc@corp.local" }),
            Secret = "rotated-secret-value"
        }, "admin");

        var audit = await _context.AuditLogs.SingleAsync(a => a.Action == "IntegrationUpdated");
        Assert.DoesNotContain("rotated-secret-value", audit.DetailsJson);

        using var details = JsonDocument.Parse(audit.DetailsJson);
        var changes = details.RootElement.GetProperty("changes");
        Assert.False(changes.GetProperty("isEnabled").GetProperty("to").GetBoolean());
        Assert.Equal("0 3 * * *", changes.GetProperty("scheduleCron").GetProperty("to").GetString());
        Assert.Equal("server", Assert.Single(details.RootElement.GetProperty("configurationKeysChanged").EnumerateArray()).GetString());
        Assert.True(details.RootElement.GetProperty("secretRotated").GetBoolean());
    }

    [Fact]
    public async Task Delete_StalesItsDataAndClosesOpenJobs()
    {
        var service = CreateService();
        var deleted = await service.CreateAsync(ValidRequest(), "admin");
        var other = await service.CreateAsync(ValidRequest(), "admin");

        var onlyHere = new InfrastructureNode { Name = "only-here", NodeType = NodeType.Computer, Status = "healthy" };
        var shared = new InfrastructureNode { Name = "shared", NodeType = NodeType.Computer, Status = "healthy" };
        _context.InfrastructureNodes.AddRange(onlyHere, shared);
        _context.NodeSourceLinks.AddRange(
            new NodeSourceLink { IntegrationId = deleted.Id, NodeId = onlyHere.Id, ExternalId = "a" },
            new NodeSourceLink { IntegrationId = deleted.Id, NodeId = shared.Id, ExternalId = "b" },
            new NodeSourceLink { IntegrationId = other.Id, NodeId = shared.Id, ExternalId = "c" });
        _context.InfrastructureRelationships.Add(new InfrastructureRelationship
        {
            SourceNodeId = onlyHere.Id,
            TargetNodeId = shared.Id,
            RelationshipType = RelationshipType.MemberOf,
            IntegrationId = deleted.Id
        });
        var queued = new DiscoveryJob { IntegrationId = deleted.Id, Status = DiscoveryJobStatus.Queued, JobType = "Full" };
        var collector = new DiscoveryJob { IntegrationId = deleted.Id, Status = DiscoveryJobStatus.Running, JobType = "Collector" };
        var done = new DiscoveryJob { IntegrationId = deleted.Id, Status = DiscoveryJobStatus.Completed, JobType = "Full" };
        _context.DiscoveryJobs.AddRange(queued, collector, done);
        await _context.SaveChangesAsync();

        Assert.True(await service.DeleteAsync(deleted.Id, "admin"));

        Assert.All(_context.NodeSourceLinks.Where(l => l.IntegrationId == deleted.Id), l => Assert.True(l.IsStale));
        Assert.False(_context.NodeSourceLinks.Single(l => l.IntegrationId == other.Id).IsStale);
        Assert.True((await _context.InfrastructureNodes.SingleAsync(n => n.Id == onlyHere.Id)).IsStale);
        Assert.False((await _context.InfrastructureNodes.SingleAsync(n => n.Id == shared.Id)).IsStale);
        Assert.True(_context.InfrastructureRelationships.Single().IsStale);

        Assert.Equal(DiscoveryJobStatus.Cancelled, (await _context.DiscoveryJobs.SingleAsync(j => j.Id == queued.Id)).Status);
        Assert.Equal(DiscoveryJobStatus.Cancelled, (await _context.DiscoveryJobs.SingleAsync(j => j.Id == collector.Id)).Status);
        Assert.Equal(DiscoveryJobStatus.Completed, (await _context.DiscoveryJobs.SingleAsync(j => j.Id == done.Id)).Status);
        Assert.Null(await service.GetAsync(deleted.Id));
    }

    [Fact]
    public async Task CancelJob_DistinguishesUnknownFromFinishedJobs()
    {
        var service = CreateService();
        var created = await service.CreateAsync(ValidRequest(), "admin");
        var done = new DiscoveryJob { IntegrationId = created.Id, Status = DiscoveryJobStatus.Completed, JobType = "Full" };
        var queued = new DiscoveryJob { IntegrationId = created.Id, Status = DiscoveryJobStatus.Queued, JobType = "Full" };
        _context.DiscoveryJobs.AddRange(done, queued);
        await _context.SaveChangesAsync();

        Assert.Equal(CancelJobOutcome.NotFound, await service.CancelJobAsync(Guid.NewGuid(), "admin"));
        Assert.Equal(CancelJobOutcome.NotCancellable, await service.CancelJobAsync(done.Id, "admin"));
        Assert.Equal(CancelJobOutcome.Cancelled, await service.CancelJobAsync(queued.Id, "admin"));
    }

    private sealed class NoopJobRunner : IDiscoveryJobRunner
    {
        public Task RunJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public bool RequestCancellation(Guid jobId) => false;
    }
}
