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
        depth = Math.Clamp(depth, 1, 5);

        var target = await _context.InfrastructureNodes
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == targetNodeId, cancellationToken);
        if (target == null)
            return result;

        var visited = new HashSet<Guid> { target.Id };
        var frontier = new HashSet<Guid> { target.Id };
        var relationshipIds = new HashSet<Guid>();
        var membershipAnchors = new HashSet<Guid> { target.Id };

        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var relationships = await ImpactEdges(_context.InfrastructureRelationships.AsNoTracking())
                .Where(r => frontier.Contains(r.SourceNodeId) || frontier.Contains(r.TargetNodeId))
                .OrderBy(r => r.Id)
                .Select(r => new { r.Id, r.SourceNodeId, r.TargetNodeId, r.RelationshipType })
                .Take(MaxImpactRelationships + 1)
                .ToListAsync(cancellationToken);
            if (relationships.Count > MaxImpactRelationships)
            {
                relationships.RemoveAt(relationships.Count - 1);
                result.IsTruncated = true;
            }

            var next = new HashSet<Guid>();
            foreach (var rel in relationships)
            {
                relationshipIds.Add(rel.Id);
                if (level == 0 && rel.SourceNodeId == target.Id && rel.RelationshipType == RelationshipType.HostedOn)
                    membershipAnchors.Add(rel.TargetNodeId);
                if (visited.Add(rel.SourceNodeId))
                    next.Add(rel.SourceNodeId);
                if (visited.Add(rel.TargetNodeId))
                    next.Add(rel.TargetNodeId);
            }

            if (visited.Count >= MaxImpactNodes)
                break;

            frontier = next;
        }

        var anchors = membershipAnchors.ToList();
        var membership = await _context.InfrastructureRelationships
            .AsNoTracking()
            .Where(r => !r.IsStale &&
                        ((r.RelationshipType == RelationshipType.MemberOf && anchors.Contains(r.SourceNodeId)) ||
                         (r.RelationshipType == RelationshipType.Contains && anchors.Contains(r.TargetNodeId))))
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.SourceNodeId, r.TargetNodeId })
            .Take(MaxImpactRelationships)
            .ToListAsync(cancellationToken);
        foreach (var rel in membership)
        {
            relationshipIds.Add(rel.Id);
            visited.Add(rel.SourceNodeId);
            visited.Add(rel.TargetNodeId);
        }

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

    /// <summary>Live edges an impact can travel along (the complement of the organisational ones).</summary>
    private static IQueryable<InfrastructureRelationship> ImpactEdges(IQueryable<InfrastructureRelationship> relationships) =>
        relationships.Where(r => !r.IsStale &&
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
}
