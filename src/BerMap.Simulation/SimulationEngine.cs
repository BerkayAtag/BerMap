using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Domain.Enums;

namespace BerMap.Simulation;

/// <summary>
/// Deterministic impact-analysis engine. Every result is derived from the real
/// topology graph served by IGraphService — the live graph, or the frozen graph
/// of a snapshot when the request names one: a scenario run NEVER fabricates
/// demo nodes, fixed risk scores or hardcoded capacity numbers. When the data
/// required for a metric is absent (e.g. vCenter metrics were not collected),
/// the metric is reported as unknown rather than estimated.
/// </summary>
public class SimulationEngine : ISimulationEngine
{
    internal const string InvalidSimulationCode = "invalid_simulation";

    // Failover admission buffer: below this remaining share the cluster is
    // treated as unable to absorb the outage.
    private const double AdmissionBufferPercent = 10;

    // Edge types that must NOT be traversed for impact propagation.
    private static readonly HashSet<string> ExcludedEdgeTypes = new(StringComparer.OrdinalIgnoreCase)
    { "Contains", "MemberOf", "ManagedBy", "Trusts", "PossibleDuplicate" };

    // Shared infrastructure never depends on a single workload: a VM going
    // down does not take its portgroup, datastore or host down with it.
    private static readonly HashSet<string> InfrastructureNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Datacenter", "Site", "Forest", "Domain", "OrganizationalUnit", "Cluster", "Hypervisor",
        "ResourcePool", "Storage", "Datastore", "Network", "NetworkDevice", "Group"
    };

    private static readonly string[] HostNodeTypes = { nameof(NodeType.Hypervisor), nameof(NodeType.PhysicalServer) };

    // Which node types each scenario can meaningfully fail.
    private static readonly Dictionary<string, string[]> ValidTargetTypes = new(StringComparer.Ordinal)
    {
        ["host_failure"] = HostNodeTypes,
        ["datastore_failure"] = new[] { nameof(NodeType.Datastore), nameof(NodeType.Storage) },
        ["network_partition"] = new[] { nameof(NodeType.Network), nameof(NodeType.NetworkDevice) },
        ["workload_outage"] = new[]
        {
            nameof(NodeType.VirtualMachine), nameof(NodeType.Computer), nameof(NodeType.DomainController),
            nameof(NodeType.Application), nameof(NodeType.Service), nameof(NodeType.Database), nameof(NodeType.Endpoint),
        },
    };

    private const string SupportedScenarios = "host_failure, datastore_failure, network_partition, workload_outage";

    private readonly IGraphService _graphService;

    public SimulationEngine(IGraphService graphService)
    {
        _graphService = graphService;
    }

    public async Task<ImpactAnalysisResultDto> RunSimulationAsync(SimulationRunRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ScenarioType))
            throw new IntegrationValidationException(InvalidSimulationCode, "Scenario type is required.");

        if (!IsKnownScenario(request.ScenarioType))
            throw new IntegrationValidationException(InvalidSimulationCode,
                $"Unknown scenario type '{request.ScenarioType}'. Supported: {SupportedScenarios}.");

        if (request.TargetNodeId is not { } targetNodeId || targetNodeId == Guid.Empty)
            throw new IntegrationValidationException(InvalidSimulationCode, "targetNodeId is required.");

        if (request.SnapshotId == Guid.Empty)
            throw new IntegrationValidationException(InvalidSimulationCode,
                "snapshotId must be a snapshot id; omit it to simulate on the live graph.");

        var scenario = NormalizeScenario(request.ScenarioType);

        // The basis: the live graph, or the frozen graph of one snapshot. Both
        // go through the same impact walk and the same stale filter below.
        SnapshotImpactGraph? snapshot = null;
        GraphQueryResultDto fetched;
        if (request.SnapshotId is { } snapshotId)
        {
            if (_graphService is not IImpactGraphSource snapshotSource)
                throw new IntegrationValidationException(InvalidSimulationCode,
                    "This server cannot read snapshot graphs; omit snapshotId to simulate on the live graph.");

            snapshot = await snapshotSource.GetSnapshotImpactGraphAsync(snapshotId, targetNodeId, depth: 3, cancellationToken)
                ?? throw new SimulationNotFoundException(SimulationNotFoundException.SnapshotNotFoundCode,
                    $"Snapshot {snapshotId} was not found.");
            fetched = snapshot.Graph;
        }
        else
        {
            fetched = _graphService is IImpactGraphSource impactSource
                ? await impactSource.GetImpactGraphAsync(targetNodeId, depth: 3, cancellationToken)
                : await _graphService.GetTopologyGraphAsync(targetNodeId, depth: 3, cancellationToken);
        }

        var graph = LiveGraph(fetched, targetNodeId);
        var targetNode = graph.Nodes.FirstOrDefault(n => n.Id == targetNodeId)
            ?? throw (snapshot == null
                ? new KeyNotFoundException(
                    $"Target node {targetNodeId} was not found in the inventory topology graph. Run a discovery first.")
                : new SimulationNotFoundException(SimulationNotFoundException.NotInSnapshotCode,
                    $"Target node {targetNodeId} is not in snapshot '{snapshot.SnapshotName}'; it was discovered after the snapshot was captured or had been removed before it."));

        var validTypes = ValidTargetTypes[scenario];
        if (!MatchesAny(targetNode.NodeType, validTypes))
            throw new IntegrationValidationException(InvalidSimulationCode,
                $"Scenario '{scenario}' cannot target {targetNode.Name} ({targetNode.NodeType}). " +
                $"Valid target types: {string.Join(", ", validTypes)}.");

        var result = new ImpactAnalysisResultDto
        {
            TargetNodeId = targetNodeId,
            TargetNodeName = targetNode.Name,
            ScenarioType = scenario,
            GraphTruncated = graph.IsTruncated,
            SnapshotId = snapshot?.SnapshotId,
            SnapshotName = snapshot?.SnapshotName,
            SnapshotCreatedAt = snapshot?.SnapshotCreatedAt,
        };

        switch (scenario)
        {
            case "host_failure":
                CalculateBlastRadius(targetNode, graph, result,
                    directEdgeTypes: new[] { "HostedOn" }, directEntityLabel: "VM");
                break;

            case "datastore_failure":
                CalculateBlastRadius(targetNode, graph, result,
                    directEdgeTypes: new[] { "UsesStorage", "MountedOn" }, directEntityLabel: "workload");
                break;

            case "network_partition":
                CalculateBlastRadius(targetNode, graph, result,
                    directEdgeTypes: new[] { "ConnectedTo", "ConnectsThrough" }, directEntityLabel: "endpoint");
                break;

            case "workload_outage":
                // The workload itself fails: nothing is placed on it, so the
                // confirmed hits are the entities that depend on it directly
                // (no placement edges), probable/possible ones further out.
                CalculateBlastRadius(targetNode, graph, result,
                    directEdgeTypes: Array.Empty<string>(), directEntityLabel: "dependent");
                break;
        }

        var nodesById = graph.Nodes.ToDictionary(n => n.Id);

        // Storage and network outages have no single compute cluster: their
        // consumers can span several clusters, so compute headroom does not apply.
        var clusterNode = scenario is "host_failure" or "workload_outage"
            ? FindOwningCluster(targetNode, graph) is { } clusterId ? nodesById.GetValueOrDefault(clusterId) : null
            : null;

        result.ClusterHeadroom = CalculateClusterHeadroom(targetNode, clusterNode, scenario);
        result.RiskScore = CalculateRiskScore(result, scenario);
        result.RecommendationItems = BuildRecommendations(targetNode, clusterNode, scenario, result);
        if (result.GraphTruncated)
        {
            // A capped graph can miss workloads and dependents: the result is a
            // lower bound, not the whole blast radius.
            result.RecommendationItems.Add(new RecommendationItemDto
            {
                Code = "graph_truncated",
                Text = $"The dependency graph around {targetNode.Name} exceeded the analysis limit; impacts and the risk score are a lower bound.",
                Values = new Dictionary<string, string> { ["target"] = targetNode.Name },
            });
        }
        result.Recommendations = result.RecommendationItems.Select(i => i.Text).ToList();

        return result;
    }

    /// <summary>
    /// Stale nodes and relationships are history (a VM's old HostedOn edge
    /// after a vMotion, the data of a deleted integration): they must not
    /// count as impact. The target itself is kept so a stale target still
    /// gets a clear answer instead of "not found".
    /// </summary>
    internal static GraphQueryResultDto LiveGraph(GraphQueryResultDto graph, Guid targetNodeId)
    {
        var staleNodeIds = graph.Nodes
            .Where(n => n.IsStale && n.Id != targetNodeId)
            .Select(n => n.Id)
            .ToHashSet();

        return new GraphQueryResultDto
        {
            Nodes = graph.Nodes.Where(n => !staleNodeIds.Contains(n.Id)).ToList(),
            Relationships = graph.Relationships
                .Where(r => !r.IsStale && !staleNodeIds.Contains(r.SourceNodeId) && !staleNodeIds.Contains(r.TargetNodeId))
                .ToList(),
            IsTruncated = graph.IsTruncated,
        };
    }

    internal static string NormalizeScenario(string scenarioType) => scenarioType.Trim().ToLowerInvariant() switch
    {
        "host_failure" or "esxi_failure" => "host_failure",
        "datastore_failure" or "datastore_outage" or "storage_failure" => "datastore_failure",
        "network_partition" or "network_cut" => "network_partition",
        "workload_outage" => "workload_outage",
        _ => scenarioType.Trim().ToLowerInvariant(),
    };

    internal static bool IsKnownScenario(string scenarioType) =>
        ValidTargetTypes.ContainsKey(NormalizeScenario(scenarioType));

    /// <summary>
    /// Real blast-radius computation:
    ///  1. Confirmed impacts = nodes directly attached to the failed target.
    ///  2. Probable impacts  = dependents reachable through one more hop.
    ///  3. Possible impacts  = dependents two more hops away.
    /// All hops are walked over the dependency edge types above; "Contains /
    /// MemberOf" style organizational edges are excluded.
    /// </summary>
    private static void CalculateBlastRadius(
        InfrastructureNodeDto targetNode,
        GraphQueryResultDto graph,
        ImpactAnalysisResultDto result,
        string[] directEdgeTypes,
        string directEntityLabel)
    {
        var nodesById = graph.Nodes.ToDictionary(n => n.Id);
        var targetIsInfrastructure = InfrastructureNodeTypes.Contains(targetNode.NodeType);

        // ---- Step 1: direct hits -------------------------------------------------
        // Placement edges point from the dependent to what it runs on
        // (vm -HostedOn-> host, vm -UsesStorage-> datastore), so only their
        // source is hit; a failed VM never takes its host or datastore down.
        // Connectivity edges are symmetric, except that a workload never takes
        // shared infrastructure down with it.
        var directHits = new List<(Guid Id, string Path)>();

        foreach (var rel in graph.Relationships)
        {
            if (!MatchesAny(rel.RelationshipType, directEdgeTypes)) continue;

            Guid other;
            if (rel.TargetNodeId == targetNode.Id) other = rel.SourceNodeId;
            else if (rel.SourceNodeId == targetNode.Id &&
                     MatchesAny(rel.RelationshipType, new[] { "ConnectedTo", "ConnectsThrough" })) other = rel.TargetNodeId;
            else continue;

            if (!targetIsInfrastructure && nodesById.TryGetValue(other, out var otherNode) &&
                InfrastructureNodeTypes.Contains(otherNode.NodeType))
                continue;

            if (directHits.Any(h => h.Id == other)) continue;
            directHits.Add((other, $"{targetNode.Name} -> [DOWN] -> {nodesById.GetValueOrDefault(other)?.Name ?? other.ToString()}"));
        }

        // A failed workload (workload_outage passes no placement edges) breaks
        // whatever depends on it directly. A bare-metal server that hosts no
        // VMs (host_failure on a PhysicalServer) is treated the same way.
        if (directEdgeTypes.Length == 0 || (directHits.Count == 0 && !targetIsInfrastructure))
        {
            foreach (var dependent in FindDependents(new HashSet<Guid> { targetNode.Id }, graph, nodesById))
            {
                if (dependent.DependentId != targetNode.Id && directHits.All(h => h.Id != dependent.DependentId))
                    directHits.Add(dependent);
            }
        }

        var directHitSet = new HashSet<Guid>(directHits.Select(h => h.Id));
        foreach (var (hitId, path) in directHits)
        {
            var hitNode = nodesById.GetValueOrDefault(hitId);
            result.ConfirmedImpacts.Add(new ImpactedEntityDto
            {
                Id = hitId,
                Name = hitNode?.Name ?? hitId.ToString(),
                Type = hitNode?.NodeType ?? directEntityLabel,
                ImpactLevel = "Confirmed",
                DependencyPath = path,
            });
        }

        // ---- Step 2: dependents of confirmed hits (probable) ----------------------
        var probableByEntity = new Dictionary<Guid, (string Path, string Name, string Type)>();

        foreach (var (dependentId, path) in FindDependents(directHitSet, graph, nodesById))
        {
            if (directHitSet.Contains(dependentId) || dependentId == targetNode.Id) continue;
            if (!probableByEntity.ContainsKey(dependentId))
            {
                var node = nodesById.GetValueOrDefault(dependentId);
                probableByEntity[dependentId] = (path, node?.Name ?? dependentId.ToString(), node?.NodeType ?? "Unknown");
            }
        }
        foreach (var (dependentId, value) in probableByEntity)
        {
            result.ProbableImpacts.Add(new ImpactedEntityDto
            {
                Id = dependentId,
                Name = value.Name,
                Type = value.Type,
                ImpactLevel = "Probable",
                DependencyPath = value.Path,
            });
        }
        var probableSet = new HashSet<Guid>(probableByEntity.Keys);

        // ---- Step 3: dependents of probable hits (possible) -----------------------
        foreach (var (dependentId, path) in FindDependents(probableSet, graph, nodesById))
        {
            if (directHitSet.Contains(dependentId) || probableSet.Contains(dependentId) ||
                dependentId == targetNode.Id) continue;

            var node = nodesById.GetValueOrDefault(dependentId);
            result.PossibleImpacts.Add(new ImpactedEntityDto
            {
                Id = dependentId,
                Name = node?.Name ?? dependentId.ToString(),
                Type = node?.NodeType ?? "Unknown",
                ImpactLevel = "Possible",
                DependencyPath = path,
            });
        }

        // deterministic ordering keeps repeated runs byte-stable
        SortImpacts(result.ConfirmedImpacts);
        SortImpacts(result.ProbableImpacts);
        SortImpacts(result.PossibleImpacts);
    }

    /// <summary>
    /// Finds entities that DEPEND on the hit set. Dependency-edge direction
    /// contract: the dependent entity is the SOURCE of the edge, except for
    /// symmetric connect/replication types where the other endpoint depends.
    /// HostedOn / UsesStorage / MountedOn / Contains / MemberOf are placement
    /// edges and never produce dependents (a downed host is not the dependent
    /// of the VMs it hosts).
    /// </summary>
    private static List<(Guid DependentId, string Path)> FindDependents(
        HashSet<Guid> hitSet,
        GraphQueryResultDto graph,
        Dictionary<Guid, InfrastructureNodeDto> nodesById)
    {
        var found = new List<(Guid DependentId, string Path)>();
        var seen = new HashSet<Guid>();

        foreach (var rel in graph.Relationships)
        {
            if (ExcludedEdgeTypes.Contains(rel.RelationshipType)) continue;
            if (MatchesAny(rel.RelationshipType, new[] { "HostedOn", "UsesStorage", "MountedOn" })) continue;
            if (rel.SourceNodeId == rel.TargetNodeId) continue;

            // a -DependsOn-> hit : a depends on the hit
            Guid dependentId;
            Guid hitId;

            if (hitSet.Contains(rel.TargetNodeId) &&
                MatchesAny(rel.RelationshipType, new[] { "DependsOn", "RunsOn", "UsesDatabase" }))
            {
                hitId = rel.TargetNodeId;
                dependentId = rel.SourceNodeId;
            }
            else if (hitSet.Contains(rel.SourceNodeId) &&
                     MatchesAny(rel.RelationshipType, new[] { "ConnectedTo", "ConnectsThrough", "ReplicatesTo" }))
            {
                // hit -ConnectedTo-> b : b loses a path to the hit
                hitId = rel.SourceNodeId;
                dependentId = rel.TargetNodeId;
            }
            else
            {
                continue;
            }

            if (hitSet.Contains(dependentId)) continue;

            var hitIsInfrastructure = nodesById.TryGetValue(hitId, out var hitNode) &&
                                      InfrastructureNodeTypes.Contains(hitNode.NodeType);
            var dependentIsInfrastructure = nodesById.TryGetValue(dependentId, out var dependentNode) &&
                                            InfrastructureNodeTypes.Contains(dependentNode.NodeType);
            if (dependentIsInfrastructure && !hitIsInfrastructure) continue;

            if (!seen.Add(dependentId)) continue;

            // The path shows the edge as stored (source -> target), so
            // "app -[DependsOn]-> db" reads as the dependency it is.
            var sourceName = nodesById.GetValueOrDefault(rel.SourceNodeId)?.Name ?? rel.SourceNodeId.ToString();
            var targetName = nodesById.GetValueOrDefault(rel.TargetNodeId)?.Name ?? rel.TargetNodeId.ToString();
            found.Add((dependentId, $"{sourceName} -> [{rel.RelationshipType}] -> {targetName}"));
        }

        return found;
    }

    /// <summary>
    /// Cluster headroom from the real vCenter metrics stored on the owning
    /// cluster and the failed host. host_failure reports N-1 headroom: the
    /// failed host's capacity leaves the cluster while its load stays (its VMs
    /// restart on the survivors). workload_outage reports the cluster's current
    /// headroom. Missing metrics give an explicit Unknown verdict; storage and
    /// network outages (or a target without a cluster) are NotApplicable.
    /// </summary>
    private static ClusterHeadroomDto CalculateClusterHeadroom(
        InfrastructureNodeDto targetNode,
        InfrastructureNodeDto? clusterNode,
        string scenario)
    {
        switch (scenario)
        {
            case "datastore_failure":
                return NotApplicable("Storage I/O path loss - failover needs replication, not compute headroom");
            case "network_partition":
                return NotApplicable("Network path loss - failover needs redundant paths, not compute headroom");
        }

        if (clusterNode == null)
        {
            return NotApplicable(scenario == "host_failure"
                ? "No owning cluster - a standalone host has no failover capacity for its workloads"
                : "No hosting cluster found - compute headroom does not apply to this workload");
        }

        double? cpuRemaining;
        double? ramRemaining;
        var isPostFailure = scenario == "host_failure";

        if (isPostFailure)
        {
            // Without per-host capacity, an even share of the cluster is the
            // failed host's capacity when the host count is known.
            var hostCount = ReadPositive(clusterNode, "HostCount");
            var clusterCpu = ReadPositive(clusterNode, "CpuCapacityMhz");
            var clusterRam = ReadPositive(clusterNode, "MemoryCapacityMb");

            cpuRemaining = PostFailureRemainingPercent(
                clusterCpu,
                ReadClusterUsage(clusterNode, "CpuUsageMhz", "CpuUsagePercent", "CpuCapacityMhz"),
                ReadHostCpuCapacity(targetNode) ?? clusterCpu / hostCount);
            ramRemaining = PostFailureRemainingPercent(
                clusterRam,
                ReadClusterUsage(clusterNode, "MemoryUsageMb", "MemoryUsagePercent", "MemoryCapacityMb"),
                ReadPositive(targetNode, "MemoryMb") ?? ReadPositive(targetNode, "MemoryCapacityMb") ?? clusterRam / hostCount);
        }
        else
        {
            cpuRemaining = CurrentRemainingPercent(clusterNode, "CpuUsagePercent", "CpuUsageMhz", "CpuCapacityMhz");
            ramRemaining = CurrentRemainingPercent(clusterNode, "MemoryUsagePercent", "MemoryUsageMb", "MemoryCapacityMb");
        }

        if (cpuRemaining == null || ramRemaining == null)
        {
            return new ClusterHeadroomDto
            {
                RemainingCpuPercent = 0,
                RemainingRamPercent = 0,
                HasCapacityForFailover = false,
                BottleneckCode = "Unknown",
                BottleneckResource = "Metrics not collected - collect vCenter metrics / collector data for a capacity verdict",
                ClusterName = clusterNode.Name,
                IsPostFailure = false,
            };
        }

        var cpu = Math.Round(cpuRemaining.Value, 1);
        var ram = Math.Round(ramRemaining.Value, 1);
        var hasCapacity = cpu >= AdmissionBufferPercent && ram >= AdmissionBufferPercent;
        var bottleneckCode = hasCapacity ? "None" : cpu < ram ? "Cpu" : "Ram";

        return new ClusterHeadroomDto
        {
            RemainingCpuPercent = cpu,
            RemainingRamPercent = ram,
            HasCapacityForFailover = hasCapacity,
            BottleneckCode = bottleneckCode,
            BottleneckResource = bottleneckCode switch
            {
                "Cpu" => "CPU headroom below the 10% failover admission buffer",
                "Ram" => "RAM headroom below the 10% failover admission buffer",
                _ => "None (headroom above the 10% admission buffer)",
            },
            ClusterName = clusterNode.Name,
            IsPostFailure = isPostFailure,
        };

        static ClusterHeadroomDto NotApplicable(string reason) => new()
        {
            RemainingCpuPercent = 0,
            RemainingRamPercent = 0,
            HasCapacityForFailover = false,
            BottleneckCode = "NotApplicable",
            BottleneckResource = reason,
            ClusterName = string.Empty,
            IsPostFailure = false,
        };
    }

    /// <summary>remaining = 100 - used / (cluster capacity - failed host capacity) x 100, clamped.</summary>
    private static double? PostFailureRemainingPercent(double? clusterCapacity, double? clusterUsage, double? hostCapacity)
    {
        if (clusterCapacity == null || clusterUsage == null || hostCapacity == null) return null;

        var surviving = clusterCapacity.Value - hostCapacity.Value;
        if (surviving <= 0) return 0; // the failed host was the cluster's only capacity

        return Math.Clamp(100.0 - (clusterUsage.Value / surviving * 100.0), 0, 100);
    }

    private static double? CurrentRemainingPercent(InfrastructureNodeDto node, string percentKey, string usageKey, string capacityKey)
    {
        var usagePercent = ReadPositive(node, percentKey);
        if (usagePercent == null &&
            ReadPositive(node, usageKey) is { } usage &&
            ReadPositive(node, capacityKey) is { } capacity)
        {
            usagePercent = usage / capacity * 100.0;
        }

        return usagePercent == null ? null : Math.Clamp(100.0 - usagePercent.Value, 0, 100);
    }

    /// <summary>Absolute cluster usage, or usage percent x capacity when only the percentage is stored.</summary>
    private static double? ReadClusterUsage(InfrastructureNodeDto cluster, string usageKey, string percentKey, string capacityKey)
    {
        if (ReadPositive(cluster, usageKey) is { } usage) return usage;

        return ReadPositive(cluster, percentKey) is { } percent && ReadPositive(cluster, capacityKey) is { } capacity
            ? percent / 100.0 * capacity
            : null;
    }

    private static double? ReadHostCpuCapacity(InfrastructureNodeDto host) =>
        ReadPositive(host, "CpuCores") is { } cores && ReadPositive(host, "CpuMhz") is { } mhz
            ? cores * mhz
            : ReadPositive(host, "CpuCapacityMhz");

    /// <summary>
    /// Deterministic risk score: 20 base + 7 per confirmed + 3 per probable +
    /// 1 per possible, +10 when a failover verdict applies to the scenario and
    /// is unfavourable or unknown, capped at 100.
    /// </summary>
    private static int CalculateRiskScore(ImpactAnalysisResultDto result, string scenario)
    {
        var headroom = result.ClusterHeadroom;
        var verdictApplies = headroom.BottleneckCode != "NotApplicable" || scenario == "host_failure";

        var score = 20
                    + (7 * result.ConfirmedImpacts.Count)
                    + (3 * result.ProbableImpacts.Count)
                    + result.PossibleImpacts.Count
                    + (verdictApplies && !headroom.HasCapacityForFailover ? 10 : 0);

        return Math.Min(100, score);
    }

    private static List<RecommendationItemDto> BuildRecommendations(
        InfrastructureNodeDto targetNode,
        InfrastructureNodeDto? clusterNode,
        string scenario,
        ImpactAnalysisResultDto result)
    {
        var items = new List<RecommendationItemDto>();

        if (result.ConfirmedImpacts.Count == 0)
        {
            items.Add(Item("no_direct_impact",
                $"No directly connected workloads found for {targetNode.Name}; no down-stream blast radius detected in the current graph.",
                ("target", targetNode.Name)));
            return items;
        }

        var headroom = result.ClusterHeadroom;

        if (headroom.BottleneckCode == "Unknown")
        {
            items.Add(Item("metrics_missing",
                "Capacity metrics are missing for the hosting cluster - run a vCenter discovery with metrics collection enabled before scheduling the change."));
        }
        else if (!headroom.HasCapacityForFailover)
        {
            items.Add(Item("failover_not_confirmed",
                "Failover capacity is not confirmed. Verify redundancy before scheduling the change."));
        }
        else
        {
            var cpu = headroom.RemainingCpuPercent.ToString("F1", CultureInfo.InvariantCulture);
            var ram = headroom.RemainingRamPercent.ToString("F1", CultureInfo.InvariantCulture);
            items.Add(Item("headroom_sufficient",
                $"Cluster headroom is sufficient for the modelled outage: CPU {cpu}%, RAM {ram}% remaining{(headroom.IsPostFailure ? " after the failed host's capacity is removed" : string.Empty)}.",
                ("cpu", cpu), ("ram", ram), ("cluster", headroom.ClusterName)));
        }

        // Reconciled nodes keep their most specific type (a VM that is also an
        // AD domain controller is a DomainController), so count every direct hit.
        // Hosts that merely mount a failed datastore keep running; only
        // workloads count as unavailable.
        var directCount = result.ConfirmedImpacts.Count(i => !InfrastructureNodeTypes.Contains(i.Type));
        if (directCount > 0)
        {
            var count = directCount.ToString(CultureInfo.InvariantCulture);
            items.Add(Item("workloads_unavailable",
                $"{count} workload(s) directly attached to {targetNode.Name} will be unavailable while the fault persists.",
                ("count", count), ("target", targetNode.Name)));
        }

        if (scenario == "host_failure")
        {
            // Only the cluster that owns the failed host decides whether its VMs restart.
            if (clusterNode != null &&
                clusterNode.Properties.TryGetValue("HaEnabled", out var ha) &&
                bool.TryParse(ha, out var haEnabled) && !haEnabled)
            {
                items.Add(Item("ha_disabled",
                    $"VMware HA is not enabled on {clusterNode.Name} - there will be no automatic VM restart.",
                    ("cluster", clusterNode.Name)));
            }

            var singleHostCluster = clusterNode != null &&
                                    ReadDouble(clusterNode, "HostCount") is { } hostCount && hostCount <= 1;
            if (directCount > 0 && (clusterNode == null || singleHostCluster))
            {
                items.Add(Item("single_point_of_failure",
                    $"{targetNode.Name} is a single point of failure: no other cluster host can take over its workloads.",
                    ("target", targetNode.Name)));
            }
        }

        var probableCount = result.ProbableImpacts.Count;
        if (probableCount > 0)
        {
            var names = string.Join(", ", result.ProbableImpacts.Take(3).Select(i => i.Name)) +
                        (probableCount > 3 ? ", …" : string.Empty);
            var count = probableCount.ToString(CultureInfo.InvariantCulture);
            items.Add(Item("dependents_degrade",
                $"{count} dependent service(s) may degrade ({names}); confirm their failure mode before the maintenance window.",
                ("count", count), ("names", names)));
        }

        return items;

        static RecommendationItemDto Item(string code, string text, params (string Key, string Value)[] values) => new()
        {
            Code = code,
            Text = text,
            Values = values.ToDictionary(v => v.Key, v => v.Value),
        };
    }

    /// <summary>
    /// Resolves the compute cluster that owns the target: a cluster is its own
    /// owner, a host belongs to its cluster (MemberOf / cluster Contains host),
    /// a workload follows its HostedOn host to that host's cluster (falling back
    /// to a direct cluster membership). Datacenter/Site containment is never
    /// walked: it leads to sibling clusters. Returns null when none is found.
    /// </summary>
    internal static Guid? FindOwningCluster(InfrastructureNodeDto targetNode, GraphQueryResultDto graph)
    {
        var nodesById = graph.Nodes.ToDictionary(n => n.Id);

        if (IsType(targetNode, nameof(NodeType.Cluster))) return targetNode.Id;

        if (MatchesAny(targetNode.NodeType, HostNodeTypes)) return DirectCluster(targetNode.Id);

        // Storage, network and organizational nodes have no single compute cluster.
        if (InfrastructureNodeTypes.Contains(targetNode.NodeType)) return null;

        var hosts = graph.Relationships
            .Where(r => r.SourceNodeId == targetNode.Id && MatchesAny(r.RelationshipType, new[] { "HostedOn" }))
            .Select(r => nodesById.GetValueOrDefault(r.TargetNodeId))
            .Where(n => n != null && MatchesAny(n.NodeType, HostNodeTypes))
            .OrderBy(n => n!.Name, StringComparer.Ordinal);

        foreach (var host in hosts)
        {
            if (DirectCluster(host!.Id) is { } hostCluster) return hostCluster;
        }

        return DirectCluster(targetNode.Id);

        Guid? DirectCluster(Guid memberId) => graph.Relationships
            .Select(r =>
                r.SourceNodeId == memberId && MatchesAny(r.RelationshipType, new[] { "MemberOf" }) ? r.TargetNodeId :
                r.TargetNodeId == memberId && MatchesAny(r.RelationshipType, new[] { "Contains" }) ? r.SourceNodeId :
                (Guid?)null)
            .Where(id => id != null)
            .Select(id => nodesById.GetValueOrDefault(id!.Value))
            .Where(n => n != null && IsType(n, nameof(NodeType.Cluster)))
            .OrderBy(n => n!.Name, StringComparer.Ordinal)
            .Select(n => (Guid?)n!.Id)
            .FirstOrDefault();
    }

    private static double? ReadDouble(InfrastructureNodeDto node, string key) =>
        node.Properties.TryGetValue(key, out var raw) &&
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value)
            ? value
            : null;

    // Adapters write "0.0" when a capacity or quickStats usage could not be
    // read (a running cluster never uses exactly 0), so zero counts as missing.
    private static double? ReadPositive(InfrastructureNodeDto node, string key) =>
        ReadDouble(node, key) is { } value && value > 0 ? value : null;

    private static bool IsType(InfrastructureNodeDto node, string nodeType) =>
        string.Equals(node.NodeType, nodeType, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesAny(string value, string[] candidates) =>
        candidates.Any(c => string.Equals(value, c, StringComparison.OrdinalIgnoreCase));

    private static void SortImpacts(List<ImpactedEntityDto> impacts) =>
        impacts.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
}
