using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BerMap.Infrastructure.Services;

public sealed class InventoryService : IInventoryService
{
    private readonly BerMapDbContext _context;
    private readonly IAuditService _auditService;
    private readonly ILogger<InventoryService> _logger;

    public InventoryService(
        BerMapDbContext context,
        IAuditService auditService,
        ILogger<InventoryService> logger)
    {
        _context = context;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<InventoryPageDto> ListNodesAsync(
        InventoryQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);

        var pendingReviewNodeIds = await GetPendingReviewNodeIdsAsync(cancellationToken);

        var query = _context.InfrastructureNodes.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var value = request.Query.Trim().ToLowerInvariant();
            query = query.Where(n =>
                n.Name.ToLower().Contains(value) ||
                n.FQDN.ToLower().Contains(value) ||
                (n.IPAddress != null && n.IPAddress.ToLower().Contains(value)) ||
                (n.MacAddress != null && n.MacAddress.ToLower().Contains(value)) ||
                n.BiosUuid.ToLower().Contains(value) ||
                n.DomainSid.ToLower().Contains(value));
        }

        // An unknown filter value is an error, not "no filter": silently
        // ignoring it returned the whole inventory as if it had matched.
        if (!string.IsNullOrWhiteSpace(request.NodeType))
        {
            var nodeType = ParseNodeTypeFilter(request.NodeType);
            query = query.Where(n => n.NodeType == nodeType);
        }

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            var status = ParseStatusFilter(request.Status);
            query = query.Where(n => n.Status == status);
        }

        if (request.IsStale.HasValue)
            query = query.Where(n => n.IsStale == request.IsStale.Value);

        if (request.NeedsReview == true)
        {
            query = query.Where(n =>
                pendingReviewNodeIds.Contains(n.Id) ||
                _context.ReconciliationReviews.Any(r =>
                    r.Status == ReconciliationReviewStatus.Pending &&
                    (r.CreatedNodeId == n.Id || r.CandidateNodeId == n.Id)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var nodes = await query
            .OrderBy(n => n.Name.ToLower())
            .ThenBy(n => n.Name)
            .ThenBy(n => n.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var nodeIds = nodes.Select(n => n.Id).ToList();
        var links = nodeIds.Count == 0
            ? new List<NodeSourceLink>()
            : await _context.NodeSourceLinks
                .AsNoTracking()
                .Where(l => nodeIds.Contains(l.NodeId))
                .ToListAsync(cancellationToken);

        var linksByNode = links.GroupBy(l => l.NodeId).ToDictionary(g => g.Key, g => g.ToList());

        var reconciledCount = await _context.InfrastructureNodes
            .AsNoTracking()
            .CountAsync(n => n.SourceLinks.Count(l => !l.IsStale) > 1, cancellationToken);

        var singleSourceCount = await _context.InfrastructureNodes
            .AsNoTracking()
            .CountAsync(n => n.SourceLinks.Count(l => !l.IsStale) == 1, cancellationToken);

        var pendingReviewCount = await _context.ReconciliationReviews
            .AsNoTracking()
            .CountAsync(r => r.Status == ReconciliationReviewStatus.Pending, cancellationToken);

        return new InventoryPageDto
        {
            Items = nodes.Select(n => MapNode(n, linksByNode.GetValueOrDefault(n.Id) ?? new List<NodeSourceLink>(),
                pendingReviewNodeIds.Contains(n.Id))).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize),
            ReconciledCount = reconciledCount,
            SingleSourceCount = singleSourceCount,
            PendingReviewCount = pendingReviewCount
        };
    }

    public async Task<InventoryNodeDetailDto?> GetNodeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var node = await _context.InfrastructureNodes
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

        if (node == null)
            return null;

        var links = await _context.NodeSourceLinks
            .AsNoTracking()
            .Include(l => l.Integration)
            .Where(l => l.NodeId == id)
            .ToListAsync(cancellationToken);

        var relationships = await _context.InfrastructureRelationships
            .AsNoTracking()
            .Include(r => r.SourceNode)
            .Include(r => r.TargetNode)
            .Where(r => r.SourceNodeId == id || r.TargetNodeId == id)
            .OrderBy(r => r.RelationshipType)
            .ToListAsync(cancellationToken);

        var reviews = await _context.ReconciliationReviews
            .AsNoTracking()
            .Where(r => r.CreatedNodeId == id || r.CandidateNodeId == id)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        var reviewNodeIds = reviews
            .SelectMany(r => new[] { r.CreatedNodeId, r.CandidateNodeId })
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();

        var reviewNodeNames = await _context.InfrastructureNodes
            .AsNoTracking()
            .Where(n => reviewNodeIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, n => n.Name, cancellationToken);

        var integrationNames = await _context.Integrations
            .AsNoTracking()
            .Where(i => links.Select(l => l.IntegrationId).Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Name, cancellationToken);

        var pendingReviewIds = await GetPendingReviewNodeIdsAsync(cancellationToken);

        return new InventoryNodeDetailDto
        {
            Node = MapNode(node, links, pendingReviewIds.Contains(node.Id)),
            SourceLinks = links.Select(l => new NodeSourceLinkDto
            {
                Id = l.Id,
                IntegrationId = l.IntegrationId,
                IntegrationName = integrationNames.GetValueOrDefault(l.IntegrationId, string.Empty),
                ExternalId = l.ExternalId,
                SourceSystem = l.SourceSystem,
                NodeType = l.NodeType.ToString(),
                IsStale = l.IsStale,
                FirstSeenAt = l.FirstSeenAt,
                LastSeenAt = l.LastSeenAt
            }).ToList(),
            Relationships = relationships.Select(r => new InventoryRelationshipDto
            {
                Id = r.Id,
                SourceNodeId = r.SourceNodeId,
                SourceNodeName = r.SourceNode?.Name ?? r.SourceNodeId.ToString(),
                TargetNodeId = r.TargetNodeId,
                TargetNodeName = r.TargetNode?.Name ?? r.TargetNodeId.ToString(),
                RelationshipType = r.RelationshipType.ToString(),
                ConfidenceLevel = r.ConfidenceLevel.ToString(),
                Evidence = r.Evidence,
                SourceSystem = r.SourceSystem,
                IsStale = r.IsStale
            }).ToList(),
            Reviews = reviews.Select(r => MapReview(r, reviewNodeNames, string.Empty)).ToList()
        };
    }

    public async Task<List<ReconciliationReviewDto>> ListReviewsAsync(
        string? status,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var reviewStatus = Enum.TryParse<ReconciliationReviewStatus>(status, true, out var parsed)
            ? parsed
            : ReconciliationReviewStatus.Pending;

        var reviews = await _context.ReconciliationReviews
            .AsNoTracking()
            .Where(r => r.Status == reviewStatus)
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

        var nodeIds = reviews
            .SelectMany(r => new[] { r.CreatedNodeId, r.CandidateNodeId })
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();

        var nodeNames = await _context.InfrastructureNodes
            .AsNoTracking()
            .Where(n => nodeIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, n => n.Name, cancellationToken);

        var integrationIds = reviews
            .Where(r => r.IntegrationId.HasValue)
            .Select(r => r.IntegrationId!.Value)
            .Distinct()
            .ToList();

        var integrationNames = await _context.Integrations
            .AsNoTracking()
            .Where(i => integrationIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Name, cancellationToken);

        return reviews.Select(r => MapReview(
            r,
            nodeNames,
            r.IntegrationId.HasValue ? integrationNames.GetValueOrDefault(r.IntegrationId.Value, string.Empty) : string.Empty))
            .ToList();
    }

    /// <summary>Valid values of the inventory status filter (the normalised node statuses).</summary>
    public static readonly IReadOnlyList<string> StatusFilterValues = new[] { "healthy", "warning", "critical", "unknown" };

    internal static NodeType ParseNodeTypeFilter(string value)
    {
        var trimmed = value.Trim();
        // Enum.TryParse also accepts numbers ("42"), which are not node types.
        if (!trimmed.All(char.IsDigit) &&
            Enum.TryParse<NodeType>(trimmed, ignoreCase: true, out var nodeType) &&
            Enum.IsDefined(nodeType))
        {
            return nodeType;
        }

        throw new IntegrationValidationException(
            "invalid_filter",
            $"Unknown node type '{trimmed}'. Valid values: {string.Join(", ", Enum.GetNames<NodeType>())}.");
    }

    internal static string ParseStatusFilter(string value)
    {
        var status = value.Trim().ToLowerInvariant();
        if (StatusFilterValues.Contains(status))
            return status;

        throw new IntegrationValidationException(
            "invalid_filter",
            $"Unknown status '{value.Trim()}'. Valid values: {string.Join(", ", StatusFilterValues)}.");
    }

    private async Task<int> RemovePossibleDuplicateEdgesAsync(ReconciliationReview review, CancellationToken cancellationToken)
    {
        if (!review.CreatedNodeId.HasValue || !review.CandidateNodeId.HasValue)
            return 0;

        var a = review.CreatedNodeId.Value;
        var b = review.CandidateNodeId.Value;
        var edges = await _context.InfrastructureRelationships
            .Where(r => r.RelationshipType == RelationshipType.PossibleDuplicate &&
                        ((r.SourceNodeId == a && r.TargetNodeId == b) ||
                         (r.SourceNodeId == b && r.TargetNodeId == a)))
            .ToListAsync(cancellationToken);

        _context.InfrastructureRelationships.RemoveRange(edges);
        return edges.Count;
    }

    public async Task<ReconciliationReviewDto?> ResolveReviewAsync(
        Guid id,
        ResolveReconciliationReviewRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var review = await _context.ReconciliationReviews
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (review == null)
            return null;

        if (review.Status != ReconciliationReviewStatus.Pending)
            throw new IntegrationValidationException("REVIEW_NOT_PENDING", "Only pending reviews can be resolved.");

        var resolution = request.Resolution?.Trim();
        if (!string.Equals(resolution, "Merged", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(resolution, "Dismissed", StringComparison.OrdinalIgnoreCase))
        {
            throw new IntegrationValidationException("REVIEW_RESOLUTION", "Resolution must be 'Merged' or 'Dismissed'.");
        }

        if (string.Equals(resolution, "Dismissed", StringComparison.OrdinalIgnoreCase))
        {
            review.Status = ReconciliationReviewStatus.Dismissed;
            review.ReviewedAt = DateTime.UtcNow;
            review.ReviewedBy = actor;
            review.ResolutionNotes = request.Notes?.Trim() ?? string.Empty;
            review.UpdatedAt = DateTime.UtcNow;

            // The nodes are confirmed distinct, so the inferred duplicate edge
            // between them is wrong and must leave the inventory and the map.
            var duplicateEdges = await RemovePossibleDuplicateEdgesAsync(review, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            await _auditService.LogAsync(
                actor, actor, "ReconciliationReviewDismissed", nameof(ReconciliationReview), review.Id.ToString(),
                JsonSerializer.Serialize(new { review.IntegrationId, review.MatchKey, review.Reason, duplicateEdgesRemoved = duplicateEdges }),
                string.Empty, true, cancellationToken);

            var names = await GetReviewNodeNamesAsync(review, cancellationToken);
            var integrationName = await GetIntegrationNameAsync(review.IntegrationId, cancellationToken);
            return MapReview(review, names, integrationName);
        }

        if (!review.CreatedNodeId.HasValue)
            throw new IntegrationValidationException("REVIEW_MERGE_TARGET", "The review has no discovered node to merge.");

        var targetNodeId = request.TargetNodeId ?? review.CandidateNodeId;
        if (!targetNodeId.HasValue)
            throw new IntegrationValidationException("REVIEW_MERGE_TARGET", "A candidate target node is required for merge.");

        var created = await _context.InfrastructureNodes
            .FirstOrDefaultAsync(n => n.Id == review.CreatedNodeId.Value, cancellationToken);

        var target = await _context.InfrastructureNodes
            .FirstOrDefaultAsync(n => n.Id == targetNodeId.Value, cancellationToken);

        if (created == null || target == null)
            throw new IntegrationValidationException("REVIEW_MERGE_TARGET", "The discovered or target node no longer exists.");

        if (created.Id == target.Id)
            throw new IntegrationValidationException("REVIEW_MERGE_TARGET", "Cannot merge a node into itself.");

        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        if (_context.Database.IsRelational())
            transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            MergeNodes(created, target);
            await MoveSourceLinksAsync(created.Id, target.Id, cancellationToken);
            await MoveRelationshipsAsync(created.Id, target.Id, cancellationToken);

            var otherReviews = await _context.ReconciliationReviews
                .Where(r => r.Id != review.Id &&
                            r.Status == ReconciliationReviewStatus.Pending &&
                            r.CreatedNodeId == created.Id)
                .ToListAsync(cancellationToken);

            foreach (var other in otherReviews)
            {
                other.CreatedNodeId = target.Id;
                other.UpdatedAt = DateTime.UtcNow;
            }

            _context.InfrastructureNodes.Remove(created);

            review.Status = ReconciliationReviewStatus.Merged;
            review.CreatedNodeId = target.Id;
            review.CandidateNodeId = target.Id;
            review.ReviewedAt = DateTime.UtcNow;
            review.ReviewedBy = actor;
            review.ResolutionNotes = request.Notes?.Trim() ?? string.Empty;
            review.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
            if (transaction != null)
                await transaction.CommitAsync(cancellationToken);

            await _auditService.LogAsync(
                actor, actor, "ReconciliationReviewMerged", nameof(ReconciliationReview), review.Id.ToString(),
                JsonSerializer.Serialize(new
                {
                    review.IntegrationId,
                    review.MatchKey,
                    mergedNodeId = created.Id,
                    targetNodeId = target.Id
                }),
                string.Empty, true, cancellationToken);

            _logger.LogInformation(
                "Reconciliation review {ReviewId} merged node {CreatedNodeId} into {TargetNodeId}",
                review.Id, created.Id, target.Id);

            var names = await GetReviewNodeNamesAsync(review, cancellationToken);
            var integrationName = await GetIntegrationNameAsync(review.IntegrationId, cancellationToken);
            return MapReview(review, names, integrationName);
        }
        catch
        {
            if (transaction != null)
                await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (transaction != null)
                await transaction.DisposeAsync();
        }
    }

    private void MergeNodes(InfrastructureNode created, InfrastructureNode target)
    {
        if (string.IsNullOrWhiteSpace(target.FQDN) && !string.IsNullOrWhiteSpace(created.FQDN))
            target.FQDN = created.FQDN;
        if (string.IsNullOrWhiteSpace(target.IPAddress) && !string.IsNullOrWhiteSpace(created.IPAddress))
            target.IPAddress = created.IPAddress;
        if (string.IsNullOrWhiteSpace(target.MacAddress) && !string.IsNullOrWhiteSpace(created.MacAddress))
            target.MacAddress = created.MacAddress;
        if (string.IsNullOrWhiteSpace(target.BiosUuid) && !string.IsNullOrWhiteSpace(created.BiosUuid))
            target.BiosUuid = created.BiosUuid;
        if (string.IsNullOrWhiteSpace(target.VmInstanceUuid) && !string.IsNullOrWhiteSpace(created.VmInstanceUuid))
            target.VmInstanceUuid = created.VmInstanceUuid;
        if (string.IsNullOrWhiteSpace(target.DomainSid) && !string.IsNullOrWhiteSpace(created.DomainSid))
            target.DomainSid = created.DomainSid;

        var sources = target.DiscoverySource
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        foreach (var source in created.DiscoverySource.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!sources.Any(s => s.Equals(source, StringComparison.OrdinalIgnoreCase)))
                sources.Add(source);
        }

        target.DiscoverySource = string.Join(" + ", sources);
        target.FirstSeen = target.FirstSeen < created.FirstSeen ? target.FirstSeen : created.FirstSeen;
        target.LastSeen = target.LastSeen > created.LastSeen ? target.LastSeen : created.LastSeen;
        target.IsStale = target.IsStale && created.IsStale;
        target.StaleSince = target.IsStale ? target.StaleSince : null;
        target.PropertiesJson = MergePropertiesJson(target.PropertiesJson, created.PropertiesJson, created.Id);
        target.UpdatedAt = DateTime.UtcNow;
    }

    private async Task MoveSourceLinksAsync(Guid createdNodeId, Guid targetNodeId, CancellationToken cancellationToken)
    {
        var createdLinks = await _context.NodeSourceLinks
            .Where(l => l.NodeId == createdNodeId)
            .ToListAsync(cancellationToken);

        var targetLinks = await _context.NodeSourceLinks
            .Where(l => l.NodeId == targetNodeId)
            .ToListAsync(cancellationToken);

        var targetKeys = targetLinks
            .Select(l => (l.IntegrationId, l.ExternalId))
            .ToHashSet();

        foreach (var link in createdLinks)
        {
            if (targetKeys.Contains((link.IntegrationId, link.ExternalId)))
            {
                _context.NodeSourceLinks.Remove(link);
                continue;
            }

            link.NodeId = targetNodeId;
            link.UpdatedAt = DateTime.UtcNow;
            targetKeys.Add((link.IntegrationId, link.ExternalId));
        }
    }

    private async Task MoveRelationshipsAsync(Guid createdNodeId, Guid targetNodeId, CancellationToken cancellationToken)
    {
        var moved = await _context.InfrastructureRelationships
            .Where(r => r.SourceNodeId == createdNodeId || r.TargetNodeId == createdNodeId)
            .ToListAsync(cancellationToken);

        var existing = await _context.InfrastructureRelationships
            .Where(r => (r.SourceNodeId == targetNodeId || r.TargetNodeId == targetNodeId) &&
                        r.SourceNodeId != createdNodeId &&
                        r.TargetNodeId != createdNodeId)
            .ToListAsync(cancellationToken);

        var keys = existing
            .Select(r => (r.SourceNodeId, r.TargetNodeId, r.RelationshipType))
            .ToHashSet();

        foreach (var relationship in moved)
        {
            var newSource = relationship.SourceNodeId == createdNodeId ? targetNodeId : relationship.SourceNodeId;
            var newTarget = relationship.TargetNodeId == createdNodeId ? targetNodeId : relationship.TargetNodeId;

            if (newSource == newTarget)
            {
                _context.InfrastructureRelationships.Remove(relationship);
                continue;
            }

            var key = (newSource, newTarget, relationship.RelationshipType);
            if (keys.Contains(key))
            {
                _context.InfrastructureRelationships.Remove(relationship);
                continue;
            }

            relationship.SourceNodeId = newSource;
            relationship.TargetNodeId = newTarget;
            relationship.UpdatedAt = DateTime.UtcNow;
            keys.Add(key);
        }
    }

    private static string MergePropertiesJson(string targetJson, string sourceJson, Guid mergedNodeId)
    {
        JsonObject target;
        JsonObject source;
        try
        {
            target = JsonNode.Parse(string.IsNullOrWhiteSpace(targetJson) ? "{}" : targetJson) as JsonObject ?? new JsonObject();
            source = JsonNode.Parse(string.IsNullOrWhiteSpace(sourceJson) ? "{}" : sourceJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return targetJson;
        }

        foreach (var (key, value) in source)
        {
            if (!target.ContainsKey(key))
                target[key] = value?.DeepClone();
        }

        target["_mergedNodeIds"] = AppendMergedId(target["_mergedNodeIds"] as JsonArray, mergedNodeId);
        return target.ToJsonString();
    }

    private static JsonArray AppendMergedId(JsonArray? array, Guid nodeId)
    {
        array ??= new JsonArray();
        var value = nodeId.ToString("D");
        if (!array.Any(x => string.Equals(x?.GetValue<string>(), value, StringComparison.OrdinalIgnoreCase)))
            array.Add(value);
        return array;
    }

    private async Task<HashSet<Guid>> GetPendingReviewNodeIdsAsync(CancellationToken cancellationToken)
    {
        var ids = await _context.ReconciliationReviews
            .AsNoTracking()
            .Where(r => r.Status == ReconciliationReviewStatus.Pending)
            .Select(r => new { r.CreatedNodeId, r.CandidateNodeId })
            .ToListAsync(cancellationToken);

        return ids
            .SelectMany(x => new[] { x.CreatedNodeId, x.CandidateNodeId })
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToHashSet();
    }

    private static InventoryNodeDto MapNode(
        InfrastructureNode node,
        IReadOnlyList<NodeSourceLink> links,
        bool needsReview)
    {
        var sourceSystems = links
            .Select(l => l.SourceSystem)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new InventoryNodeDto
        {
            Id = node.Id,
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
            StaleSince = node.StaleSince,
            FirstSeen = node.FirstSeen,
            LastSeen = node.LastSeen,
            SourceCount = links.Count(l => !l.IsStale),
            SourceSystems = sourceSystems,
            Properties = ParseProperties(node.PropertiesJson),
            NeedsReview = needsReview
        };
    }

    private static ReconciliationReviewDto MapReview(
        ReconciliationReview review,
        IReadOnlyDictionary<Guid, string> nodeNames,
        string integrationName)
    {
        var candidateIds = ParseGuidList(review.CandidateNodeIdsJson);
        return new ReconciliationReviewDto
        {
            Id = review.Id,
            Status = review.Status.ToString(),
            IntegrationId = review.IntegrationId,
            IntegrationName = integrationName,
            DiscoveryJobId = review.DiscoveryJobId,
            CreatedNodeId = review.CreatedNodeId,
            CreatedNodeName = review.CreatedNodeId.HasValue
                ? nodeNames.GetValueOrDefault(review.CreatedNodeId.Value, string.Empty)
                : string.Empty,
            CandidateNodeId = review.CandidateNodeId,
            CandidateNodeName = review.CandidateNodeId.HasValue
                ? nodeNames.GetValueOrDefault(review.CandidateNodeId.Value, string.Empty)
                : string.Empty,
            SourceSystem = review.SourceSystem,
            MatchKey = review.MatchKey,
            Confidence = review.Confidence,
            MatchScore = review.MatchScore,
            Reason = review.Reason,
            CandidateSummary = review.CandidateSummary,
            DiscoveredNode = ParseJson(review.DiscoveredNodeJson),
            CandidateNodeIds = candidateIds,
            CreatedAt = review.CreatedAt,
            ReviewedAt = review.ReviewedAt,
            ReviewedBy = review.ReviewedBy,
            ResolutionNotes = review.ResolutionNotes
        };
    }

    private async Task<Dictionary<Guid, string>> GetReviewNodeNamesAsync(
        ReconciliationReview review,
        CancellationToken cancellationToken)
    {
        var ids = new[] { review.CreatedNodeId, review.CandidateNodeId }
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();

        return await _context.InfrastructureNodes
            .AsNoTracking()
            .Where(n => ids.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, n => n.Name, cancellationToken);
    }

    private async Task<string> GetIntegrationNameAsync(Guid? integrationId, CancellationToken cancellationToken)
    {
        if (!integrationId.HasValue)
            return string.Empty;

        return await _context.Integrations
            .AsNoTracking()
            .Where(i => i.Id == integrationId.Value)
            .Select(i => i.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
    }

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
        }

        return result;
    }

    private static JsonElement ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return JsonSerializer.SerializeToElement(new { });

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    private static List<Guid> ParseGuidList(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json ?? "[]") ?? new List<Guid>();
        }
        catch (JsonException)
        {
            return new List<Guid>();
        }
    }
}
