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
using BerMap.Application.Services.Reconciliation;
using BerMap.Contracts;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using BerMap.Infrastructure.Integrations;
using BerMap.Infrastructure.Repositories;
using BerMap.Infrastructure.Security;
using BerMap.Infrastructure.Services;
using BerMap.Integrations.ActiveDirectory;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BerMap.Tests.Unit;

/// <summary>
/// Resting integration health, abandoned collector jobs, delete cleanup of
/// reviews, multi-source node status and the simulation's impact graph walk.
/// </summary>
public sealed class IntegrationHealthAndNodeStatusTests : IDisposable
{
    private readonly string _vault = Path.Combine(Path.GetTempPath(), "bermap-health-" + Guid.NewGuid().ToString("N"));
    private readonly BerMapDbContext _context = new(new DbContextOptionsBuilder<BerMapDbContext>()
        .UseInMemoryDatabase($"health-{Guid.NewGuid():N}")
        .Options);

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
        new DiscoveryScheduleSettings { TimeZone = TimeZoneInfo.Utc });

    private DiscoveryIngestionService CreateIngestion() => new(
        _context, new EntityReconciliationService(), NullLogger<DiscoveryIngestionService>.Instance);

    private async Task<Integration> AddIntegrationAsync(string adapterType = "ActiveDirectory")
    {
        var integration = new Integration
        {
            Name = adapterType + " " + Guid.NewGuid().ToString("N")[..6],
            AdapterType = adapterType,
            IsEnabled = true,
            Status = IntegrationStatus.Active,
            ConfigurationJson = """{"server":"dc01.corp.local","port":636,"username":"svc@corp.local"}"""
        };
        _context.Integrations.Add(integration);
        await _context.SaveChangesAsync();
        return integration;
    }

    // ---- resting status ------------------------------------------------------

    [Fact]
    public void LastOutcome_FollowsTheMostRecentFinishedRun()
    {
        var earlier = DateTime.UtcNow.AddHours(-1);
        var later = DateTime.UtcNow;

        // Nothing failed since the last success or reset.
        Assert.Equal(IntegrationStatus.Active,
            IntegrationStatusRules.LastOutcome("", later, false, DiscoveryJobStatus.Failed, later));
        // A partial job is Degraded, not Error.
        Assert.Equal(IntegrationStatus.Degraded,
            IntegrationStatusRules.LastOutcome("timeout on 2 hosts", null, null, DiscoveryJobStatus.PartialSuccess, later));
        // A failed test after the partial job wins, and vice versa.
        Assert.Equal(IntegrationStatus.Error,
            IntegrationStatusRules.LastOutcome("LDAP_BIND", later, false, DiscoveryJobStatus.PartialSuccess, earlier));
        Assert.Equal(IntegrationStatus.Degraded,
            IntegrationStatusRules.LastOutcome("partial", earlier, false, DiscoveryJobStatus.PartialSuccess, later));
        // An error without any recorded run (an interrupted server) stays an Error.
        Assert.Equal(IntegrationStatus.Error,
            IntegrationStatusRules.LastOutcome("DISCOVERY_INTERRUPTED", null, null, null, null));

        Assert.Null(IntegrationStatusRules.OutcomeOf(DiscoveryJobStatus.Cancelled));
        Assert.Null(IntegrationStatusRules.OutcomeOf(DiscoveryJobStatus.Running));
    }

    [Fact]
    public async Task Toggling_KeepsADegradedIntegrationDegraded()
    {
        var integration = await AddIntegrationAsync();
        integration.Status = IntegrationStatus.Degraded;
        integration.LastError = "2 hosts did not answer";
        _context.DiscoveryJobs.AddRange(
            new DiscoveryJob
            {
                IntegrationId = integration.Id, JobType = "Full", Status = DiscoveryJobStatus.PartialSuccess,
                CompletedAt = DateTime.UtcNow.AddMinutes(-5)
            },
            // A later cancelled run proves nothing.
            new DiscoveryJob
            {
                IntegrationId = integration.Id, JobType = "Full", Status = DiscoveryJobStatus.Cancelled,
                CompletedAt = DateTime.UtcNow
            });
        await _context.SaveChangesAsync();
        var service = CreateService();

        Assert.Equal("Inactive", (await service.UpdateAsync(integration.Id, new UpdateIntegrationRequest { IsEnabled = false }, "admin"))!.Status);
        Assert.Equal("Degraded", (await service.UpdateAsync(integration.Id, new UpdateIntegrationRequest { IsEnabled = true }, "admin"))!.Status);
        Assert.Equal(IntegrationStatus.Degraded, await IntegrationHealth.RestingStatusAsync(_context, integration, CancellationToken.None));

        // A failed job after the partial one is an Error again.
        _context.DiscoveryJobs.Add(new DiscoveryJob
        {
            IntegrationId = integration.Id, JobType = "Full", Status = DiscoveryJobStatus.Failed,
            CompletedAt = DateTime.UtcNow.AddMinutes(1)
        });
        await _context.SaveChangesAsync();
        await service.UpdateAsync(integration.Id, new UpdateIntegrationRequest { IsEnabled = false }, "admin");
        Assert.Equal("Error", (await service.UpdateAsync(integration.Id, new UpdateIntegrationRequest { IsEnabled = true }, "admin"))!.Status);
    }

    [Fact]
    public async Task AbandonedCollectorJobs_AreFailed_FreshOnesAndServerJobsAreKept()
    {
        var integration = await AddIntegrationAsync();
        var old = DateTime.UtcNow - IntegrationHealth.AbandonedCollectorJobAge - TimeSpan.FromMinutes(1);
        var abandoned = new DiscoveryJob { IntegrationId = integration.Id, JobType = "Collector", Status = DiscoveryJobStatus.Running, StartedAt = old };
        var stillSending = new DiscoveryJob
        {
            IntegrationId = integration.Id, JobType = "Collector", Status = DiscoveryJobStatus.Running,
            StartedAt = old, UpdatedAt = DateTime.UtcNow
        };
        var serverRun = new DiscoveryJob { IntegrationId = integration.Id, JobType = "Full", Status = DiscoveryJobStatus.Running, StartedAt = old };
        _context.DiscoveryJobs.AddRange(abandoned, stillSending, serverRun);
        await _context.SaveChangesAsync();

        Assert.Equal(1, await IntegrationHealth.FailAbandonedCollectorJobsAsync(_context, integration.Id, CancellationToken.None));
        await _context.SaveChangesAsync();

        Assert.Equal(DiscoveryJobStatus.Failed, abandoned.Status);
        Assert.NotNull(abandoned.CompletedAt);
        Assert.Equal(DiscoveryJobStatus.Running, stillSending.Status);
        Assert.Equal(DiscoveryJobStatus.Running, serverRun.Status);
    }

    // ---- delete cleanup -------------------------------------------------------

    [Fact]
    public async Task Delete_DismissesPendingReviews_AndRecomputesSharedNodeStatus()
    {
        var deleted = await AddIntegrationAsync();
        var other = await AddIntegrationAsync();
        var shared = new InfrastructureNode { Name = "sql-01", NodeType = NodeType.Computer, Status = "warning" };
        _context.InfrastructureNodes.Add(shared);
        _context.NodeSourceLinks.AddRange(
            new NodeSourceLink
            {
                IntegrationId = deleted.Id, NodeId = shared.Id, ExternalId = "vm-1",
                PropertiesJson = NodeStatusRules.LinkProperties(new Dictionary<string, string>(), "warning")
            },
            new NodeSourceLink
            {
                IntegrationId = other.Id, NodeId = shared.Id, ExternalId = "ad-1",
                PropertiesJson = NodeStatusRules.LinkProperties(new Dictionary<string, string>(), "healthy")
            });
        var pending = new ReconciliationReview { IntegrationId = deleted.Id, Status = ReconciliationReviewStatus.Pending };
        var merged = new ReconciliationReview { IntegrationId = deleted.Id, Status = ReconciliationReviewStatus.Merged, ResolutionNotes = "kept" };
        var otherPending = new ReconciliationReview { IntegrationId = other.Id, Status = ReconciliationReviewStatus.Pending };
        _context.ReconciliationReviews.AddRange(pending, merged, otherPending);
        await _context.SaveChangesAsync();

        Assert.True(await CreateService().DeleteAsync(deleted.Id, "admin"));

        Assert.Equal(ReconciliationReviewStatus.Dismissed, pending.Status);
        Assert.Equal("Integration deleted", pending.ResolutionNotes);
        Assert.Equal("admin", pending.ReviewedBy);
        Assert.NotNull(pending.ReviewedAt);
        Assert.Equal(ReconciliationReviewStatus.Merged, merged.Status);
        Assert.Equal("kept", merged.ResolutionNotes);
        Assert.Equal(ReconciliationReviewStatus.Pending, otherPending.Status);

        // Only the remaining source's status counts now.
        Assert.False(shared.IsStale);
        Assert.Equal("healthy", shared.Status);
        Assert.NotNull(shared.StatusChangedAt);

        var audit = await _context.AuditLogs.SingleAsync(a => a.Action == "IntegrationDeleted");
        Assert.Equal(1, JsonDocument.Parse(audit.DetailsJson).RootElement.GetProperty("reviewsDismissed").GetInt32());
    }

    // ---- multi-source node status ---------------------------------------------

    [Fact]
    public void NodeStatusRules_TakeTheWorstReportedStatus()
    {
        Assert.Equal("warning", NodeStatusRules.Effective(new[] { "healthy", "warning", null }));
        Assert.Equal("critical", NodeStatusRules.Effective(new[] { "PoweredOff", "warning" }));
        // "unknown" carries no information.
        Assert.Equal("healthy", NodeStatusRules.Effective(new[] { "unknown", "healthy" }));
        Assert.Equal("unknown", NodeStatusRules.Effective(new[] { "unknown" }));
        Assert.Null(NodeStatusRules.Effective(new string?[] { null, "" }));

        var json = NodeStatusRules.LinkProperties(new Dictionary<string, string> { ["Os"] = "Windows" }, "Online");
        Assert.Equal("healthy", NodeStatusRules.Reported(json));
        Assert.Equal("Windows", JsonDocument.Parse(json).RootElement.GetProperty("Os").GetString());
        Assert.Null(NodeStatusRules.Reported("{}"));
        Assert.Null(NodeStatusRules.Reported("not json"));
    }

    private static DiscoveryResult Report(Guid integrationId, string source, string externalId, string status, bool includeNode = true) => new()
    {
        Success = true,
        IntegrationId = integrationId,
        Nodes = includeNode
            ? new List<DiscoveredNodeContract>
            {
                new()
                {
                    ExternalId = externalId,
                    NodeType = source == "VMware" ? "VirtualMachine" : "Computer",
                    Name = "sql-dwh-01",
                    FQDN = "sql-dwh-01.corp.local",
                    BiosUuid = "4211aa00-0000-0000-0000-000000000001",
                    SourceSystem = source,
                    Status = status
                }
            }
            : new List<DiscoveredNodeContract>
            {
                new() { ExternalId = "other-" + externalId, NodeType = "VirtualMachine", Name = "other-vm", SourceSystem = source }
            }
    };

    [Fact]
    public async Task MultiSourceNode_ShowsTheWorstStatus_AndOnlyStampsRealChanges()
    {
        var vcenter = await AddIntegrationAsync("VMware");
        var ad = await AddIntegrationAsync();
        var ingestion = CreateIngestion();

        await ingestion.IngestAsync(vcenter.Id, Guid.NewGuid(), Report(vcenter.Id, "VMware", "vm-42", "warning"));
        var node = await _context.InfrastructureNodes.SingleAsync();
        Assert.Equal("warning", node.Status);
        Assert.Null(node.StatusChangedAt);

        // AD says healthy, vCenter warning: the node stays warning, on every
        // run and whichever source ran last, and no change is recorded.
        for (var run = 0; run < 2; run++)
        {
            var adRun = await ingestion.IngestAsync(ad.Id, Guid.NewGuid(), Report(ad.Id, "ActiveDirectory", "ad-42", "healthy"));
            await ingestion.IngestAsync(vcenter.Id, Guid.NewGuid(), Report(vcenter.Id, "VMware", "vm-42", "warning"));
            Assert.Equal(0, adRun.NodesCreated);
        }
        node = await _context.InfrastructureNodes.SingleAsync();
        Assert.Equal("warning", node.Status);
        Assert.Null(node.StatusChangedAt);
        Assert.Equal(2, await _context.NodeSourceLinks.CountAsync(l => l.NodeId == node.Id));

        // A real change of the worst status is a transition.
        var critical = await ingestion.IngestAsync(vcenter.Id, Guid.NewGuid(), Report(vcenter.Id, "VMware", "vm-42", "critical"));
        Assert.Equal(1, critical.NodesUpdated);
        Assert.Equal("critical", node.Status);
        var changedAt = Assert.IsType<DateTime>(node.StatusChangedAt);

        // vCenter's complete snapshot no longer contains the VM: its status
        // stops counting and the node shows what AD still reports.
        await Task.Delay(5);
        await ingestion.IngestAsync(vcenter.Id, Guid.NewGuid(), Report(vcenter.Id, "VMware", "vm-42", "critical", includeNode: false));
        node = await _context.InfrastructureNodes.SingleAsync(n => n.Name == "sql-dwh-01");
        Assert.False(node.IsStale);
        Assert.Equal("healthy", node.Status);
        Assert.True(node.StatusChangedAt > changedAt);
    }

    // ---- impact graph -----------------------------------------------------------

    [Fact]
    public async Task ImpactGraph_FollowsLiveDependencies_AndStopsAtContainment()
    {
        var dc = new InfrastructureNode { Name = "DC-01", NodeType = NodeType.Datacenter };
        var cluster = new InfrastructureNode { Name = "CL-PROD", NodeType = NodeType.Cluster };
        var host = new InfrastructureNode { Name = "esx01", NodeType = NodeType.Hypervisor };
        var oldHost = new InfrastructureNode { Name = "esx00", NodeType = NodeType.Hypervisor };
        var vm = new InfrastructureNode { Name = "app-01", NodeType = NodeType.VirtualMachine };
        var app = new InfrastructureNode { Name = "Payments", NodeType = NodeType.Application };
        var siblings = Enumerable.Range(0, 20)
            .Select(i => new InfrastructureNode { Name = $"web-{i:00}", NodeType = NodeType.VirtualMachine })
            .ToList();
        _context.InfrastructureNodes.AddRange(new[] { dc, cluster, host, oldHost, vm, app }.Concat(siblings));

        InfrastructureRelationship Rel(InfrastructureNode source, InfrastructureNode target, RelationshipType type, bool stale = false) =>
            new() { SourceNodeId = source.Id, TargetNodeId = target.Id, RelationshipType = type, IsStale = stale };

        _context.InfrastructureRelationships.AddRange(
            Rel(vm, host, RelationshipType.HostedOn),
            Rel(vm, oldHost, RelationshipType.HostedOn, stale: true),
            Rel(app, vm, RelationshipType.DependsOn),
            Rel(cluster, host, RelationshipType.Contains),
            Rel(dc, cluster, RelationshipType.Contains),
            Rel(dc, host, RelationshipType.Contains),
            Rel(dc, vm, RelationshipType.Contains));
        _context.InfrastructureRelationships.AddRange(siblings.Select(s => Rel(dc, s, RelationshipType.Contains)));
        await _context.SaveChangesAsync();

        var repository = new GraphRepository(_context);

        var impact = await repository.GetImpactGraphAsync(vm.Id, depth: 3);
        var names = impact.Nodes.Select(n => n.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "app-01", "esx01", "Payments", "CL-PROD" }, names);
        // The datacenter is read as a container of the target, never expanded.
        Assert.DoesNotContain(names, n => n.StartsWith("web-", StringComparison.Ordinal));
        Assert.DoesNotContain("esx00", names);
        Assert.DoesNotContain(impact.Relationships, r => r.IsStale);
        Assert.Contains(impact.Relationships, r => r.RelationshipType == "Contains" && r.SourceNodeName == "CL-PROD");
        Assert.False(impact.IsTruncated);

        // The map's topology graph is unchanged and still walks containment.
        var topology = await repository.GetTopologyGraphAsync(vm.Id, depth: 2);
        Assert.Contains(topology.Nodes, n => n.Name.StartsWith("web-", StringComparison.Ordinal));

        // End to end: the cluster comes from the live host.
        var result = await new BerMap.Simulation.SimulationEngine(repository).RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = vm.Id,
            ScenarioType = "workload_outage"
        });
        Assert.Equal("CL-PROD", result.ClusterHeadroom.ClusterName);
        Assert.Equal("Payments", Assert.Single(result.ConfirmedImpacts).Name);
        Assert.False(result.GraphTruncated);
    }

    private sealed class NoopJobRunner : IDiscoveryJobRunner
    {
        public Task RunJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public bool RequestCancellation(Guid jobId) => false;
    }
}
