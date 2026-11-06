using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;

namespace BerMap.Infrastructure.Repositories;

/// <summary>
/// Reads the persisted infrastructure graph. An empty database returns an empty
/// graph; this repository never synthesizes demo topology.
/// </summary>
public class GraphRepository : IGraphService, IImpactGraphSource
{
    private const int MaxTopologyNodes = 500;
    private const int MaxTopologyRelationships = 2000;

    // The impact graph is evaluated on the server, not drawn, so it may be
    // larger; it stays small anyway because containment is not walked.
    private const int MaxImpactNodes = 5000;
    private const int MaxImpactRelationships = 20000;

    private readonly BerMapDbContext _context;

    public GraphRepository(BerMapDbContext context)
    {
        _context = context;
    }

    public async Task<GraphQueryResultDto> GetTopologyGraphAsync(
        Guid? centerNodeId = null,
        int depth = 2,
        CancellationToken cancellationToken = default)
    {
        var result = new GraphQueryResultDto();
        depth = Math.Clamp(depth, 1, 5);

        if (centerNodeId.HasValue)
        {
            var center = await _context.InfrastructureNodes
                .AsNoTracking()
                .FirstOrDefaultAsync(n => n.Id == centerNodeId.Value, cancellationToken);

            if (center == null)
                return result;

            var visited = new HashSet<Guid> { center.Id };
            var frontier = new HashSet<Guid> { center.Id };
            var relationshipIds = new HashSet<Guid>();

            for (var level = 0; level < depth && frontier.Count > 0; level++)
            {
                var relationships = await _context.InfrastructureRelationships
                    .AsNoTracking()
                    .Where(r => frontier.Contains(r.SourceNodeId) || frontier.Contains(r.TargetNodeId))
                    .OrderBy(r => r.Id)
                    .Take(MaxTopologyRelationships + 1)
                    .ToListAsync(cancellationToken);
                if (relationships.Count > MaxTopologyRelationships)
                {
                    relationships.RemoveAt(relationships.Count - 1);
                    result.IsTruncated = true;
                }

                var next = new HashSet<Guid>();
                foreach (var rel in relationships)
                {
                    relationshipIds.Add(rel.Id);
                    if (visited.Add(rel.SourceNodeId))
                        next.Add(rel.SourceNodeId);
                    if (visited.Add(rel.TargetNodeId))
                        next.Add(rel.TargetNodeId);
                }

                if (visited.Count >= MaxTopologyNodes)
                    break;

                frontier = next;
            }

            // The center node is always part of the result, even when the
            // neighbourhood is larger than the node cap.
            var nodes = await _context.InfrastructureNodes
                .AsNoTracking()
                .Where(n => visited.Contains(n.Id) && n.Id != center.Id)
                .OrderBy(n => n.NodeType)
                .ThenBy(n => n.Name)
                .Take(MaxTopologyNodes - 1)
                .ToListAsync(cancellationToken);
            nodes.Insert(0, center);
            result.IsTruncated |= visited.Count > nodes.Count;

            var nodeIds = nodes.Select(n => n.Id).ToHashSet();
            var relationshipsForNodes = await _context.InfrastructureRelationships
                .AsNoTracking()
                .Include(r => r.SourceNode)
                .Include(r => r.TargetNode)
                .Where(r => relationshipIds.Contains(r.Id) ||
                            (nodeIds.Contains(r.SourceNodeId) && nodeIds.Contains(r.TargetNodeId)))
                .OrderBy(r => r.Id)
                .Take(MaxTopologyRelationships + 1)
                .ToListAsync(cancellationToken);
            if (relationshipsForNodes.Count > MaxTopologyRelationships)
            {
                relationshipsForNodes.RemoveAt(relationshipsForNodes.Count - 1);
                result.IsTruncated = true;
            }

            result.Nodes = await MapNodesAsync(nodes, cancellationToken);
            result.Relationships = relationshipsForNodes
                .Where(r => nodeIds.Contains(r.SourceNodeId) && nodeIds.Contains(r.TargetNodeId))
                .Select(MapRelationshipDto)
                .ToList();

            return result;
        }

        var initialNodes = await _context.InfrastructureNodes
            .AsNoTracking()
            .OrderBy(n => n.NodeType)
            .ThenBy(n => n.Name)
            .Take(MaxTopologyNodes + 1)
            .ToListAsync(cancellationToken);

        if (initialNodes.Count > MaxTopologyNodes)
        {
            initialNodes.RemoveAt(initialNodes.Count - 1);
            result.IsTruncated = true;
        }

        if (initialNodes.Count == 0)
            return result;

        var initialIds = initialNodes.Select(n => n.Id).ToHashSet();

        var initialRelationships = await _context.InfrastructureRelationships
            .AsNoTracking()
            .Include(r => r.SourceNode)
            .Include(r => r.TargetNode)
            .Where(r => initialIds.Contains(r.SourceNodeId) && initialIds.Contains(r.TargetNodeId))
            .OrderBy(r => r.Id)
            .Take(MaxTopologyRelationships + 1)
            .ToListAsync(cancellationToken);
        if (initialRelationships.Count > MaxTopologyRelationships)
        {
            initialRelationships.RemoveAt(initialRelationships.Count - 1);
            result.IsTruncated = true;
        }

        result.Nodes = await MapNodesAsync(initialNodes, cancellationToken);
        result.Relationships = initialRelationships.Select(MapRelationshipDto).ToList();
        return result;
    }

    /// <summary>
    /// The simulation's neighbourhood of the target: a breadth-first walk over
    /// live (non-stale) edges that skips organisational ones (Contains,
    /// MemberOf, ManagedBy, Trusts, PossibleDuplicate). The only membership
    /// edges read are those that place the target, or a host it runs on, in a
    /// container (host -MemberOf-> cluster, cluster -Contains-> host); they are
    /// not expanded further, so a datacenter never pulls in its whole inventory.
    /// </summary>
    public async Task<GraphQueryResultDto> GetImpactGraphAsync(
        Guid targetNodeId,
        int depth,
        CancellationToken cancellationToken = default)
    {
        var result = new GraphQueryResultDto();

        var target = await _context.InfrastructureNodes
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == targetNodeId, cancellationToken);
        if (target == null)
            return result;

        var walk = await WalkImpactNeighbourhoodAsync(
            target.Id,
            depth,
            (frontier, ct) => ImpactEdges(_context.InfrastructureRelationships.AsNoTracking())
                .Where(r => frontier.Contains(r.SourceNodeId) || frontier.Contains(r.TargetNodeId))
                .OrderBy(r => r.Id)
                .Take(MaxImpactRelationships + 1)
                .Select(r => new ImpactEdge(r.Id, r.SourceNodeId, r.TargetNodeId, r.RelationshipType))
                .ToListAsync(ct),
            (anchors, ct) => _context.InfrastructureRelationships
                .AsNoTracking()
                .Where(r => !r.IsStale &&
                            ((r.RelationshipType == RelationshipType.MemberOf && anchors.Contains(r.SourceNodeId)) ||
                             (r.RelationshipType == RelationshipType.Contains && anchors.Contains(r.TargetNodeId))))
                .OrderBy(r => r.Id)
                .Take(MaxImpactRelationships)
                .Select(r => new ImpactEdge(r.Id, r.SourceNodeId, r.TargetNodeId, r.RelationshipType))
                .ToListAsync(ct),
            cancellationToken);
        result.IsTruncated = walk.IsTruncated;

        var visited = walk.Visited;
        var nodes = await _context.InfrastructureNodes
            .AsNoTracking()
            .Where(n => visited.Contains(n.Id) && n.Id != target.Id)
            .OrderBy(n => n.NodeType)
            .ThenBy(n => n.Name)
            .Take(MaxImpactNodes - 1)
            .ToListAsync(cancellationToken);
        nodes.Insert(0, target);
        result.IsTruncated |= visited.Count > nodes.Count;

        var nodeIds = nodes.Select(n => n.Id).ToHashSet();
        var relationshipIds = walk.RelationshipIds;
        var relationshipsForNodes = await _context.InfrastructureRelationships
            .AsNoTracking()
            .Include(r => r.SourceNode)
            .Include(r => r.TargetNode)
            .Where(r => relationshipIds.Contains(r.Id))
            .OrderBy(r => r.Id)
            .Take(MaxImpactRelationships + 1)
            .ToListAsync(cancellationToken);
        if (relationshipsForNodes.Count > MaxImpactRelationships)
        {
            relationshipsForNodes.RemoveAt(relationshipsForNodes.Count - 1);
            result.IsTruncated = true;
        }

        result.Nodes = await MapNodesAsync(nodes, cancellationToken);
        result.Relationships = relationshipsForNodes
            .Where(r => nodeIds.Contains(r.SourceNodeId) && nodeIds.Contains(r.TargetNodeId))
            .Select(MapRelationshipDto)
            .ToList();

        return result;
    }

    /// <summary>
    /// The impact neighbourhood of <paramref name="targetNodeId"/> as it was when
    /// the snapshot was captured. Same walk, edge rules and caps as
    /// <see cref="GetImpactGraphAsync"/>, read from SnapshotNodes and
    /// SnapshotRelationships; ids are the original graph ids, so the engine and
    /// the console see the same identities as on the live graph.
    /// </summary>
    public async Task<SnapshotImpactGraph?> GetSnapshotImpactGraphAsync(
        Guid snapshotId,
        Guid targetNodeId,
        int depth,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _context.Snapshots
            .AsNoTracking()
            .Where(s => s.Id == snapshotId)
            .Select(s => new { s.Id, s.Name, s.GraphCapturedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshot == null)
            return null;

        var graph = new SnapshotImpactGraph
        {
            SnapshotId = snapshot.Id,
            SnapshotName = snapshot.Name,
            SnapshotCreatedAt = snapshot.GraphCapturedAt,
        };
        var result = graph.Graph;

        var target = await _context.SnapshotNodes
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.SnapshotId == snapshotId && n.NodeId == targetNodeId, cancellationToken);
        if (target == null)
            return graph;

        var walk = await WalkImpactNeighbourhoodAsync(
            target.NodeId,
            depth,
            (frontier, ct) => SnapshotImpactEdges(_context.SnapshotRelationships.AsNoTracking(), snapshotId)
                .Where(r => frontier.Contains(r.SourceNodeId) || frontier.Contains(r.TargetNodeId))
                .OrderBy(r => r.RelationshipId)
                .Take(MaxImpactRelationships + 1)
                .Select(r => new ImpactEdge(r.RelationshipId, r.SourceNodeId, r.TargetNodeId, r.RelationshipType))
                .ToListAsync(ct),
            (anchors, ct) => _context.SnapshotRelationships
                .AsNoTracking()
                .Where(r => r.SnapshotId == snapshotId && !r.IsStale &&
                            ((r.RelationshipType == RelationshipType.MemberOf && anchors.Contains(r.SourceNodeId)) ||
                             (r.RelationshipType == RelationshipType.Contains && anchors.Contains(r.TargetNodeId))))
                .OrderBy(r => r.RelationshipId)
                .Take(MaxImpactRelationships)
                .Select(r => new ImpactEdge(r.RelationshipId, r.SourceNodeId, r.TargetNodeId, r.RelationshipType))
                .ToListAsync(ct),
            cancellationToken);
        result.IsTruncated = walk.IsTruncated;

        var visited = walk.Visited;
        var nodes = await _context.SnapshotNodes
            .AsNoTracking()
            .Where(n => n.SnapshotId == snapshotId && visited.Contains(n.NodeId) && n.NodeId != target.NodeId)
            .OrderBy(n => n.NodeType)
            .ThenBy(n => n.Name)
            .Take(MaxImpactNodes - 1)
            .ToListAsync(cancellationToken);
        nodes.Insert(0, target);
        result.IsTruncated |= visited.Count > nodes.Count;

        var nodesById = nodes.ToDictionary(n => n.NodeId);
        var relationshipIds = walk.RelationshipIds;
        var relationships = await _context.SnapshotRelationships
            .AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId && relationshipIds.Contains(r.RelationshipId))
            .OrderBy(r => r.RelationshipId)
            .Take(MaxImpactRelationships + 1)
            .ToListAsync(cancellationToken);
        if (relationships.Count > MaxImpactRelationships)
        {
            relationships.RemoveAt(relationships.Count - 1);
            result.IsTruncated = true;
        }

        result.Nodes = nodes.Select(MapSnapshotNodeDto).ToList();
        result.Relationships = relationships
            .Where(r => nodesById.ContainsKey(r.SourceNodeId) && nodesById.ContainsKey(r.TargetNodeId))
            .Select(r => MapSnapshotRelationshipDto(r, nodesById))
            .ToList();

        return graph;
    }

    /// <summary>One traversable edge of the impact walk, independent of where it is stored.</summary>
    private readonly record struct ImpactEdge(Guid Id, Guid SourceNodeId, Guid TargetNodeId, RelationshipType RelationshipType);

    private sealed class ImpactNeighbourhood
    {
        public HashSet<Guid> Visited { get; } = new();
        public HashSet<Guid> RelationshipIds { get; } = new();
        public bool IsTruncated { get; set; }
    }

    /// <summary>
    /// The breadth-first walk shared by the live and the snapshot impact graph.
    /// <paramref name="impactEdges"/> returns the live dependency/placement edges
    /// touching the frontier (at most the cap + 1, so truncation is detectable);
    /// <paramref name="membershipEdges"/> the non-stale MemberOf/Contains edges
    /// that place the given anchors in a container.
    /// </summary>
    private static async Task<ImpactNeighbourhood> WalkImpactNeighbourhoodAsync(
        Guid targetId,
        int depth,
        Func<HashSet<Guid>, CancellationToken, Task<List<ImpactEdge>>> impactEdges,
        Func<List<Guid>, CancellationToken, Task<List<ImpactEdge>>> membershipEdges,
        CancellationToken cancellationToken)
    {
        depth = Math.Clamp(depth, 1, 5);
        var walk = new ImpactNeighbourhood();
        walk.Visited.Add(targetId);
        var frontier = new HashSet<Guid> { targetId };
        var membershipAnchors = new HashSet<Guid> { targetId };

        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var relationships = await impactEdges(frontier, cancellationToken);
            if (relationships.Count > MaxImpactRelationships)
            {
                relationships.RemoveAt(relationships.Count - 1);
                walk.IsTruncated = true;
            }

            var next = new HashSet<Guid>();
            foreach (var rel in relationships)
            {
                walk.RelationshipIds.Add(rel.Id);
                if (level == 0 && rel.SourceNodeId == targetId && rel.RelationshipType == RelationshipType.HostedOn)
                    membershipAnchors.Add(rel.TargetNodeId);
                if (walk.Visited.Add(rel.SourceNodeId))
                    next.Add(rel.SourceNodeId);
                if (walk.Visited.Add(rel.TargetNodeId))
                    next.Add(rel.TargetNodeId);
            }

            if (walk.Visited.Count >= MaxImpactNodes)
                break;

            frontier = next;
        }

        var membership = await membershipEdges(membershipAnchors.ToList(), cancellationToken);
        foreach (var rel in membership)
        {
            walk.RelationshipIds.Add(rel.Id);
            walk.Visited.Add(rel.SourceNodeId);
            walk.Visited.Add(rel.TargetNodeId);
        }

        return walk;
    }

    /// <summary>Live edges an impact can travel along (the complement of the organisational ones).</summary>
    private static IQueryable<InfrastructureRelationship> ImpactEdges(IQueryable<InfrastructureRelationship> relationships) =>
        relationships.Where(r => !r.IsStale &&
                                 r.RelationshipType != RelationshipType.Contains &&
                                 r.RelationshipType != RelationshipType.MemberOf &&
                                 r.RelationshipType != RelationshipType.ManagedBy &&
                                 r.RelationshipType != RelationshipType.Trusts &&
                                 r.RelationshipType != RelationshipType.PossibleDuplicate);

    /// <summary>The same edge rule over one snapshot's frozen relationships.</summary>
    private static IQueryable<SnapshotRelationship> SnapshotImpactEdges(IQueryable<SnapshotRelationship> relationships, Guid snapshotId) =>
        relationships.Where(r => r.SnapshotId == snapshotId &&
                                 !r.IsStale &&
                                 r.RelationshipType != RelationshipType.Contains &&
                                 r.RelationshipType != RelationshipType.MemberOf &&
                                 r.RelationshipType != RelationshipType.ManagedBy &&
                                 r.RelationshipType != RelationshipType.Trusts &&
                                 r.RelationshipType != RelationshipType.PossibleDuplicate);

    public async Task<List<InfrastructureNodeDto>> SearchNodesAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<InfrastructureNodeDto>();

        var normalized = query.Trim().ToLowerInvariant();

        var nodes = await _context.InfrastructureNodes
            .AsNoTracking()
            .Where(n => n.Name.ToLower().Contains(normalized) ||
                        n.FQDN.ToLower().Contains(normalized) ||
                        (n.IPAddress != null && n.IPAddress.ToLower().Contains(normalized)) ||
                        (n.MacAddress != null && n.MacAddress.ToLower().Contains(normalized)) ||
                        n.BiosUuid.ToLower().Contains(normalized) ||
                        n.DomainSid.ToLower().Contains(normalized))
            .OrderBy(n => n.Name)
            .Take(25)
            .ToListAsync(cancellationToken);

        return await MapNodesAsync(nodes, cancellationToken);
    }

    public async Task<InfrastructureNodeDto?> GetNodeDetailsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var node = await _context.InfrastructureNodes
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

        if (node == null)
            return null;

        var mapped = await MapNodesAsync(new List<InfrastructureNode> { node }, cancellationToken);
        return mapped.FirstOrDefault();
    }

    private async Task<List<InfrastructureNodeDto>> MapNodesAsync(
        IReadOnlyCollection<InfrastructureNode> nodes,
        CancellationToken cancellationToken)
    {
        if (nodes.Count == 0)
            return new List<InfrastructureNodeDto>();

        var nodeIds = nodes.Select(n => n.Id).ToList();
        var links = await _context.NodeSourceLinks
            .AsNoTracking()
            .Where(l => nodeIds.Contains(l.NodeId))
            .Select(l => new { l.NodeId, l.SourceSystem, l.IsStale })
            .ToListAsync(cancellationToken);

        var sourcesByNode = links
            .GroupBy(l => l.NodeId)
            .ToDictionary(
                g => g.Key,
                g => g.Where(l => !l.IsStale)
                    .Select(l => l.SourceSystem)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s)
                    .ToList());

        return nodes.Select(node =>
        {
            var properties = ParseProperties(node.PropertiesJson);

            if (!sourcesByNode.TryGetValue(node.Id, out var sources))
                sources = new List<string>();

            return new InfrastructureNodeDto
            {
                Id = node.Id,
                Name = node.Name,
                FQDN = node.FQDN,
                NodeType = node.NodeType.ToString(),
                IPAddress = node.IPAddress,
                MacAddress = node.MacAddress,
                Status = node.Status,
                DiscoverySource = node.DiscoverySource,
                BiosUuid = node.BiosUuid,
                VmInstanceUuid = node.VmInstanceUuid,
                DomainSid = node.DomainSid,
                IsStale = node.IsStale,
                StaleSince = node.StaleSince,
                FirstSeen = node.FirstSeen,
                LastSeen = node.LastSeen,
                SourceSystems = sources,
                Properties = properties
            };
        }).ToList();
    }

    /// <summary>
    /// Node properties mix scalars written by adapters with arrays written by
    /// reconciliation (e.g. <c>_sourceSystems</c>), so they cannot be read as a
    /// string dictionary. Scalars keep their value; arrays/objects keep raw JSON.
    /// </summary>
    internal static Dictionary<string, string> ParseProperties(string? json)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return properties;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return properties;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                properties[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                    _ => property.Value.GetRawText()
                };
            }
        }
        catch (JsonException)
        {
            // Corrupt metadata must not hide the node itself.
        }

        return properties;
    }

    private static InfrastructureRelationshipDto MapRelationshipDto(InfrastructureRelationship rel)
    {
        return new InfrastructureRelationshipDto
        {
            Id = rel.Id,
            SourceNodeId = rel.SourceNodeId,
            SourceNodeName = rel.SourceNode?.Name ?? rel.SourceNodeId.ToString(),
            TargetNodeId = rel.TargetNodeId,
            TargetNodeName = rel.TargetNode?.Name ?? rel.TargetNodeId.ToString(),
            RelationshipType = rel.RelationshipType.ToString(),
            ConfidenceLevel = rel.ConfidenceLevel.ToString(),
            Evidence = rel.Evidence,
            SourceSystem = rel.SourceSystem,
            IsStale = rel.IsStale
        };
    }

    /// <summary>
    /// A frozen node, mapped like a live one so the engine reads it the same way:
    /// properties go through <see cref="ParseProperties"/>. Snapshots do not keep
    /// per-source links, so the source list falls back to the discovery source.
    /// </summary>
    private static InfrastructureNodeDto MapSnapshotNodeDto(SnapshotNode node) => new()
    {
        Id = node.NodeId,
        Name = node.Name,
        FQDN = node.FQDN,
        NodeType = node.NodeType.ToString(),
        IPAddress = node.IPAddress,
        MacAddress = node.MacAddress,
        Status = node.Status,
        DiscoverySource = node.DiscoverySource,
        BiosUuid = node.BiosUuid,
        VmInstanceUuid = node.VmInstanceUuid,
        DomainSid = node.DomainSid,
        IsStale = node.IsStale,
        FirstSeen = node.FirstSeen,
        LastSeen = node.LastSeen,
        SourceSystems = string.IsNullOrWhiteSpace(node.DiscoverySource)
            ? new List<string>()
            : new List<string> { node.DiscoverySource },
        Properties = ParseProperties(node.PropertiesJson)
    };

    private static InfrastructureRelationshipDto MapSnapshotRelationshipDto(
        SnapshotRelationship rel,
        IReadOnlyDictionary<Guid, SnapshotNode> nodesById) => new()
    {
        Id = rel.RelationshipId,
        SourceNodeId = rel.SourceNodeId,
        SourceNodeName = nodesById.TryGetValue(rel.SourceNodeId, out var source) ? source.Name : rel.SourceNodeId.ToString(),
        TargetNodeId = rel.TargetNodeId,
        TargetNodeName = nodesById.TryGetValue(rel.TargetNodeId, out var target) ? target.Name : rel.TargetNodeId.ToString(),
        RelationshipType = rel.RelationshipType.ToString(),
        ConfidenceLevel = rel.ConfidenceLevel.ToString(),
        Evidence = rel.Evidence,
        SourceSystem = rel.SourceSystem,
        IsStale = rel.IsStale
    };
}
