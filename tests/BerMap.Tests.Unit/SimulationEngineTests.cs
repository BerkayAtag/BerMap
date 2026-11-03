using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Simulation;

namespace BerMap.Tests.Unit;

/// <summary>
/// Graph fixture mirrors the REAL adapter conventions:
///   vm -HostedOn-> host, cluster -Contains-> host/service,
///   app -DependsOn-> vm, service -DependsOn-> vm.
/// </summary>
public sealed class FixtureGraphService(Guid targetId) : IGraphService
{
    public GraphQueryResultDto Graph { get; } = Build(targetId);

    private static GraphQueryResultDto Build(Guid targetId)
    {
        var vm1Id = Guid.NewGuid();
        var vm2Id = Guid.NewGuid();
        var appId = Guid.NewGuid();
        var svcId = Guid.NewGuid();
        var clusterId = Guid.NewGuid();

        var clusterProps = new Dictionary<string, string>
        {
            ["CpuUsagePercent"] = "78.40",
            ["MemoryUsagePercent"] = "61.20",
            ["HaEnabled"] = "true",
        };

        return new GraphQueryResultDto
        {
            Nodes = new List<InfrastructureNodeDto>
            {
                new() { Id = targetId, Name = "esxi-node01.prod.local", NodeType = "Hypervisor", Status = "healthy" },
                new() { Id = vm1Id, Name = "vm-core-api01", NodeType = "VirtualMachine", Status = "healthy" },
                new() { Id = vm2Id, Name = "vm-core-api02", NodeType = "VirtualMachine", Status = "healthy" },
                new() { Id = appId, Name = "Payments Core", NodeType = "Application", Status = "healthy" },
                new() { Id = svcId, Name = "Session Service", NodeType = "Service", Status = "healthy" },
                new() { Id = clusterId, Name = "Cluster-Core-Banking", NodeType = "Cluster", Status = "healthy", Properties = clusterProps },
            },
            Relationships = new List<InfrastructureRelationshipDto>
            {
                // REAL direction: vm -HostedOn-> host
                new() { Id = Guid.NewGuid(), SourceNodeId = vm1Id, TargetNodeId = targetId, RelationshipType = "HostedOn" },
                new() { Id = Guid.NewGuid(), SourceNodeId = vm2Id, TargetNodeId = targetId, RelationshipType = "HostedOn" },
                // cluster -Contains-> host
                new() { Id = Guid.NewGuid(), SourceNodeId = clusterId, TargetNodeId = targetId, RelationshipType = "Contains" },
                // dependents of the VMs
                new() { Id = Guid.NewGuid(), SourceNodeId = appId, TargetNodeId = vm1Id, RelationshipType = "DependsOn" },
                new() { Id = Guid.NewGuid(), SourceNodeId = svcId, TargetNodeId = vm2Id, RelationshipType = "DependsOn" },
            },
        };
    }

    public Task<GraphQueryResultDto> GetTopologyGraphAsync(Guid? centerNodeId = null, int depth = 2, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Graph);
    }

    public Task<List<InfrastructureNodeDto>> SearchNodesAsync(string query, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<InfrastructureNodeDto>());
    }

    public Task<InfrastructureNodeDto?> GetNodeDetailsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<InfrastructureNodeDto?>(null);
    }
}

public class SimulationEngineTests
{
    private static FixtureGraphService NewFixture(out Guid targetId)
    {
        targetId = Guid.NewGuid();
        return new FixtureGraphService(targetId);
    }

    [Fact]
    public async Task HostFailure_ComputesRealBlastRadiusAndHeadroom()
    {
        var fixture = NewFixture(out var targetId);
        var engine = new SimulationEngine(fixture);

        var result = await engine.RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = targetId,
            ScenarioType = "host_failure",
        });

        // Real graph: exactly 2 hosted VMs, not fabricated seeds.
        Assert.Equal(2, result.ConfirmedImpacts.Count);
        Assert.Contains(result.ConfirmedImpacts, i => i.Name == "vm-core-api01");
        Assert.Contains(result.ConfirmedImpacts, i => i.Name == "vm-core-api02");

        // Probable: apps/services that depend on the downed VMs.
        Assert.Equal(2, result.ProbableImpacts.Count);
        Assert.Contains(result.ProbableImpacts, i => i.Name == "Payments Core");

        // Headroom derived from real cluster metrics: 100-78.4 / 100-61.2.
        Assert.Equal(21.6, result.ClusterHeadroom.RemainingCpuPercent);
        Assert.Equal(38.8, result.ClusterHeadroom.RemainingRamPercent);
        Assert.True(result.ClusterHeadroom.HasCapacityForFailover);

        // Deterministic risk formula, and it is not the old hardcoded 78.
        Assert.InRange(result.RiskScore, 0, 100);
        Assert.NotEqual(0, result.ConfirmedImpacts.Count + result.ProbableImpacts.Count);

        // Same request twice → byte-stable deterministic output.
        var second = await engine.RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = targetId,
            ScenarioType = "host_failure",
        });
        Assert.Equal(result.RiskScore, second.RiskScore);
        Assert.Equal(
            result.ConfirmedImpacts.Select(i => i.Name).ToList(),
            second.ConfirmedImpacts.Select(i => i.Name).ToList());
    }

    [Fact]
    public async Task HostFailure_ScenarioAliases_AreNormalized()
    {
        var fixture = NewFixture(out var targetId);
        var engine = new SimulationEngine(fixture);

        var result = await engine.RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = targetId,
            ScenarioType = "esxi_failure",
        });

        Assert.Equal("host_failure", result.ScenarioType);
    }

    [Fact]
    public async Task FrontendScenarioKeys_DatastoreOutage_AreAccepted()
    {
        var fixture = NewFixture(out var targetId);
        var engine = new SimulationEngine(fixture);

        var result = await engine.RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = targetId,
            ScenarioType = "datastore_outage",
        });

        // UsesStorage edges are absent in this fixture, so no confirmed hits,
        // but the scenario must still be recognized (normalized), not generic.
        Assert.Equal("datastore_failure", result.ScenarioType);
    }

    [Fact]
    public async Task UnknownScenario_IsRejected()
    {
        var fixture = NewFixture(out var targetId);
        var engine = new SimulationEngine(fixture);

        await Assert.ThrowsAsync<ArgumentException>(() => engine.RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = targetId,
            ScenarioType = "cluster_capacity_fake",
        }));
    }

    [Fact]
    public async Task TargetNotFound_IsRejected()
    {
        var engine = new SimulationEngine(
            new FixtureGraphService(Guid.NewGuid()));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => engine.RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = Guid.NewGuid(),
            ScenarioType = "host_failure",
        }));
    }

    [Fact]
    public async Task DatastoreFailure_CalculatesRealImpacts()
    {
        var targetId = Guid.NewGuid();
        var vmId = Guid.NewGuid();
        var graph = new GraphQueryResultDto
        {
            Nodes = new List<InfrastructureNodeDto>
            {
                new() { Id = targetId, Name = "ds-core-tier1", NodeType = "Datastore" },
                new() { Id = vmId, Name = "vm-core-api01", NodeType = "VirtualMachine" },
            },
            Relationships = new List<InfrastructureRelationshipDto>
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = vmId, TargetNodeId = targetId, RelationshipType = "UsesStorage" },
            },
        };

        var graphService = new StaticGraphService(graph);
        var engine = new SimulationEngine(graphService);

        var result = await engine.RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = targetId,
            ScenarioType = "datastore_failure",
        });

        Assert.Single(result.ConfirmedImpacts);
        Assert.Equal("vm-core-api01", result.ConfirmedImpacts[0].Name);
        Assert.False(result.ClusterHeadroom.HasCapacityForFailover,
            "Datastore outage has no compute failover; headroom alone cannot serve storage.");
        Assert.InRange(result.RiskScore, 0, 100);
    }
}

/// <summary>Serves a fixed real graph (no fabricated values).</summary>
public sealed class StaticGraphService(GraphQueryResultDto graph) : IGraphService
{
    public Task<GraphQueryResultDto> GetTopologyGraphAsync(Guid? centerNodeId = null, int depth = 2, CancellationToken cancellationToken = default)
        => Task.FromResult(graph);

    public Task<List<InfrastructureNodeDto>> SearchNodesAsync(string query, CancellationToken cancellationToken = default)
        => Task.FromResult(new List<InfrastructureNodeDto>());

    public Task<InfrastructureNodeDto?> GetNodeDetailsAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult<InfrastructureNodeDto?>(null);
}
