using System;
using System.Linq;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using BerMap.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BerMap.Tests.Unit;

public class SnapshotServiceTests
{
    private static BerMapDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<BerMapDbContext>()
            .UseInMemoryDatabase($"snapshot-{Guid.NewGuid():N}")
            .Options;

        return new BerMapDbContext(options);
    }

    private static async Task<(InfrastructureNode first, InfrastructureNode second)> SeedGraphAsync(BerMapDbContext context)
    {
        var first = new InfrastructureNode
        {
            Name = "srv-app01",
            FQDN = "srv-app01.corp.local",
            NodeType = NodeType.VirtualMachine,
            IPAddress = "10.10.1.10",
            MacAddress = "00:50:56:AA:BB:CC",
            BiosUuid = "42189b21-4a11-82e1-90a1-8912410a1122",
            Status = "healthy",
            DiscoverySource = "VMware"
        };

        var second = new InfrastructureNode
        {
            Name = "esxi01",
            FQDN = "esxi01.corp.local",
            NodeType = NodeType.Hypervisor,
            IPAddress = "10.10.1.11",
            Status = "healthy",
            DiscoverySource = "VMware"
        };

        context.InfrastructureNodes.AddRange(first, second);
        context.InfrastructureRelationships.Add(new InfrastructureRelationship
        {
            SourceNodeId = second.Id,
            TargetNodeId = first.Id,
            RelationshipType = RelationshipType.HostedOn,
            ConfidenceLevel = ConfidenceLevel.Verified,
            Evidence = "vCenter host reference",
            SourceSystem = "VMware"
        });

        await context.SaveChangesAsync();
        return (first, second);
    }

    [Fact]
    public async Task CreateSnapshot_CapturesImmutableGraphWithStableHash()
    {
        await using var context = CreateContext();
        await SeedGraphAsync(context);
        var service = new SnapshotService(context);

        var snapshot = await service.CreateSnapshotAsync(
            new CreateSnapshotRequest { Name = "Baseline", Description = "Initial capture" },
            "tester");

        Assert.Equal(2, snapshot.NodeCount);
        Assert.Equal(1, snapshot.RelationshipCount);
        Assert.True(snapshot.IsImmutable);
        Assert.Equal(1, snapshot.SnapshotVersion);
        Assert.Equal(64, snapshot.Hash.Length);
        Assert.Empty(snapshot.Warnings);

        var graph = await service.GetSnapshotGraphAsync(snapshot.Id);
        Assert.NotNull(graph);
        Assert.Equal(2, graph!.Nodes.Count);
        Assert.Single(graph.Relationships);

        var verification = await service.VerifySnapshotAsync(snapshot.Id);
        Assert.NotNull(verification);
        Assert.True(verification!.IsValid);
        Assert.Equal(snapshot.Hash, verification.ComputedHash);
    }

    [Fact]
    public async Task CompareSnapshots_ReportsAddedChangedNodesAndRelationships()
    {
        await using var context = CreateContext();
        var (first, second) = await SeedGraphAsync(context);
        var service = new SnapshotService(context);

        var baseline = await service.CreateSnapshotAsync(
            new CreateSnapshotRequest { Name = "Before" }, "tester");

        first.Status = "critical";
        var third = new InfrastructureNode
        {
            Name = "db01",
            FQDN = "db01.corp.local",
            NodeType = NodeType.VirtualMachine,
            Status = "healthy",
            DiscoverySource = "ActiveDirectory"
        };
        context.InfrastructureNodes.Add(third);
        context.InfrastructureRelationships.Add(new InfrastructureRelationship
        {
            SourceNodeId = second.Id,
            TargetNodeId = third.Id,
            RelationshipType = RelationshipType.HostedOn,
            SourceSystem = "VMware"
        });
        await context.SaveChangesAsync();

        var current = await service.CreateSnapshotAsync(
            new CreateSnapshotRequest { Name = "After" }, "tester");

        Assert.Equal(2, current.SnapshotVersion);

        var diff = await service.CompareSnapshotsAsync(baseline.Id, current.Id);

        Assert.Equal(1, diff.AddedNodesCount);
        Assert.Equal(0, diff.RemovedNodesCount);
        Assert.Equal(1, diff.ChangedNodesCount);
        Assert.Equal(1, diff.AddedRelationshipsCount);
        Assert.Contains(diff.ChangedNodes, n => n.NodeId == first.Id &&
            n.Changes.Any(c => c.Field == "Status" && c.To == "critical"));
        Assert.Contains(diff.AddedNodes, n => n.Name == "db01");
    }

    [Fact]
    public async Task CompareSnapshots_IgnoresReconciliationBookkeeping_ButNotRealPropertyChanges()
    {
        await using var context = CreateContext();
        var (first, second) = await SeedGraphAsync(context);
        first.PropertiesJson = """{"PowerState":"POWERED_ON","_lastReconciliationAt":"2026-11-01T10:00:00.0000000Z","_lastReconciliationEvidence":"BIOS UUID match","_lastReconciliationConfidence":"Verified","_sourceSystems":["VMware"]}""";
        second.PropertiesJson = """{"_lastReconciliationAt":"2026-11-01T10:00:00.0000000Z"}""";
        await context.SaveChangesAsync();
        var service = new SnapshotService(context);

        var baseline = await service.CreateSnapshotAsync(new CreateSnapshotRequest { Name = "Before" }, "tester");

        // An unchanged re-discovery only rewrites the bookkeeping.
        first.PropertiesJson = """{"_sourceSystems":["VMware","ActiveDirectory"],"_lastReconciliationConfidence":"High","_lastReconciliationEvidence":"FQDN match","_lastReconciliationAt":"2026-11-02T10:00:00.0000000Z","PowerState":"POWERED_ON"}""";
        second.PropertiesJson = """{"_lastReconciliationAt":"2026-11-02T10:00:00.0000000Z","CpuUsagePercent":"91"}""";
        await context.SaveChangesAsync();

        var current = await service.CreateSnapshotAsync(new CreateSnapshotRequest { Name = "After" }, "tester");
        var diff = await service.CompareSnapshotsAsync(baseline.Id, current.Id);

        var changed = Assert.Single(diff.ChangedNodes);
        Assert.Equal(second.Id, changed.NodeId);
        var properties = Assert.Single(changed.Changes, c => c.Field == "Properties");
        Assert.DoesNotContain("_lastReconciliation", properties.From + properties.To);
        Assert.Contains("CpuUsagePercent", properties.To);
        Assert.Equal(1, diff.ChangedNodesCount);
    }

    [Fact]
    public async Task SnapshotRows_RejectUpdatesAndDeletes()
    {
        await using var context = CreateContext();
        await SeedGraphAsync(context);
        var service = new SnapshotService(context);

        var summary = await service.CreateSnapshotAsync(
            new CreateSnapshotRequest { Name = "Immutable" }, "tester");

        var snapshot = await context.Snapshots.FirstAsync(s => s.Id == summary.Id);
        snapshot.Name = "Tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());

        context.ChangeTracker.Clear();

        var reloaded = await context.Snapshots.FirstAsync(s => s.Id == summary.Id);
        context.Snapshots.Remove(reloaded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());

        context.ChangeTracker.Clear();

        var nodeRow = await context.SnapshotNodes.FirstAsync(n => n.SnapshotId == summary.Id);
        nodeRow.Status = "critical";
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task CreateSnapshot_EmptyGraph_AddsWarning()
    {
        await using var context = CreateContext();
        var service = new SnapshotService(context);

        var snapshot = await service.CreateSnapshotAsync(
            new CreateSnapshotRequest { Name = "Empty" }, "tester");

        Assert.Equal(0, snapshot.NodeCount);
        Assert.Single(snapshot.Warnings);
    }

    [Fact]
    public async Task CompareSnapshots_UnknownSnapshot_Throws()
    {
        await using var context = CreateContext();
        var service = new SnapshotService(context);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.CompareSnapshotsAsync(Guid.NewGuid(), Guid.NewGuid()));
    }
}
