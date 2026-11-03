using System;
using System.Threading.Tasks;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using BerMap.Infrastructure.Repositories;
using BerMap.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BerMap.Tests.Unit;

public class DashboardAndGraphTests
{
    private static BerMapDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<BerMapDbContext>()
            .UseInMemoryDatabase($"dashboard-{Guid.NewGuid():N}")
            .Options;

        return new BerMapDbContext(options);
    }

    [Fact]
    public async Task EmptyDatabase_ReturnsEmptyGraphInsteadOfMockTopology()
    {
        await using var context = CreateContext();
        var repository = new GraphRepository(context);

        var graph = await repository.GetTopologyGraphAsync();
        var search = await repository.SearchNodesAsync("esxi");

        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Relationships);
        Assert.Empty(search);
    }

    [Fact]
    public async Task DashboardSummary_IsComputedFromPersistedData()
    {
        await using var context = CreateContext();

        var healthy = new InfrastructureNode
        {
            Name = "healthy-01",
            NodeType = NodeType.VirtualMachine,
            Status = "healthy",
            DiscoverySource = "VMware",
            FirstSeen = DateTime.UtcNow.AddMinutes(-5),
            LastSeen = DateTime.UtcNow.AddMinutes(-1)
        };

        var critical = new InfrastructureNode
        {
            Name = "critical-01",
            NodeType = NodeType.VirtualMachine,
            Status = "critical",
            DiscoverySource = "VMware",
            FirstSeen = DateTime.UtcNow.AddHours(-2),
            LastSeen = DateTime.UtcNow.AddMinutes(-3)
        };

        var stale = new InfrastructureNode
        {
            Name = "stale-01",
            NodeType = NodeType.Computer,
            Status = "warning",
            DiscoverySource = "ActiveDirectory",
            IsStale = true,
            StaleSince = DateTime.UtcNow.AddDays(-1),
            FirstSeen = DateTime.UtcNow.AddDays(-10),
            LastSeen = DateTime.UtcNow.AddDays(-2)
        };

        context.InfrastructureNodes.AddRange(healthy, critical, stale);
        context.InfrastructureRelationships.Add(new InfrastructureRelationship
        {
            SourceNodeId = critical.Id,
            TargetNodeId = healthy.Id,
            RelationshipType = RelationshipType.HostedOn,
            SourceSystem = "VMware"
        });

        context.Integrations.AddRange(
            new Integration { Name = "Active", AdapterType = "VMware", Status = IntegrationStatus.Active, IsEnabled = true },
            new Integration { Name = "Broken", AdapterType = "ActiveDirectory", Status = IntegrationStatus.Error, IsEnabled = true },
            new Integration { Name = "Disabled", AdapterType = "VMware", Status = IntegrationStatus.Inactive, IsEnabled = false });

        context.ReconciliationReviews.AddRange(
            new ReconciliationReview { Status = ReconciliationReviewStatus.Pending },
            new ReconciliationReview { Status = ReconciliationReviewStatus.Merged });

        await context.SaveChangesAsync();

        var service = new DashboardService(context);
        var summary = await service.GetSummaryAsync();

        Assert.Equal(3, summary.TotalEntities);
        Assert.Equal(1, summary.HealthyEntities);
        Assert.Equal(0, summary.WarningEntities);
        Assert.Equal(1, summary.CriticalEntities);
        Assert.Equal(1, summary.StaleEntities);
        Assert.Equal(0, summary.UnknownEntities);
        Assert.Equal(1, summary.ActiveIntegrations);
        Assert.Equal(3, summary.TotalIntegrations);
        Assert.Equal(1, summary.PendingReconciliationReviews);
        Assert.Equal(1, summary.CriticalDependenciesCount);
        Assert.Equal(2, summary.RecentDiscoveries);
        Assert.NotNull(summary.LastDiscoveryAt);
        Assert.NotEmpty(summary.RecentChanges);
    }

    [Fact]
    public async Task TopologyGraph_ReturnsPersistedNodesAndRelationships()
    {
        await using var context = CreateContext();

        var host = new InfrastructureNode
        {
            Name = "esxi01",
            FQDN = "esxi01.corp.local",
            NodeType = NodeType.Hypervisor,
            Status = "healthy",
            DiscoverySource = "VMware"
        };

        var vm = new InfrastructureNode
        {
            Name = "app01",
            FQDN = "app01.corp.local",
            NodeType = NodeType.VirtualMachine,
            Status = "healthy",
            DiscoverySource = "VMware",
            BiosUuid = "42189b21-4a11-82e1-90a1-8912410a1122"
        };

        context.InfrastructureNodes.AddRange(host, vm);
        context.InfrastructureRelationships.Add(new InfrastructureRelationship
        {
            SourceNodeId = host.Id,
            TargetNodeId = vm.Id,
            RelationshipType = RelationshipType.HostedOn,
            SourceSystem = "VMware"
        });
        await context.SaveChangesAsync();

        var repository = new GraphRepository(context);
        var graph = await repository.GetTopologyGraphAsync(host.Id, depth: 1);

        Assert.Equal(2, graph.Nodes.Count);
        Assert.Single(graph.Relationships);
        Assert.Equal("HostedOn", graph.Relationships[0].RelationshipType);
        Assert.Contains(graph.Nodes, n => n.BiosUuid == vm.BiosUuid);
    }
}
