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
using BerMap.Infrastructure.Data;

namespace BerMap.Infrastructure.Repositories;

/// <summary>
/// Reads the persisted infrastructure graph. An empty database returns an empty
/// graph; this repository never synthesizes demo topology.
/// </summary>
public class GraphRepository : IGraphService
{
    private const int MaxTopologyNodes = 500;
    private const int MaxTopologyRelationships = 2000;

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
                    .Take(MaxTopologyRelationships)
                    .ToListAsync(cancellationToken);

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
            result.IsTruncated = visited.Count > nodes.Count;

            var nodeIds = nodes.Select(n => n.Id).ToHashSet();
            var relationshipsForNodes = await _context.InfrastructureRelationships
                .AsNoTracking()
                .Include(r => r.SourceNode)
                .Include(r => r.TargetNode)
                .Where(r => relationshipIds.Contains(r.Id) ||
                            (nodeIds.Contains(r.SourceNodeId) && nodeIds.Contains(r.TargetNodeId)))
                .OrderBy(r => r.Id)
                .Take(MaxTopologyRelationships)
                .ToListAsync(cancellationToken);

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
            .Take(MaxTopologyRelationships)
            .ToListAsync(cancellationToken);

        result.Nodes = await MapNodesAsync(initialNodes, cancellationToken);
        result.Relationships = initialRelationships.Select(MapRelationshipDto).ToList();
        return result;
    }

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
