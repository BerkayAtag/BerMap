using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using BerMap.Infrastructure.Repositories;
using BerMap.Infrastructure.Services;
using BerMap.Simulation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BerMap.Tests.Unit;

/// <summary>
/// Simulation on a snapshot basis: the impact graph is read from the frozen
/// SnapshotNodes/SnapshotRelationships with the live walk's rules, and the
/// engine reports which snapshot it ran on.
/// </summary>
public class SimulationSnapshotTests
{
    private static BerMapDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<BerMapDbContext>()
            .UseInMemoryDatabase($"simulation-snapshot-{Guid.NewGuid():N}")
            .Options);

    private static InfrastructureNode Node(string name, NodeType type, string properties = "{}") => new()
    {
        Name = name,
        FQDN = $"{name}.corp.local",
        NodeType = type,
        Status = "healthy",
        DiscoverySource = "VMware",
        PropertiesJson = properties,
    };

    private static InfrastructureRelationship Rel(InfrastructureNode source, InfrastructureNode target, RelationshipType type, bool stale = false) => new()
    {
        SourceNodeId = source.Id,
        TargetNodeId = target.Id,
        RelationshipType = type,
        SourceSystem = "VMware",
        IsStale = stale,
    };

    /// <summary>
    /// DC-01 Contains CL-PROD, which Contains esx-a and esx-b; vm-01 runs on
    /// esx-a and app-01 depends on it. vm-old has only a stale HostedOn edge
    /// to esx-a (moved away before the snapshot), vm-gone is itself stale.
    /// </summary>
    private sealed class Topology
    {
        public InfrastructureNode Datacenter { get; } = Node("DC-01", NodeType.Datacenter);
        public InfrastructureNode Cluster { get; } = Node("CL-PROD", NodeType.Cluster,
            """{"HaEnabled":false,"HostCount":2,"_sourceSystems":["VMware","ActiveDirectory"],"Note":null,"Owner":"infra"}""");
        public InfrastructureNode HostA { get; } = Node("esx-a", NodeType.Hypervisor);
        public InfrastructureNode HostB { get; } = Node("esx-b", NodeType.Hypervisor);
        public InfrastructureNode Vm { get; } = Node("vm-01", NodeType.VirtualMachine);
        public InfrastructureNode OldVm { get; } = Node("vm-old", NodeType.VirtualMachine);
        public InfrastructureNode GoneVm { get; } = Node("vm-gone", NodeType.VirtualMachine);
        public InfrastructureNode App { get; } = Node("app-01", NodeType.Application);
        public InfrastructureRelationship VmOnHostA { get; }

        public Topology()
        {
            GoneVm.IsStale = true;
            VmOnHostA = Rel(Vm, HostA, RelationshipType.HostedOn);
        }

        public async Task SeedAsync(BerMapDbContext context)
        {
            context.InfrastructureNodes.AddRange(Datacenter, Cluster, HostA, HostB, Vm, OldVm, GoneVm, App);
            context.InfrastructureRelationships.AddRange(
                Rel(Datacenter, Cluster, RelationshipType.Contains),
                Rel(Cluster, HostA, RelationshipType.Contains),
                Rel(Cluster, HostB, RelationshipType.Contains),
                VmOnHostA,
                Rel(OldVm, HostA, RelationshipType.HostedOn, stale: true),
                Rel(GoneVm, HostA, RelationshipType.HostedOn),
                Rel(App, Vm, RelationshipType.DependsOn));
            await context.SaveChangesAsync();
        }
    }

    private static async Task<(BerMapDbContext Context, Topology Topology, SnapshotSummaryDto Snapshot)> SeedWithSnapshotAsync()
    {
        var context = CreateContext();
        var topology = new Topology();
        await topology.SeedAsync(context);
        var snapshot = await new SnapshotService(context).CreateSnapshotAsync(
            new CreateSnapshotRequest { Name = "before-maintenance" }, "tester");
        return (context, topology, snapshot);
    }

    [Fact]
    public async Task SnapshotImpactGraph_WalksFrozenGraph_WithLiveRules()
    {
        var (context, t, snapshot) = await SeedWithSnapshotAsync();
        await using var _ = context;
        var repository = new GraphRepository(context);

        var result = await repository.GetSnapshotImpactGraphAsync(snapshot.Id, t.HostA.Id, depth: 3);

        Assert.NotNull(result);
        Assert.Equal(snapshot.Id, result.SnapshotId);
        Assert.Equal("before-maintenance", result.SnapshotName);
        Assert.Equal(snapshot.GraphCapturedAt, result.SnapshotCreatedAt);

        var graph = result.Graph;
        Assert.False(graph.IsTruncated);
        Assert.Equal(t.HostA.Id, graph.Nodes[0].Id);

        // The stale HostedOn edge is not walked, so vm-old never joins; the
        // datacenter is not reached because containment is not expanded.
        var names = graph.Nodes.Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "CL-PROD", "app-01", "esx-a", "vm-01", "vm-gone" }, names);
        Assert.DoesNotContain(graph.Relationships, r => r.IsStale);

        // Original ids, so the live target id finds its frozen copy.
        Assert.Contains(graph.Relationships, r => r.Id == t.VmOnHostA.Id &&
                                                  r.SourceNodeName == "vm-01" &&
                                                  r.TargetNodeName == "esx-a" &&
                                                  r.RelationshipType == "HostedOn");

        // A stale node reached over a live edge is returned marked stale; the
        // engine's LiveGraph filter drops it, exactly as on the live graph.
        Assert.True(graph.Nodes.Single(n => n.Name == "vm-gone").IsStale);
    }

    [Fact]
    public async Task SnapshotImpactGraph_MatchesTheLiveImpactGraph_WhenNothingChanged()
    {
        var (context, t, snapshot) = await SeedWithSnapshotAsync();
        await using var _ = context;
        var repository = new GraphRepository(context);

        var live = await repository.GetImpactGraphAsync(t.Vm.Id, depth: 3);
        var frozen = (await repository.GetSnapshotImpactGraphAsync(snapshot.Id, t.Vm.Id, depth: 3))!.Graph;

        Assert.Equal(live.Nodes.Select(n => n.Id).OrderBy(id => id), frozen.Nodes.Select(n => n.Id).OrderBy(id => id));
        Assert.Equal(live.Relationships.Select(r => r.Id).OrderBy(id => id), frozen.Relationships.Select(r => r.Id).OrderBy(id => id));
        Assert.Equal(live.Nodes[0].Id, frozen.Nodes[0].Id);
    }

    [Fact]
    public async Task SnapshotImpactGraph_ParsesPropertiesLikeTheLiveGraph()
    {
        var (context, t, snapshot) = await SeedWithSnapshotAsync();
        await using var _ = context;

        var graph = (await new GraphRepository(context).GetSnapshotImpactGraphAsync(snapshot.Id, t.HostA.Id, depth: 3))!.Graph;
        var cluster = graph.Nodes.Single(n => n.Id == t.Cluster.Id);

        // Scalars keep their value, arrays keep raw JSON, null becomes empty.
        Assert.Equal("false", cluster.Properties["HaEnabled"]);
        Assert.Equal("2", cluster.Properties["HostCount"]);
        Assert.Equal("infra", cluster.Properties["owner"]);
        Assert.Equal("""["VMware","ActiveDirectory"]""", cluster.Properties["_sourceSystems"]);
        Assert.Equal(string.Empty, cluster.Properties["Note"]);
        Assert.Equal("Cluster", cluster.NodeType);
        Assert.Equal(new[] { "VMware" }, cluster.SourceSystems);
    }

    [Fact]
    public async Task SnapshotImpactGraph_KeepsANodeWithCorruptProperties()
    {
        await using var context = CreateContext();
        var host = Node("esx-corrupt", NodeType.Hypervisor, "{not json");
        context.InfrastructureNodes.Add(host);
        await context.SaveChangesAsync();
        var snapshot = await new SnapshotService(context).CreateSnapshotAsync(new CreateSnapshotRequest { Name = "s" }, "tester");

        var graph = (await new GraphRepository(context).GetSnapshotImpactGraphAsync(snapshot.Id, host.Id, depth: 3))!.Graph;

        var node = Assert.Single(graph.Nodes);
        Assert.Equal("esx-corrupt", node.Name);
        Assert.Empty(node.Properties);
    }

    [Fact]
    public async Task SnapshotImpactGraph_ReturnsEmptyGraph_ForTargetNotInSnapshot_AndNull_ForUnknownSnapshot()
    {
        var (context, _, snapshot) = await SeedWithSnapshotAsync();
        await using var __ = context;
        var repository = new GraphRepository(context);

        // A node discovered after the capture.
        var newHost = Node("esx-new", NodeType.Hypervisor);
        context.InfrastructureNodes.Add(newHost);
        await context.SaveChangesAsync();

        var missingTarget = await repository.GetSnapshotImpactGraphAsync(snapshot.Id, newHost.Id, depth: 3);
        Assert.NotNull(missingTarget);
        Assert.Equal("before-maintenance", missingTarget.SnapshotName);
        Assert.Empty(missingTarget.Graph.Nodes);
        Assert.Empty(missingTarget.Graph.Relationships);

        Assert.Null(await repository.GetSnapshotImpactGraphAsync(Guid.NewGuid(), newHost.Id, depth: 3));
    }

    [Fact]
    public async Task SnapshotImpactGraph_IsIsolatedFromOtherSnapshots()
    {
        var (context, t, first) = await SeedWithSnapshotAsync();
        await using var _ = context;

        // Move vm-01 to esx-b and capture a second snapshot.
        var live = await context.InfrastructureRelationships.SingleAsync(r => r.Id == t.VmOnHostA.Id);
        live.IsStale = true;
        context.InfrastructureRelationships.Add(Rel(t.Vm, t.HostB, RelationshipType.HostedOn));
        await context.SaveChangesAsync();
        var second = await new SnapshotService(context).CreateSnapshotAsync(new CreateSnapshotRequest { Name = "after" }, "tester");

        var repository = new GraphRepository(context);
        var before = (await repository.GetSnapshotImpactGraphAsync(first.Id, t.HostA.Id, depth: 3))!.Graph;
        var after = (await repository.GetSnapshotImpactGraphAsync(second.Id, t.HostA.Id, depth: 3))!.Graph;

        Assert.Contains(before.Nodes, n => n.Id == t.Vm.Id);
        Assert.DoesNotContain(after.Nodes, n => n.Id == t.Vm.Id);
        Assert.Equal(before.Relationships.Count, before.Relationships.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public async Task Engine_OnSnapshotBasis_SeesTheTopologyAsCaptured_AndLiveSeesTheChange()
    {
        var (context, t, snapshot) = await SeedWithSnapshotAsync();
        await using var _ = context;

        // vMotion after the snapshot: vm-01 now runs on esx-b.
        var live = await context.InfrastructureRelationships.SingleAsync(r => r.Id == t.VmOnHostA.Id);
        live.IsStale = true;
        context.InfrastructureRelationships.Add(Rel(t.Vm, t.HostB, RelationshipType.HostedOn));
        await context.SaveChangesAsync();

        var engine = new SimulationEngine(new GraphRepository(context));
        var liveResult = await engine.RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = t.HostA.Id,
            ScenarioType = "host_failure",
        });
        var snapshotResult = await engine.RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = t.HostA.Id,
            ScenarioType = "host_failure",
            SnapshotId = snapshot.Id,
        });

        Assert.Empty(liveResult.ConfirmedImpacts);
        Assert.Null(liveResult.SnapshotId);
        Assert.Null(liveResult.SnapshotName);
        Assert.Null(liveResult.SnapshotCreatedAt);

        var hit = Assert.Single(snapshotResult.ConfirmedImpacts);
        Assert.Equal("vm-01", hit.Name);
        Assert.Contains(snapshotResult.ProbableImpacts, i => i.Name == "app-01");
        Assert.Equal(snapshot.Id, snapshotResult.SnapshotId);
        Assert.Equal("before-maintenance", snapshotResult.SnapshotName);
        Assert.Equal(snapshot.GraphCapturedAt, snapshotResult.SnapshotCreatedAt);
        // The frozen cluster properties drive the HA verdict: HA was off.
        Assert.Contains(snapshotResult.RecommendationItems, i => i.Code == "ha_disabled");
        Assert.True(snapshotResult.RiskScore > liveResult.RiskScore);
    }

    [Fact]
    public async Task Engine_RejectsAnUnknownSnapshot_WithSnapshotNotFound()
    {
        var (context, t, _) = await SeedWithSnapshotAsync();
        await using var __ = context;
        var engine = new SimulationEngine(new GraphRepository(context));

        var error = await Assert.ThrowsAsync<SimulationNotFoundException>(() => engine.RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = t.HostA.Id,
            ScenarioType = "host_failure",
            SnapshotId = Guid.NewGuid(),
        }));

        Assert.Equal(SimulationNotFoundException.SnapshotNotFoundCode, error.Code);
        Assert.IsAssignableFrom<KeyNotFoundException>(error);
    }

    [Fact]
    public async Task Engine_RejectsATargetTheSnapshotDidNotCapture_WithNotInSnapshot()
    {
        var (context, _, snapshot) = await SeedWithSnapshotAsync();
        await using var __ = context;
        var newHost = Node("esx-new", NodeType.Hypervisor);
        context.InfrastructureNodes.Add(newHost);
        await context.SaveChangesAsync();
        var engine = new SimulationEngine(new GraphRepository(context));

        var error = await Assert.ThrowsAsync<SimulationNotFoundException>(() => engine.RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = newHost.Id,
            ScenarioType = "host_failure",
            SnapshotId = snapshot.Id,
        }));

        Assert.Equal(SimulationNotFoundException.NotInSnapshotCode, error.Code);
        Assert.Contains("before-maintenance", error.Message);

        // The same target on the live graph is found.
        var live = await engine.RunSimulationAsync(new SimulationRunRequest { TargetNodeId = newHost.Id, ScenarioType = "host_failure" });
        Assert.Equal("esx-new", live.TargetNodeName);
    }

    [Fact]
    public async Task Engine_ChecksTheScenarioAgainstTheFrozenNodeType()
    {
        var (context, t, snapshot) = await SeedWithSnapshotAsync();
        await using var _ = context;
        var engine = new SimulationEngine(new GraphRepository(context));

        var error = await Assert.ThrowsAsync<IntegrationValidationException>(() => engine.RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = t.Vm.Id,
            ScenarioType = "host_failure",
            SnapshotId = snapshot.Id,
        }));
        Assert.Equal("invalid_simulation", error.Code);
    }

    [Fact]
    public async Task Engine_RejectsAnEmptySnapshotId()
    {
        var target = Guid.NewGuid();
        var engine = new SimulationEngine(new SnapshotFakeGraphService(new GraphQueryResultDto()));

        var error = await Assert.ThrowsAsync<IntegrationValidationException>(() => engine.RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = target,
            ScenarioType = "host_failure",
            SnapshotId = Guid.Empty,
        }));
        Assert.Equal("invalid_simulation", error.Code);
    }

    [Fact]
    public async Task Engine_RejectsASnapshotBasis_WhenTheGraphServiceCannotReadSnapshots()
    {
        var host = new InfrastructureNodeDto { Id = Guid.NewGuid(), Name = "esx", NodeType = "Hypervisor" };
        var engine = new SimulationEngine(new StaticGraphService(new GraphQueryResultDto { Nodes = new() { host } }));

        var error = await Assert.ThrowsAsync<IntegrationValidationException>(() => engine.RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = host.Id,
            ScenarioType = "host_failure",
            SnapshotId = Guid.NewGuid(),
        }));
        Assert.Equal("invalid_simulation", error.Code);
    }

    [Fact]
    public async Task Engine_UsesTheSnapshotGraph_NotTheLiveOne_AndFiltersStaleFrozenEdges()
    {
        var host = new InfrastructureNodeDto { Id = Guid.NewGuid(), Name = "esx", NodeType = "Hypervisor" };
        var vm = new InfrastructureNodeDto { Id = Guid.NewGuid(), Name = "vm-frozen", NodeType = "VirtualMachine" };
        var movedVm = new InfrastructureNodeDto { Id = Guid.NewGuid(), Name = "vm-moved", NodeType = "VirtualMachine" };
        var frozen = new GraphQueryResultDto
        {
            Nodes = new() { host, vm, movedVm },
            Relationships = new()
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = vm.Id, TargetNodeId = host.Id, RelationshipType = "HostedOn" },
                new() { Id = Guid.NewGuid(), SourceNodeId = movedVm.Id, TargetNodeId = host.Id, RelationshipType = "HostedOn", IsStale = true },
            },
        };
        var snapshotId = Guid.NewGuid();
        var capturedAt = new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);
        var service = new SnapshotFakeGraphService(new GraphQueryResultDto { Nodes = new() { host } })
        {
            Snapshot = new SnapshotImpactGraph
            {
                SnapshotId = snapshotId,
                SnapshotName = "Q3 baseline",
                SnapshotCreatedAt = capturedAt,
                Graph = frozen,
            },
        };

        var result = await new SimulationEngine(service).RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = host.Id,
            ScenarioType = "host_failure",
            SnapshotId = snapshotId,
        });

        Assert.Equal((snapshotId, host.Id, 3), service.SnapshotRequest);
        Assert.Null(service.LiveRequest);
        Assert.Equal("vm-frozen", Assert.Single(result.ConfirmedImpacts).Name);
        Assert.Equal(snapshotId, result.SnapshotId);
        Assert.Equal("Q3 baseline", result.SnapshotName);
        Assert.Equal(capturedAt, result.SnapshotCreatedAt);
    }

    /// <summary>Serves a fixed live graph and, optionally, one snapshot graph.</summary>
    private sealed class SnapshotFakeGraphService(GraphQueryResultDto live) : IGraphService, IImpactGraphSource
    {
        public SnapshotImpactGraph? Snapshot { get; init; }
        public (Guid, Guid, int)? SnapshotRequest { get; private set; }
        public Guid? LiveRequest { get; private set; }

        public Task<GraphQueryResultDto> GetImpactGraphAsync(Guid targetNodeId, int depth, CancellationToken cancellationToken = default)
        {
            LiveRequest = targetNodeId;
            return Task.FromResult(live);
        }

        public Task<SnapshotImpactGraph?> GetSnapshotImpactGraphAsync(Guid snapshotId, Guid targetNodeId, int depth, CancellationToken cancellationToken = default)
        {
            SnapshotRequest = (snapshotId, targetNodeId, depth);
            return Task.FromResult(Snapshot?.SnapshotId == snapshotId ? Snapshot : null);
        }

        public Task<GraphQueryResultDto> GetTopologyGraphAsync(Guid? centerNodeId = null, int depth = 2, CancellationToken cancellationToken = default)
            => Task.FromResult(live);

        public Task<List<InfrastructureNodeDto>> SearchNodesAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<InfrastructureNodeDto>());

        public Task<InfrastructureNodeDto?> GetNodeDetailsAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<InfrastructureNodeDto?>(null);
    }
}
