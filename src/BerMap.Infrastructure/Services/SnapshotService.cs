using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Application.Services;
using BerMap.Domain.Entities;
using BerMap.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BerMap.Infrastructure.Services;

/// <summary>
/// Immutable point-in-time graph snapshots. Capture is transactional and
/// append-only; the DbContext rejects updates/deletes of snapshot rows.
/// </summary>
public sealed class SnapshotService : ISnapshotService
{
    private readonly BerMapDbContext _context;

    public SnapshotService(BerMapDbContext context)
    {
        _context = context;
    }

    public async Task<SnapshotSummaryDto> CreateSnapshotAsync(
        CreateSnapshotRequest request,
        string createdBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Snapshot name is required.");

        if (request.Name.Trim().Length > 200)
            throw new ArgumentException("Snapshot name must be 200 characters or fewer.");

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await CaptureSnapshotAsync(request, createdBy, cancellationToken);
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // A concurrent capture claimed the same SnapshotVersion. Reload
                // and retry with the next monotonic version.
                _context.ChangeTracker.Clear();
                await Task.Delay(50 * (attempt + 1), cancellationToken);
            }
        }
    }

    private async Task<SnapshotSummaryDto> CaptureSnapshotAsync(
        CreateSnapshotRequest request,
        string createdBy,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        if (_context.Database.IsRelational())
        {
            transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken);
        }

        try
        {
            var version = (await _context.Snapshots.MaxAsync(s => (long?)s.SnapshotVersion, cancellationToken) ?? 0) + 1;

            var nodes = await _context.InfrastructureNodes
                .AsNoTracking()
                .OrderBy(n => n.Id)
                .ToListAsync(cancellationToken);

            var relationships = await _context.InfrastructureRelationships
                .AsNoTracking()
                .OrderBy(r => r.Id)
                .ToListAsync(cancellationToken);

            var capturedAt = DateTime.UtcNow;
            var snapshot = new Snapshot
            {
                Id = Guid.NewGuid(),
                Name = request.Name.Trim(),
                Description = request.Description?.Trim() ?? string.Empty,
                CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? "unknown" : createdBy,
                NodeCount = nodes.Count,
                RelationshipCount = relationships.Count,
                IsImmutable = true,
                SnapshotVersion = version,
                SourceJobId = request.SourceJobId,
                GraphCapturedAt = capturedAt,
                CreatedAt = capturedAt,
                WarningsJson = JsonSerializer.Serialize(
                    nodes.Count == 0
                        ? new[] { "Snapshot captured an empty infrastructure graph." }
                        : Array.Empty<string>())
            };

            foreach (var node in nodes)
            {
                snapshot.Nodes.Add(new SnapshotNode
                {
                    Id = Guid.NewGuid(),
                    SnapshotId = snapshot.Id,
                    NodeId = node.Id,
                    NodeType = node.NodeType,
                    Name = node.Name,
                    FQDN = node.FQDN,
                    IPAddress = node.IPAddress,
                    MacAddress = node.MacAddress,
                    Status = node.Status,
                    DiscoverySource = node.DiscoverySource,
                    PropertiesJson = node.PropertiesJson,
                    BiosUuid = node.BiosUuid,
                    VmInstanceUuid = node.VmInstanceUuid,
                    DomainSid = node.DomainSid,
                    IsStale = node.IsStale,
                    FirstSeen = node.FirstSeen,
                    LastSeen = node.LastSeen,
                    GraphVersion = version,
                    CreatedAt = capturedAt
                });
            }

            foreach (var relationship in relationships)
            {
                snapshot.Relationships.Add(new SnapshotRelationship
                {
                    Id = Guid.NewGuid(),
                    SnapshotId = snapshot.Id,
                    RelationshipId = relationship.Id,
                    SourceNodeId = relationship.SourceNodeId,
                    TargetNodeId = relationship.TargetNodeId,
                    RelationshipType = relationship.RelationshipType,
                    ConfidenceLevel = relationship.ConfidenceLevel,
                    Evidence = relationship.Evidence,
                    SourceSystem = relationship.SourceSystem,
                    IsStale = relationship.IsStale,
                    PropertiesJson = relationship.PropertiesJson,
                    FirstSeen = relationship.FirstSeen,
                    LastSeen = relationship.LastSeen,
                    GraphVersion = version,
                    CreatedAt = capturedAt
                });
            }

            snapshot.Hash = SnapshotGraphHasher.Compute(snapshot.Nodes, snapshot.Relationships);

            _context.Snapshots.Add(snapshot);
            await _context.SaveChangesAsync(cancellationToken);

            if (transaction != null)
                await transaction.CommitAsync(cancellationToken);

            return MapSummary(snapshot);
        }
        finally
        {
            if (transaction != null)
                await transaction.DisposeAsync();
        }
    }

    public async Task<List<SnapshotSummaryDto>> ListSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = await _context.Snapshots
            .AsNoTracking()
            .OrderByDescending(s => s.SnapshotVersion)
            .ThenByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        return snapshots.Select(MapSummary).ToList();
    }

    public async Task<SnapshotSummaryDto?> GetSnapshotAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var snapshot = await _context.Snapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        return snapshot == null ? null : MapSummary(snapshot);
    }

    public async Task<SnapshotGraphDto?> GetSnapshotGraphAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var snapshot = await _context.Snapshots
            .AsNoTracking()
            .Include(s => s.Nodes)
            .Include(s => s.Relationships)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (snapshot == null)
            return null;

        var nodeNames = snapshot.Nodes.ToDictionary(n => n.NodeId, n => n.Name);
        return new SnapshotGraphDto
        {
            Snapshot = MapSummary(snapshot),
            Nodes = snapshot.Nodes.OrderBy(n => n.Name).Select(MapNode).ToList(),
            Relationships = snapshot.Relationships
                .OrderBy(r => r.RelationshipType)
                .Select(r => MapRelationship(r, nodeNames))
                .ToList()
        };
    }

    public async Task<SnapshotDiffDto> CompareSnapshotsAsync(
        Guid snapshotAId,
        Guid snapshotBId,
        CancellationToken cancellationToken = default)
    {
        // The messages reach the console as the problem title; ArgumentException
        // is created without a parameter name so no ".NET suffix" is appended.
        if (snapshotAId == Guid.Empty || snapshotBId == Guid.Empty)
            throw new ArgumentException("Select two snapshots to compare (query parameters a and b).");

        var a = await LoadGraphAsync(snapshotAId, cancellationToken)
                ?? throw new KeyNotFoundException($"Snapshot '{snapshotAId}' was not found.");
        var b = await LoadGraphAsync(snapshotBId, cancellationToken)
                ?? throw new KeyNotFoundException($"Snapshot '{snapshotBId}' was not found.");

        var aNodes = a.Nodes.ToDictionary(n => n.NodeId);
        var bNodes = b.Nodes.ToDictionary(n => n.NodeId);
        var aRelationships = a.Relationships.ToDictionary(r => r.RelationshipId);
        var bRelationships = b.Relationships.ToDictionary(r => r.RelationshipId);

        var addedNodes = bNodes.Values
            .Where(n => !aNodes.ContainsKey(n.NodeId))
            .OrderBy(n => n.Name)
            .ToList();

        var removedNodes = aNodes.Values
            .Where(n => !bNodes.ContainsKey(n.NodeId))
            .OrderBy(n => n.Name)
            .ToList();

        var changedNodes = new List<SnapshotNodeFieldChangeDto>();
        foreach (var (nodeId, currentNode) in bNodes)
        {
            if (!aNodes.TryGetValue(nodeId, out var previous))
                continue;

            var changes = CompareNodeFields(previous, currentNode);
            if (changes.Count == 0)
                continue;

            changedNodes.Add(new SnapshotNodeFieldChangeDto
            {
                NodeId = nodeId,
                Name = currentNode.Name,
                NodeType = currentNode.NodeType.ToString(),
                Changes = changes
            });
        }

        var addedRelationships = b.Relationships
            .Where(r => !aRelationships.ContainsKey(r.RelationshipId))
            .ToList();

        var removedRelationships = a.Relationships
            .Where(r => !bRelationships.ContainsKey(r.RelationshipId))
            .ToList();

        var changedRelationships = new List<SnapshotRelationshipChangeDto>();
        foreach (var (relationshipId, current) in bRelationships)
        {
            if (!aRelationships.TryGetValue(relationshipId, out var previous))
                continue;

            var changes = CompareRelationshipFields(previous, current);
            if (changes.Count == 0)
                continue;

            changedRelationships.Add(new SnapshotRelationshipChangeDto
            {
                RelationshipId = relationshipId,
                SourceNodeName = NodeName(b, current.SourceNodeId),
                TargetNodeName = NodeName(b, current.TargetNodeId),
                RelationshipType = current.RelationshipType.ToString(),
                Changes = changes
            });
        }

        var diff = new SnapshotDiffDto
        {
            SnapshotAId = a.Id,
            SnapshotAName = a.Name,
            SnapshotBId = b.Id,
            SnapshotBName = b.Name,
            AddedNodesCount = addedNodes.Count,
            RemovedNodesCount = removedNodes.Count,
            ChangedNodesCount = changedNodes.Count,
            AddedRelationshipsCount = addedRelationships.Count,
            RemovedRelationshipsCount = removedRelationships.Count,
            ChangedRelationshipsCount = changedRelationships.Count,
            AddedNodes = addedNodes.Select(MapNodeChange).ToList(),
            RemovedNodes = removedNodes.Select(MapNodeChange).ToList(),
            ChangedNodes = changedNodes,
            AddedRelationships = addedRelationships
                .Select(r => MapRelationshipChange(b, r))
                .ToList(),
            RemovedRelationships = removedRelationships
                .Select(r => MapRelationshipChange(a, r))
                .ToList(),
            ChangedRelationships = changedRelationships
        };

        BuildHighlights(diff);
        return diff;
    }

    public async Task<SnapshotVerificationDto?> VerifySnapshotAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadGraphAsync(id, cancellationToken);
        if (snapshot == null)
            return null;

        var computed = SnapshotGraphHasher.Compute(snapshot.Nodes, snapshot.Relationships);
        var differences = new List<string>();

        if (!string.Equals(snapshot.Hash, computed, StringComparison.OrdinalIgnoreCase))
            differences.Add("The stored SHA-256 graph hash does not match the captured rows.");

        if (snapshot.NodeCount != snapshot.Nodes.Count)
            differences.Add($"Stored node count {snapshot.NodeCount} does not match {snapshot.Nodes.Count} snapshot node rows.");

        if (snapshot.RelationshipCount != snapshot.Relationships.Count)
            differences.Add($"Stored relationship count {snapshot.RelationshipCount} does not match {snapshot.Relationships.Count} snapshot relationship rows.");

        return new SnapshotVerificationDto
        {
            SnapshotId = snapshot.Id,
            SnapshotName = snapshot.Name,
            IsValid = differences.Count == 0,
            StoredHash = snapshot.Hash,
            ComputedHash = computed,
            StoredNodeCount = snapshot.NodeCount,
            ActualNodeCount = snapshot.Nodes.Count,
            StoredRelationshipCount = snapshot.RelationshipCount,
            ActualRelationshipCount = snapshot.Relationships.Count,
            Differences = differences,
            VerifiedAt = DateTime.UtcNow
        };
    }

    private async Task<Snapshot?> LoadGraphAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _context.Snapshots
            .AsNoTracking()
            .Include(s => s.Nodes)
            .Include(s => s.Relationships)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    private static List<FieldChangeDto> CompareNodeFields(SnapshotNode a, SnapshotNode b)
    {
        var changes = new List<FieldChangeDto>();
        Add(changes, "Name", a.Name, b.Name);
        Add(changes, "FQDN", a.FQDN, b.FQDN);
        Add(changes, "IPAddress", a.IPAddress, b.IPAddress);
        Add(changes, "MacAddress", a.MacAddress, b.MacAddress);
        Add(changes, "NodeType", a.NodeType.ToString(), b.NodeType.ToString());
        Add(changes, "Status", a.Status, b.Status);
        Add(changes, "DiscoverySource", a.DiscoverySource, b.DiscoverySource);
        Add(changes, "BiosUuid", a.BiosUuid, b.BiosUuid);
        Add(changes, "VmInstanceUuid", a.VmInstanceUuid, b.VmInstanceUuid);
        Add(changes, "DomainSid", a.DomainSid, b.DomainSid);
        Add(changes, "IsStale", a.IsStale.ToString(), b.IsStale.ToString());
        Add(changes, "Properties",
            ComparableProperties(a.PropertiesJson),
            ComparableProperties(b.PropertiesJson));
        return changes;
    }

    /// <summary>
    /// Node properties as compared between snapshots. Keys starting with '_'
    /// are reconciliation bookkeeping (_lastReconciliationAt/Evidence/
    /// Confidence, _sourceSystems) that ingestion rewrites on every run; they
    /// would report every re-discovered node as changed. The stored hash still
    /// covers them.
    /// </summary>
    internal static string ComparableProperties(string? json)
    {
        if (SnapshotGraphHasher.ParseObject(json) is not JsonObject properties)
            return SnapshotGraphHasher.Canonicalize(json);

        foreach (var key in properties.Select(kv => kv.Key)
                     .Where(k => k.StartsWith('_'))
                     .ToList())
        {
            properties.Remove(key);
        }

        return SnapshotGraphHasher.Sort(properties)?.ToJsonString() ?? "{}";
    }

    private static List<FieldChangeDto> CompareRelationshipFields(SnapshotRelationship a, SnapshotRelationship b)
    {
        var changes = new List<FieldChangeDto>();
        Add(changes, "SourceNodeId", a.SourceNodeId.ToString(), b.SourceNodeId.ToString());
        Add(changes, "TargetNodeId", a.TargetNodeId.ToString(), b.TargetNodeId.ToString());
        Add(changes, "RelationshipType", a.RelationshipType.ToString(), b.RelationshipType.ToString());
        Add(changes, "ConfidenceLevel", a.ConfidenceLevel.ToString(), b.ConfidenceLevel.ToString());
        Add(changes, "Evidence", a.Evidence, b.Evidence);
        Add(changes, "SourceSystem", a.SourceSystem, b.SourceSystem);
        Add(changes, "IsStale", a.IsStale.ToString(), b.IsStale.ToString());
        Add(changes, "Properties",
            SnapshotGraphHasher.Canonicalize(a.PropertiesJson),
            SnapshotGraphHasher.Canonicalize(b.PropertiesJson));
        return changes;
    }

    private static void Add(List<FieldChangeDto> changes, string field, string? from, string? to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
            return;

        changes.Add(new FieldChangeDto { Field = field, From = from, To = to });
    }

    private static SnapshotNodeChangeDto MapNodeChange(SnapshotNode node) => new()
    {
        NodeId = node.NodeId,
        Name = node.Name,
        FQDN = node.FQDN,
        NodeType = node.NodeType.ToString(),
        IPAddress = node.IPAddress,
        Status = node.Status
    };

    private static SnapshotRelationshipChangeDto MapRelationshipChange(Snapshot snapshot, SnapshotRelationship relationship) => new()
    {
        RelationshipId = relationship.RelationshipId,
        SourceNodeName = NodeName(snapshot, relationship.SourceNodeId),
        TargetNodeName = NodeName(snapshot, relationship.TargetNodeId),
        RelationshipType = relationship.RelationshipType.ToString()
    };

    private static string NodeName(Snapshot snapshot, Guid nodeId)
    {
        return snapshot.Nodes.FirstOrDefault(n => n.NodeId == nodeId)?.Name ?? nodeId.ToString();
    }

    private static void BuildHighlights(SnapshotDiffDto diff)
    {
        if (diff.AddedNodesCount > 0)
            diff.Highlights.Add($"+{diff.AddedNodesCount} new node(s): {JoinNames(diff.AddedNodes.Select(n => n.Name))}");

        if (diff.RemovedNodesCount > 0)
            diff.Highlights.Add($"-{diff.RemovedNodesCount} removed node(s): {JoinNames(diff.RemovedNodes.Select(n => n.Name))}");

        if (diff.ChangedNodesCount > 0)
            diff.Highlights.Add($"{diff.ChangedNodesCount} changed node(s): {JoinNames(diff.ChangedNodes.Select(n => n.Name))}");

        if (diff.AddedRelationshipsCount > 0)
            diff.Highlights.Add($"+{diff.AddedRelationshipsCount} new relationship(s).");

        if (diff.RemovedRelationshipsCount > 0)
            diff.Highlights.Add($"-{diff.RemovedRelationshipsCount} removed relationship(s).");

        if (diff.ChangedRelationshipsCount > 0)
            diff.Highlights.Add($"{diff.ChangedRelationshipsCount} changed relationship(s).");

        if (diff.Highlights.Count == 0)
            diff.Highlights.Add("No graph changes detected between the selected snapshots.");
    }

    private static string JoinNames(IEnumerable<string> names)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).Take(5).ToList();
        return list.Count == 0 ? "(unnamed)" : string.Join(", ", list);
    }

    private static SnapshotSummaryDto MapSummary(Snapshot snapshot) => new()
    {
        Id = snapshot.Id,
        Name = snapshot.Name,
        Description = snapshot.Description,
        CreatedBy = snapshot.CreatedBy,
        NodeCount = snapshot.NodeCount,
        RelationshipCount = snapshot.RelationshipCount,
        IsImmutable = snapshot.IsImmutable,
        Hash = snapshot.Hash,
        SnapshotVersion = snapshot.SnapshotVersion,
        SourceJobId = snapshot.SourceJobId,
        GraphCapturedAt = snapshot.GraphCapturedAt,
        CreatedAt = snapshot.CreatedAt,
        Warnings = ParseStringList(snapshot.WarningsJson)
    };

    private static SnapshotNodeDto MapNode(SnapshotNode node) => new()
    {
        Id = node.Id,
        NodeId = node.NodeId,
        NodeType = node.NodeType.ToString(),
        Name = node.Name,
        FQDN = node.FQDN,
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
        Properties = ParseProperties(node.PropertiesJson)
    };

    private static SnapshotRelationshipDto MapRelationship(
        SnapshotRelationship relationship,
        IReadOnlyDictionary<Guid, string> nodeNames) => new()
    {
        Id = relationship.Id,
        RelationshipId = relationship.RelationshipId,
        SourceNodeId = relationship.SourceNodeId,
        SourceNodeName = nodeNames.GetValueOrDefault(relationship.SourceNodeId, relationship.SourceNodeId.ToString()),
        TargetNodeId = relationship.TargetNodeId,
        TargetNodeName = nodeNames.GetValueOrDefault(relationship.TargetNodeId, relationship.TargetNodeId.ToString()),
        RelationshipType = relationship.RelationshipType.ToString(),
        ConfidenceLevel = relationship.ConfidenceLevel.ToString(),
        Evidence = relationship.Evidence,
        SourceSystem = relationship.SourceSystem,
        IsStale = relationship.IsStale
    };

    private static Dictionary<string, JsonElement> ParseProperties(string? json)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var property in document.RootElement.EnumerateObject())
                result[property.Name] = property.Value.Clone();
        }
        catch (JsonException)
        {
            // Corrupt metadata is surfaced as an empty property bag rather than
            // failing the snapshot read.
        }

        return result;
    }

    private static List<string> ParseStringList(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }
}
