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
/// topology graph served by IGraphService: a scenario run NEVER fabricates
/// demo nodes, fixed risk scores or hardcoded capacity numbers. When the data
/// required for a metric is absent (e.g. vCenter metrics were not collected),
/// the metric is reported as unknown rather than estimated.
/// </summary>
public class SimulationEngine : ISimulationEngine
{
    // Relationship types that connect a workload to the entity it depends on.
    private static readonly HashSet<string> DependencyEdgeTypes = new(StringComparer.OrdinalIgnoreCase)
    { "DependsOn", "RunsOn", "UsesDatabase", "ConnectedTo" };

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

    private readonly IGraphService _graphService;

    public SimulationEngine(IGraphService graphService)
    {
        _graphService = graphService;
    }

    public async Task<ImpactAnalysisResultDto> RunSimulationAsync(SimulationRunRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ScenarioType))
            throw new ArgumentException("Scenario type is required.");

        if (!IsKnownScenario(request.ScenarioType))
            throw new ArgumentException(
                $"Unknown scenario type '{request.ScenarioType}'. Supported: host_failure, datastore_failure, network_partition.");

        var graph = await _graphService.GetTopologyGraphAsync(request.TargetNodeId, depth: 3, cancellationToken);
        var targetNode = graph.Nodes.FirstOrDefault(n => n.Id == request.TargetNodeId)
            ?? throw new KeyNotFoundException(
                $"Target node {request.TargetNodeId} was not found in the inventory topology graph. Run a discovery first.");

        var result = new ImpactAnalysisResultDto
        {
            TargetNodeId = request.TargetNodeId,
            TargetNodeName = targetNode.Name,
            ScenarioType = NormalizeScenario(request.ScenarioType),
        };

        switch (NormalizeScenario(request.ScenarioType))
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

            default:
                throw new ArgumentException(
                    $"Unknown scenario type '{request.ScenarioType}'. Supported: host_failure, datastore_failure, network_partition.");
        }

        result.ClusterHeadroom = CalculateClusterHeadroom(targetNode, graph, NormalizeScenario(request.ScenarioType));
        result.RiskScore = CalculateRiskScore(result);
        result.Recommendations = BuildRecommendations(targetNode, graph, result);

        return result;
    }

    internal static string NormalizeScenario(string scenarioType) => scenarioType.Trim().ToLowerInvariant() switch
    {
        "host_failure" or "esxi_failure" => "host_failure",
        "datastore_failure" or "datastore_outage" or "storage_failure" => "datastore_failure",
        "network_partition" or "network_cut" => "network_partition",
        _ => scenarioType.Trim().ToLowerInvariant(),
    };

    internal static bool IsKnownScenario(string scenarioType)
    {
        var normalized = NormalizeScenario(scenarioType);
        return normalized is "host_failure" or "datastore_failure" or "network_partition";
    }

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

        // A workload target (VM, server, database...) is itself the failed
        // entity in every scenario: what breaks first is whatever depends on it.
        if (directHits.Count == 0 && !targetIsInfrastructure)
            directHits.AddRange(FindDependents(new HashSet<Guid> { targetNode.Id }, graph, nodesById));

        var directHitIds = directHits.Select(h => h.Id).ToList();
        var directHitSet = new HashSet<Guid>(directHitIds);
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
        var nodeTypeById = graph.Nodes.ToDictionary(n => n.Id, n => n.NodeType);
        var probableByEntity = new Dictionary<Guid, (string Path, string Name, string Type)>();
        var probableSet = new HashSet<Guid>();

        foreach (var (dependentId, path) in FindDependents(directHitSet, graph, nodesById))
        {
            if (directHitSet.Contains(dependentId) || dependentId == targetNode.Id) continue;
            if (!probableByEntity.ContainsKey(dependentId))
            {
                var node = nodesById.GetValueOrDefault(dependentId);
                probableByEntity[dependentId] = (path, node?.Name ?? dependentId.ToString(), node?.NodeType ?? nodeTypeById.GetValueOrDefault(dependentId, "Unknown"));
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
        probableSet.UnionWith(probableByEntity.Keys);

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
        var nameById = nodesById.ToDictionary(n => n.Key, n => n.Value.Name);
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
            var sourceName = nameById.GetValueOrDefault(rel.SourceNodeId, rel.SourceNodeId.ToString());
            var targetName = nameById.GetValueOrDefault(rel.TargetNodeId, rel.TargetNodeId.ToString());
            found.Add((dependentId, $"{sourceName} -> [{rel.RelationshipType}] -> {targetName}"));
        }

        return found;
    }

    /// <summary>
    /// Cluster headroom is computed from real vCenter metrics stored on the
    /// cluster/hypervisor nodes (CpuUsagePercent / MemoryUsagePercent).
    /// When metrics are absent the capacity verdict is explicitly "unknown"
    /// (HasCapacityForFailover = false, bottleneck = "Metrics not collected").
    /// </summary>
    private static ClusterHeadroomDto CalculateClusterHeadroom(
        InfrastructureNodeDto targetNode,
        GraphQueryResultDto graph,
        string scenario)
    {
        var nodesById = graph.Nodes.ToDictionary(n => n.Id);

        // Locate the owning cluster by walking the placement edges
        // (direction-agnostic BFS, max 2 hops): host -> MountedOn -> datastore,
        // cluster -> Contains -> host, workload -> MemberOf -> cluster etc.
        Guid? clusterId = FindOwningCluster(targetNode.Id, graph, nodesById);

        var clusterNode = clusterId != null ? nodesById.GetValueOrDefault(clusterId.Value) : null;

        double? cpuRemaining = null;
        double? ramRemaining = null;

        if (clusterNode != null)
        {
            cpuRemaining = ReadRemainingPercent(clusterNode, "CpuUsagePercent");
            ramRemaining = ReadRemainingPercent(clusterNode, "MemoryUsagePercent");
        }

        if (cpuRemaining == null || ramRemaining == null)
        {
            return new ClusterHeadroomDto
            {
                RemainingCpuPercent = 0,
                RemainingRamPercent = 0,
                HasCapacityForFailover = false,
                BottleneckResource = scenario == "datastore_failure"
                    ? "Metrics not collected - failover verdict unknown for storage; verify secondary datastore replication"
                    : "Metrics not collected - collect vCenter metrics / collector data for a capacity verdict",
            };
        }

        var hasCapacity = cpuRemaining.Value >= 10 && ramRemaining.Value >= 10;
        var bottleneck = scenario == "datastore_failure"
            ? "Storage I/O path loss - failover needs replication, not compute headroom"
            : !hasCapacity
                ? cpuRemaining.Value < ramRemaining.Value
                    ? "CPU headroom below the 10% failover admission buffer"
                    : "RAM headroom below the 10% failover admission buffer"
                : "None (headroom above the 10% admission buffer)";

        return new ClusterHeadroomDto
        {
            RemainingCpuPercent = Math.Round(cpuRemaining.Value, 1),
            RemainingRamPercent = Math.Round(ramRemaining.Value, 1),
            HasCapacityForFailover = hasCapacity && scenario != "datastore_failure",
            BottleneckResource = bottleneck,
        };
    }

    /// <summary>
    /// Deterministic risk score: 20 base + 7 per confirmed + 3 per probable +
    /// 1 per possible, +10 when the failover verdict is unfavourable or
    /// unknown, capped at 100.
    /// </summary>
    private static int CalculateRiskScore(ImpactAnalysisResultDto result)
    {
        var score = 20
                    + (7 * result.ConfirmedImpacts.Count)
                    + (3 * result.ProbableImpacts.Count)
                    + result.PossibleImpacts.Count
                    + (result.ClusterHeadroom.HasCapacityForFailover ? 0 : 10);

        return Math.Min(100, score);
    }

    private static List<string> BuildRecommendations(
        InfrastructureNodeDto targetNode,
        GraphQueryResultDto graph,
        ImpactAnalysisResultDto result)
    {
        var recommendations = new List<string>();
        var nodesById = graph.Nodes.ToDictionary(n => n.Id);

        if (result.ConfirmedImpacts.Count == 0)
        {
            recommendations.Add(
                $"No directly connected workloads found for {targetNode.Name}; no down-stream blast radius detected in the current graph.");
            return recommendations;
        }

        var headroom = result.ClusterHeadroom;

        if (headroom != null && !headroom.HasCapacityForFailover)
        {
            recommendations.Add(
                headroom.BottleneckResource.StartsWith("Metrics not collected", StringComparison.OrdinalIgnoreCase)
                    ? "Capacity metrics are missing for the hosting cluster - run a vCenter discovery with metrics collection enabled before scheduling the change."
                    : "Failover capacity is not confirmed. Verify redundancy or reschedule outside banking core hours (09:00-17:00).");
        }

        if (headroom != null && headroom.HasCapacityForFailover)
        {
            recommendations.Add(
                string.Create(CultureInfo.InvariantCulture,
                    $"Cluster headroom is sufficient for the modelled outage: CPU {headroom.RemainingCpuPercent:F1}%, RAM {headroom.RemainingRamPercent:F1}% remaining."));
        }

        // Reconciled nodes keep their most specific type (a VM that is also an
        // AD domain controller is a DomainController), so count every direct hit.
        var directCount = result.ConfirmedImpacts.Count;
        if (directCount > 0)
        {
            recommendations.Add(
                $"{directCount} workload(s) directly attached to {targetNode.Name} will be unavailable while the fault persists.");
        }

        var haEnabled = false;
        foreach (var rel in graph.Relationships)
        {
            if (!MatchesAny(rel.RelationshipType, new[] { "Contains", "MemberOf" })) continue;
            var cluster = rel.TargetNodeId == targetNode.Id ? nodesById.GetValueOrDefault(rel.SourceNodeId) : nodesById.GetValueOrDefault(rel.TargetNodeId);
            if (cluster == null || !cluster.NodeType.Equals("Cluster", StringComparison.OrdinalIgnoreCase)) continue;
            if (cluster.Properties.TryGetValue("HaEnabled", out var ha) && bool.TryParse(ha, out haEnabled) && haEnabled)
            {
                break;
            }
        }

        if (!haEnabled && targetNode.NodeType.Equals("Hypervisor", StringComparison.OrdinalIgnoreCase))
        {
            recommendations.Add(
                "VMware HA is not enabled on the hosting cluster - there will be no automatic VM restart.");
        }

        var probableCount = result.ProbableImpacts.Count;
        if (probableCount > 0)
        {
            var dependentNames = string.Join(", ", result.ProbableImpacts.Take(3).Select(i => i.Name));
            recommendations.Add(
                $"{probableCount} dependent service(s) may degrade: {dependentNames}{(probableCount > 3 ? "..." : string.Empty)}. Confirm their failure mode before the maintenance window.");
        }

        return recommendations;
    }

    private static Guid? FindOwningCluster(
        Guid targetNodeId,
        GraphQueryResultDto graph,
        Dictionary<Guid, InfrastructureNodeDto> nodesById)
    {
        var visited = new HashSet<Guid> { targetNodeId };
        var frontier = new List<Guid> { targetNodeId };

        for (var hop = 0; hop < 2 && frontier.Count > 0; hop++)
        {
            var next = new List<Guid>();

            foreach (var rel in graph.Relationships)
            {
                if (!MatchesAny(rel.RelationshipType, new[] { "Contains", "MemberOf", "HostedOn", "MountedOn" })) continue;

                Guid other;
                if (frontier.Contains(rel.SourceNodeId)) other = rel.TargetNodeId;
                else if (frontier.Contains(rel.TargetNodeId)) other = rel.SourceNodeId;
                else continue;

                if (!visited.Add(other)) continue;

                var otherNode = nodesById.GetValueOrDefault(other);
                if (otherNode != null &&
                    otherNode.NodeType.Equals("Cluster", StringComparison.OrdinalIgnoreCase))
                {
                    return other;
                }

                next.Add(other);
            }

            frontier = next;
        }

        return null;
    }

    private static double? ReadRemainingPercent(InfrastructureNodeDto node, string usageKey)
    {
        if (!node.Properties.TryGetValue(usageKey, out var raw) ||
            !double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var usagePercent))
            return null;

        var remaining = 100.0 - usagePercent;
        return Math.Clamp(remaining, 0, 100);
    }

    private static bool MatchesAny(string value, string[] candidates) =>
        candidates.Any(c => string.Equals(value, c, StringComparison.OrdinalIgnoreCase));

    private static void SortImpacts(List<ImpactedEntityDto> impacts) =>
        impacts.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
}
