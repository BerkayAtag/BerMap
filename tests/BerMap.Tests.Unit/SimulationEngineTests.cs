using System;
using System.Collections.Generic;
using System.Globalization;
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

    private static InfrastructureNodeDto Node(string name, string type, Dictionary<string, string>? props = null) =>
        new() { Id = Guid.NewGuid(), Name = name, NodeType = type, Status = "healthy", Properties = props ?? new() };

    private static InfrastructureRelationshipDto Edge(InfrastructureNodeDto source, InfrastructureNodeDto target, string type) =>
        new() { Id = Guid.NewGuid(), SourceNodeId = source.Id, TargetNodeId = target.Id, RelationshipType = type };

    private static Task<ImpactAnalysisResultDto> Run(GraphQueryResultDto graph, InfrastructureNodeDto target, string scenario) =>
        new SimulationEngine(new StaticGraphService(graph)).RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = target.Id,
            ScenarioType = scenario,
        });

    /// <summary>
    /// Datacenter DC-01 Contains two clusters: CL-PROD (4 hosts, 70% CPU / 60% RAM,
    /// HA as given) and CL-SIBLING (idle, HA enabled). esx-a is a CL-PROD member
    /// hosting vm-01; a datastore and a network are contained by the datacenter.
    /// </summary>
    private sealed class ClusterTopology
    {
        public InfrastructureNodeDto Datacenter { get; } = Node("DC-01", "Datacenter");
        public InfrastructureNodeDto Cluster { get; }
        public InfrastructureNodeDto Sibling { get; } = Node("CL-SIBLING", "Cluster", new()
        {
            ["HaEnabled"] = "True",
            ["HostCount"] = "4",
            ["CpuCapacityMhz"] = "400000.0",
            ["CpuUsageMhz"] = "40000.0",
            ["MemoryCapacityMb"] = "4000.0",
            ["MemoryUsageMb"] = "400.0",
        });
        public InfrastructureNodeDto Host { get; }
        public InfrastructureNodeDto Vm { get; } = Node("vm-01", "VirtualMachine");
        public InfrastructureNodeDto App { get; } = Node("app-01", "Application");
        public InfrastructureNodeDto Datastore { get; } = Node("DS-01", "Datastore");
        public InfrastructureNodeDto Network { get; } = Node("VLAN-10", "Network");
        public GraphQueryResultDto Graph { get; }

        public ClusterTopology(string haEnabled = "True", Dictionary<string, string>? hostProps = null, Dictionary<string, string>? clusterProps = null)
        {
            Cluster = Node("CL-PROD", "Cluster", clusterProps ?? new()
            {
                ["HaEnabled"] = haEnabled,
                ["HostCount"] = "4",
                ["CpuCapacityMhz"] = "400000.0",
                ["CpuUsageMhz"] = "280000.0",
                ["MemoryCapacityMb"] = "4000.0",
                ["MemoryUsageMb"] = "2400.0",
            });
            Host = Node("esx-a", "Hypervisor", hostProps ?? new()
            {
                ["CpuCores"] = "10",
                ["CpuMhz"] = "10000.0",
                ["CpuCapacityMhz"] = "100000.0",
                ["MemoryMb"] = "1000.0",
            });
            Graph = new GraphQueryResultDto
            {
                // Sibling first so any datacenter walk would hit it before CL-PROD.
                Nodes = new() { Datacenter, Sibling, Cluster, Host, Vm, App, Datastore, Network },
                Relationships = new()
                {
                    Edge(Datacenter, Sibling, "Contains"),
                    Edge(Datacenter, Cluster, "Contains"),
                    Edge(Datacenter, Host, "Contains"),
                    Edge(Datacenter, Datastore, "Contains"),
                    Edge(Datacenter, Network, "Contains"),
                    Edge(Cluster, Host, "Contains"),
                    Edge(Vm, Host, "HostedOn"),
                    Edge(Vm, Datastore, "UsesStorage"),
                    Edge(Vm, Network, "ConnectedTo"),
                    Edge(App, Vm, "DependsOn"),
                },
            };
        }
    }

    [Fact]
    public async Task HostFailure_ComputesRealBlastRadius_AndReportsUnknownHeadroomWithoutCapacityMetrics()
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

        // The cluster stores usage percentages only: without the cluster and
        // host capacities the N-1 headroom cannot be computed, so it is Unknown.
        Assert.Equal("Unknown", result.ClusterHeadroom.BottleneckCode);
        Assert.Equal("Cluster-Core-Banking", result.ClusterHeadroom.ClusterName);
        Assert.False(result.ClusterHeadroom.HasCapacityForFailover);
        Assert.False(result.ClusterHeadroom.IsPostFailure);
        Assert.Contains(result.RecommendationItems, i => i.Code == "metrics_missing");
        Assert.Equal(result.RecommendationItems.Select(i => i.Text).ToList(), result.Recommendations);

        Assert.InRange(result.RiskScore, 0, 100);

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

    [Theory]
    [InlineData("datastore_outage", "DS-01", "datastore_failure")]
    [InlineData("storage_failure", "DS-01", "datastore_failure")]
    [InlineData("network_cut", "VLAN-10", "network_partition")]
    public async Task ScenarioAliases_AreAcceptedForMatchingTargets(string alias, string targetName, string expected)
    {
        var topology = new ClusterTopology();
        var target = topology.Graph.Nodes.Single(n => n.Name == targetName);

        var result = await Run(topology.Graph, target, alias);

        Assert.Equal(expected, result.ScenarioType);
    }

    [Fact]
    public async Task UnknownScenario_IsRejected()
    {
        var fixture = NewFixture(out var targetId);
        var engine = new SimulationEngine(fixture);

        var error = await Assert.ThrowsAsync<IntegrationValidationException>(() => engine.RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = targetId,
            ScenarioType = "cluster_capacity_fake",
        }));
        Assert.Equal("invalid_simulation", error.Code);
    }

    [Fact]
    public async Task MissingTarget_IsAValidationError_NotANotFound()
    {
        var engine = new SimulationEngine(new FixtureGraphService(Guid.NewGuid()));

        foreach (var target in new Guid?[] { null, Guid.Empty })
        {
            var error = await Assert.ThrowsAsync<IntegrationValidationException>(() => engine.RunSimulationAsync(new SimulationRunRequest
            {
                TargetNodeId = target,
                ScenarioType = "host_failure",
            }));
            Assert.Equal("invalid_simulation", error.Code);
        }
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

    [Theory]
    [InlineData("datastore_failure", "esx-a")]
    [InlineData("host_failure", "DS-01")]
    [InlineData("host_failure", "vm-01")]
    [InlineData("host_failure", "CL-PROD")]
    [InlineData("network_partition", "vm-01")]
    [InlineData("workload_outage", "esx-a")]
    [InlineData("workload_outage", "DC-01")]
    public async Task MismatchedScenarioAndTarget_IsRejected(string scenario, string targetName)
    {
        var topology = new ClusterTopology();
        var target = topology.Graph.Nodes.Single(n => n.Name == targetName);

        var error = await Assert.ThrowsAsync<IntegrationValidationException>(() => Run(topology.Graph, target, scenario));

        Assert.Equal("invalid_simulation", error.Code);
    }

    [Fact]
    public async Task HostFailure_SubtractsTheFailedHostCapacity_NMinusOne()
    {
        // 4 x 100 GHz hosts at 70% CPU: 280 GHz of load on 300 GHz of survivors
        // leaves 6.7%, below the 10% admission buffer. RAM: 2400 / 3000 MB → 20%.
        var topology = new ClusterTopology();

        var result = await Run(topology.Graph, topology.Host, "host_failure");

        var headroom = result.ClusterHeadroom;
        Assert.True(headroom.IsPostFailure);
        Assert.Equal("CL-PROD", headroom.ClusterName);
        Assert.Equal(6.7, headroom.RemainingCpuPercent);
        Assert.Equal(20.0, headroom.RemainingRamPercent);
        Assert.False(headroom.HasCapacityForFailover);
        Assert.Equal("Cpu", headroom.BottleneckCode);
        Assert.Contains(result.RecommendationItems, i => i.Code == "failover_not_confirmed");
        Assert.DoesNotContain(result.RecommendationItems, i => i.Code == "headroom_sufficient");
    }

    [Fact]
    public async Task HostFailure_NMinusOne_UsesPercentUsageAndHostCountFallbacks()
    {
        // Audit demo numbers: CL-PROD-01 has 4 hosts, 665600 MHz at 68% CPU and
        // 3145728 MB at 74% RAM; the host has no capacity properties, so an even
        // quarter of the cluster leaves. Survivors: 90.7% CPU, 98.7% RAM used.
        var topology = new ClusterTopology(
            hostProps: new Dictionary<string, string>(),
            clusterProps: new()
            {
                ["HaEnabled"] = "True",
                ["HostCount"] = "4",
                ["CpuCapacityMhz"] = "665600.0",
                ["CpuUsagePercent"] = "68.00",
                ["MemoryCapacityMb"] = "3145728.0",
                ["MemoryUsagePercent"] = "74.00",
            });

        var result = await Run(topology.Graph, topology.Host, "host_failure");

        Assert.True(result.ClusterHeadroom.IsPostFailure);
        Assert.Equal(9.3, result.ClusterHeadroom.RemainingCpuPercent);
        Assert.Equal(1.3, result.ClusterHeadroom.RemainingRamPercent);
        Assert.False(result.ClusterHeadroom.HasCapacityForFailover);
        Assert.Equal("Ram", result.ClusterHeadroom.BottleneckCode);
    }

    [Fact]
    public async Task HostFailure_WithoutAnyHostCapacity_IsUnknown()
    {
        var topology = new ClusterTopology(
            hostProps: new Dictionary<string, string> { ["CpuCapacityMhz"] = "0.0", ["MemoryMb"] = "0.0" },
            clusterProps: new()
            {
                ["CpuCapacityMhz"] = "400000.0",
                ["CpuUsageMhz"] = "280000.0",
                ["MemoryCapacityMb"] = "4000.0",
                ["MemoryUsageMb"] = "2400.0",
            });

        var result = await Run(topology.Graph, topology.Host, "host_failure");

        Assert.Equal("Unknown", result.ClusterHeadroom.BottleneckCode);
        Assert.False(result.ClusterHeadroom.IsPostFailure);
        Assert.Contains(result.RecommendationItems, i => i.Code == "metrics_missing");
    }

    [Fact]
    public async Task HostFailure_SufficientHeadroom_UsesInvariantNumbers()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            var topology = new ClusterTopology(clusterProps: new()
            {
                ["HaEnabled"] = "True",
                ["CpuCapacityMhz"] = "400000.0",
                ["CpuUsageMhz"] = "120000.0",
                ["MemoryCapacityMb"] = "4000.0",
                ["MemoryUsageMb"] = "1500.0",
            });

            var result = await Run(topology.Graph, topology.Host, "host_failure");

            // 120/300 GHz → 60% left; 1500/3000 MB → 50% left.
            Assert.True(result.ClusterHeadroom.HasCapacityForFailover);
            Assert.Equal("None", result.ClusterHeadroom.BottleneckCode);
            var item = Assert.Single(result.RecommendationItems, i => i.Code == "headroom_sufficient");
            Assert.Equal("60.0", item.Values["cpu"]);
            Assert.Equal("50.0", item.Values["ram"]);
            Assert.Contains("CPU 60.0%, RAM 50.0%", item.Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task HaDisabled_IsReportedForTheOwningClusterOnly()
    {
        // CL-PROD has HA disabled; the HA-enabled sibling in the same
        // datacenter must not mask the warning.
        var noHa = new ClusterTopology(haEnabled: "False");
        var result = await Run(noHa.Graph, noHa.Host, "host_failure");

        var ha = Assert.Single(result.RecommendationItems, i => i.Code == "ha_disabled");
        Assert.Equal("CL-PROD", ha.Values["cluster"]);

        // HA enabled on the owning cluster: no warning, even though a sibling
        // could be anything.
        var withHa = new ClusterTopology(haEnabled: "True");
        var resultWithHa = await Run(withHa.Graph, withHa.Host, "host_failure");
        Assert.DoesNotContain(resultWithHa.RecommendationItems, i => i.Code == "ha_disabled");
    }

    [Fact]
    public void FindOwningCluster_ResolvesClusterHostAndWorkload_AndNeverWalksTheDatacenter()
    {
        var topology = new ClusterTopology();
        var graph = topology.Graph;

        Assert.Equal(topology.Cluster.Id, SimulationEngine.FindOwningCluster(topology.Cluster, graph));
        Assert.Equal(topology.Cluster.Id, SimulationEngine.FindOwningCluster(topology.Host, graph));
        Assert.Equal(topology.Cluster.Id, SimulationEngine.FindOwningCluster(topology.Vm, graph));
        Assert.Null(SimulationEngine.FindOwningCluster(topology.Datastore, graph));
        Assert.Null(SimulationEngine.FindOwningCluster(topology.Network, graph));
        Assert.Null(SimulationEngine.FindOwningCluster(topology.Datacenter, graph));

        // A host with a MemberOf edge (instead of cluster Contains host) resolves too.
        var memberHost = Node("esx-b", "Hypervisor");
        graph.Nodes.Add(memberHost);
        graph.Relationships.Add(Edge(memberHost, topology.Sibling, "MemberOf"));
        graph.Relationships.Add(Edge(topology.Datacenter, memberHost, "Contains"));
        Assert.Equal(topology.Sibling.Id, SimulationEngine.FindOwningCluster(memberHost, graph));
    }

    [Theory]
    [InlineData("datastore_failure", "DS-01")]
    [InlineData("network_partition", "VLAN-10")]
    public async Task StorageAndNetworkTargets_HaveNoComputeHeadroom(string scenario, string targetName)
    {
        var topology = new ClusterTopology();
        var target = topology.Graph.Nodes.Single(n => n.Name == targetName);

        var result = await Run(topology.Graph, target, scenario);

        Assert.Equal("NotApplicable", result.ClusterHeadroom.BottleneckCode);
        Assert.Equal(string.Empty, result.ClusterHeadroom.ClusterName);
        Assert.False(result.ClusterHeadroom.IsPostFailure);
        Assert.Equal("vm-01", Assert.Single(result.ConfirmedImpacts).Name);
    }

    [Fact]
    public async Task WorkloadOutage_ReportsTheHostingClusterHeadroom()
    {
        var topology = new ClusterTopology();

        var result = await Run(topology.Graph, topology.Vm, "workload_outage");

        // Current (not N-1) headroom of the cluster the VM runs in.
        Assert.Equal("CL-PROD", result.ClusterHeadroom.ClusterName);
        Assert.False(result.ClusterHeadroom.IsPostFailure);
        Assert.Equal(30.0, result.ClusterHeadroom.RemainingCpuPercent);
        Assert.Equal(40.0, result.ClusterHeadroom.RemainingRamPercent);
        Assert.Equal("app-01", Assert.Single(result.ConfirmedImpacts).Name);
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

    [Fact]
    public async Task HostFailure_PathsFollowEdgeDirection_AndWorkloadsDoNotTakeDownTheirNetwork()
    {
        var hostId = Guid.NewGuid();
        var vmId = Guid.NewGuid();
        var appId = Guid.NewGuid();
        var networkId = Guid.NewGuid();
        var graph = new GraphQueryResultDto
        {
            Nodes = new List<InfrastructureNodeDto>
            {
                new() { Id = hostId, Name = "esx01", NodeType = "Hypervisor" },
                new() { Id = vmId, Name = "mq-broker-01", NodeType = "VirtualMachine" },
                new() { Id = appId, Name = "app-core-02", NodeType = "VirtualMachine" },
                new() { Id = networkId, Name = "VLAN-204-App", NodeType = "Network" },
            },
            Relationships = new List<InfrastructureRelationshipDto>
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = vmId, TargetNodeId = hostId, RelationshipType = "HostedOn" },
                new() { Id = Guid.NewGuid(), SourceNodeId = appId, TargetNodeId = vmId, RelationshipType = "DependsOn" },
                new() { Id = Guid.NewGuid(), SourceNodeId = vmId, TargetNodeId = networkId, RelationshipType = "ConnectedTo" },
            },
        };

        var result = await new SimulationEngine(new StaticGraphService(graph)).RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = hostId,
            ScenarioType = "host_failure",
        });

        var probable = Assert.Single(result.ProbableImpacts);
        Assert.Equal("app-core-02", probable.Name);
        Assert.Equal("app-core-02 -> [DependsOn] -> mq-broker-01", probable.DependencyPath);
        Assert.DoesNotContain(result.ProbableImpacts.Concat(result.PossibleImpacts), i => i.Name == "VLAN-204-App");

        // Standalone host (no cluster): no failover target for its workloads.
        Assert.Equal("NotApplicable", result.ClusterHeadroom.BottleneckCode);
        Assert.Contains(result.RecommendationItems, i => i.Code == "single_point_of_failure");
    }

    [Fact]
    public async Task WorkloadOutage_ImpactsItsDependents_NotItsHostOrDatastore()
    {
        var hostId = Guid.NewGuid();
        var datastoreId = Guid.NewGuid();
        var dbId = Guid.NewGuid();
        var appId = Guid.NewGuid();
        var webId = Guid.NewGuid();
        var portalId = Guid.NewGuid();
        var graph = new GraphQueryResultDto
        {
            Nodes = new List<InfrastructureNodeDto>
            {
                new() { Id = hostId, Name = "esx02", NodeType = "Hypervisor" },
                new() { Id = datastoreId, Name = "DS-01", NodeType = "Datastore" },
                new() { Id = dbId, Name = "sql-core-01", NodeType = "Database" },
                new() { Id = appId, Name = "app-core-01", NodeType = "VirtualMachine" },
                new() { Id = webId, Name = "web-portal-01", NodeType = "VirtualMachine" },
                new() { Id = portalId, Name = "Customer Portal", NodeType = "Application" },
            },
            Relationships = new List<InfrastructureRelationshipDto>
            {
                new() { Id = Guid.NewGuid(), SourceNodeId = dbId, TargetNodeId = hostId, RelationshipType = "HostedOn" },
                new() { Id = Guid.NewGuid(), SourceNodeId = dbId, TargetNodeId = datastoreId, RelationshipType = "UsesStorage" },
                new() { Id = Guid.NewGuid(), SourceNodeId = appId, TargetNodeId = dbId, RelationshipType = "UsesDatabase" },
                new() { Id = Guid.NewGuid(), SourceNodeId = webId, TargetNodeId = appId, RelationshipType = "DependsOn" },
                new() { Id = Guid.NewGuid(), SourceNodeId = portalId, TargetNodeId = webId, RelationshipType = "DependsOn" },
            },
        };

        var result = await new SimulationEngine(new StaticGraphService(graph)).RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = dbId,
            ScenarioType = "workload_outage",
        });

        Assert.Equal("workload_outage", result.ScenarioType);
        var confirmed = Assert.Single(result.ConfirmedImpacts);
        Assert.Equal("app-core-01", confirmed.Name);
        Assert.Equal("app-core-01 -> [UsesDatabase] -> sql-core-01", confirmed.DependencyPath);
        Assert.Equal("web-portal-01", Assert.Single(result.ProbableImpacts).Name);
        Assert.Equal("Customer Portal", Assert.Single(result.PossibleImpacts).Name);
        Assert.DoesNotContain(result.ConfirmedImpacts.Concat(result.ProbableImpacts).Concat(result.PossibleImpacts),
            i => i.Name is "esx02" or "DS-01");

        // The host has no cluster in this graph: compute headroom does not apply.
        Assert.Equal("NotApplicable", result.ClusterHeadroom.BottleneckCode);
    }

    [Fact]
    public async Task Recommendations_AreStructured_WithoutFixedMaintenanceHoursOrDoubledEllipsis()
    {
        var host = Node("esx01", "Hypervisor");
        var vm = Node("mq-01", "VirtualMachine");
        var nodes = new List<InfrastructureNodeDto> { host, vm };
        var edges = new List<InfrastructureRelationshipDto> { Edge(vm, host, "HostedOn") };
        foreach (var name in new[] { "svc-a", "svc-b", "svc-c", "svc-d", "svc-e" })
        {
            var svc = Node(name, "Service");
            nodes.Add(svc);
            edges.Add(Edge(svc, vm, "DependsOn"));
        }
        var graph = new GraphQueryResultDto { Nodes = nodes, Relationships = edges };

        var result = await Run(graph, host, "host_failure");

        var unavailable = Assert.Single(result.RecommendationItems, i => i.Code == "workloads_unavailable");
        Assert.Equal("1", unavailable.Values["count"]);
        Assert.Equal("esx01", unavailable.Values["target"]);

        var degrade = Assert.Single(result.RecommendationItems, i => i.Code == "dependents_degrade");
        Assert.Equal("5", degrade.Values["count"]);
        Assert.Equal("svc-a, svc-b, svc-c, …", degrade.Values["names"]);

        Assert.Equal(result.RecommendationItems.Select(i => i.Text).ToList(), result.Recommendations);
        Assert.All(result.Recommendations, text =>
        {
            Assert.DoesNotContain("....", text);
            Assert.DoesNotContain("banking", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("09:00", text);
        });
    }

    [Fact]
    public async Task NoConfirmedImpact_ReturnsNoDirectImpactOnly()
    {
        var topology = new ClusterTopology();
        var idleHost = Node("esx-idle", "Hypervisor");
        topology.Graph.Nodes.Add(idleHost);
        topology.Graph.Relationships.Add(Edge(topology.Cluster, idleHost, "Contains"));

        var result = await Run(topology.Graph, idleHost, "host_failure");

        var item = Assert.Single(result.RecommendationItems);
        Assert.Equal("no_direct_impact", item.Code);
        Assert.Equal("esx-idle", item.Values["target"]);
    }

    [Fact]
    public async Task StaleEdgesAndNodes_AreNotImpacts_AfterAVMotion()
    {
        // vm-01 moved from esx-a (CL-PROD) to esx-b (CL-SIBLING): the old
        // HostedOn edge stays as stale history next to the live one.
        var topology = new ClusterTopology();
        var newHost = Node("esx-b", "Hypervisor");
        var retired = Node("app-retired", "Application");
        retired.IsStale = true;
        topology.Graph.Nodes.AddRange(new[] { newHost, retired });
        topology.Graph.Relationships.Single(r => r.SourceNodeId == topology.Vm.Id && r.RelationshipType == "HostedOn").IsStale = true;
        topology.Graph.Relationships.Add(Edge(topology.Vm, newHost, "HostedOn"));
        topology.Graph.Relationships.Add(Edge(topology.Sibling, newHost, "Contains"));
        topology.Graph.Relationships.Add(Edge(retired, topology.Vm, "DependsOn"));

        var oldHost = await Run(topology.Graph, topology.Host, "host_failure");
        Assert.Empty(oldHost.ConfirmedImpacts);
        Assert.Equal("no_direct_impact", Assert.Single(oldHost.RecommendationItems).Code);

        var liveHost = await Run(topology.Graph, newHost, "host_failure");
        Assert.Equal("vm-01", Assert.Single(liveHost.ConfirmedImpacts).Name);
        // The stale dependent is history, the live one still degrades.
        Assert.Equal("app-01", Assert.Single(liveHost.ProbableImpacts).Name);

        // The workload's cluster follows its live host, not the stale one.
        var workload = await Run(topology.Graph, topology.Vm, "workload_outage");
        Assert.Equal("CL-SIBLING", workload.ClusterHeadroom.ClusterName);
    }

    [Fact]
    public async Task StaleTarget_IsStillSimulated()
    {
        var topology = new ClusterTopology();
        topology.Host.IsStale = true;

        var result = await Run(topology.Graph, topology.Host, "host_failure");

        Assert.Equal("vm-01", Assert.Single(result.ConfirmedImpacts).Name);
    }

    [Fact]
    public async Task TruncatedGraph_IsSurfaced_EvenWithoutDirectImpact()
    {
        var topology = new ClusterTopology();
        topology.Graph.IsTruncated = true;

        var hit = await Run(topology.Graph, topology.Host, "host_failure");
        Assert.True(hit.GraphTruncated);
        var item = hit.RecommendationItems[^1];
        Assert.Equal("graph_truncated", item.Code);
        Assert.Equal("esx-a", item.Values["target"]);
        Assert.Equal(item.Text, hit.Recommendations[^1]);

        var idleHost = Node("esx-idle", "Hypervisor");
        topology.Graph.Nodes.Add(idleHost);
        var idle = await Run(topology.Graph, idleHost, "host_failure");
        Assert.Equal(new[] { "no_direct_impact", "graph_truncated" }, idle.RecommendationItems.Select(i => i.Code));

        topology.Graph.IsTruncated = false;
        var complete = await Run(topology.Graph, topology.Host, "host_failure");
        Assert.False(complete.GraphTruncated);
        Assert.DoesNotContain(complete.RecommendationItems, i => i.Code == "graph_truncated");
    }

    [Fact]
    public async Task ImpactGraphSource_IsPreferredOverTheTopologyGraph()
    {
        var topology = new ClusterTopology();
        var source = new ImpactOnlyGraphService(topology.Graph);

        var result = await new SimulationEngine(source).RunSimulationAsync(new SimulationRunRequest
        {
            TargetNodeId = topology.Host.Id,
            ScenarioType = "host_failure",
        });

        Assert.Equal((topology.Host.Id, 3), source.ImpactRequest);
        Assert.Equal("vm-01", Assert.Single(result.ConfirmedImpacts).Name);
    }

    /// <summary>Serves the graph only as an impact graph; the topology graph is empty.</summary>
    private sealed class ImpactOnlyGraphService(GraphQueryResultDto graph) : IGraphService, IImpactGraphSource
    {
        public (Guid, int)? ImpactRequest { get; private set; }

        public Task<GraphQueryResultDto> GetImpactGraphAsync(Guid targetNodeId, int depth, CancellationToken cancellationToken = default)
        {
            ImpactRequest = (targetNodeId, depth);
            return Task.FromResult(graph);
        }

        public Task<GraphQueryResultDto> GetTopologyGraphAsync(Guid? centerNodeId = null, int depth = 2, CancellationToken cancellationToken = default)
            => Task.FromResult(new GraphQueryResultDto());

        public Task<List<InfrastructureNodeDto>> SearchNodesAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<InfrastructureNodeDto>());

        public Task<InfrastructureNodeDto?> GetNodeDetailsAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<InfrastructureNodeDto?>(null);
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
