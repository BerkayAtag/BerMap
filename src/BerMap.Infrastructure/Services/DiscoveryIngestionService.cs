using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Application.Services.Reconciliation;
using BerMap.Contracts;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BerMap.Infrastructure.Services;

/// <summary>
/// Converts an adapter discovery result into reconciled graph nodes, stable
/// source links, relationships and review items. The service is idempotent for
/// a given (integration, externalId) pair.
/// </summary>
public sealed class DiscoveryIngestionService : IDiscoveryIngestionService
{
    private readonly BerMapDbContext _context;
    private readonly IEntityReconciliationService _reconciliation;
    private readonly ILogger<DiscoveryIngestionService> _logger;

    public DiscoveryIngestionService(
        BerMapDbContext context,
        IEntityReconciliationService reconciliation,
        ILogger<DiscoveryIngestionService> logger)
    {
        _context = context;
        _reconciliation = reconciliation;
        _logger = logger;
    }

    public Task<IngestionResult> IngestAsync(
        Guid integrationId,
        Guid jobId,
        DiscoveryResult discovery,
        CancellationToken cancellationToken = default) =>
        IngestAsync(integrationId, jobId, discovery,
            discovery.Success && string.Equals(discovery.Status, "Completed", StringComparison.OrdinalIgnoreCase),
            cancellationToken);

    public async Task<IngestionResult> IngestAsync(
        Guid integrationId,
        Guid jobId,
        DiscoveryResult discovery,
        bool isCompleteSnapshot,
        CancellationToken cancellationToken = default)
    {
        var integration = await _context.Integrations
            .FirstOrDefaultAsync(i => i.Id == integrationId, cancellationToken)
            ?? throw new InvalidOperationException($"Integration '{integrationId}' was not found.");

        var result = new IngestionResult();
        var now = DateTime.UtcNow;

        var links = await _context.NodeSourceLinks
            .Where(l => l.IntegrationId == integrationId)
            .ToListAsync(cancellationToken);

        var linksByExternalId = links
            .GroupBy(l => l.ExternalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var seenLinkIds = new HashSet<Guid>();
        var externalToNodeId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var createdNodes = new List<InfrastructureNode>();
        var createdNodeIds = new HashSet<Guid>();

        var pendingReviews = await _context.ReconciliationReviews
            .Where(r => r.IntegrationId == integrationId && r.Status == ReconciliationReviewStatus.Pending)
            .ToListAsync(cancellationToken);

        var reviewsBySignature = pendingReviews
            .GroupBy(r => r.Signature, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var existingRelationships = await _context.InfrastructureRelationships
            .Where(r => r.IntegrationId == integrationId)
            .ToListAsync(cancellationToken);

        var relationshipIndex = existingRelationships
            .GroupBy(r => (r.SourceNodeId, r.TargetNodeId, r.RelationshipType))
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var discovered in discovery.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(discovered.ExternalId))
            {
                result.Warnings.Add($"Skipped node '{discovered.Name}' because the adapter returned no external id.");
                continue;
            }

            var externalId = discovered.ExternalId.Trim();
            linksByExternalId.TryGetValue(externalId, out var existingLink);

            InfrastructureNode? linkedNode = null;
            if (existingLink != null)
            {
                linkedNode = await _context.InfrastructureNodes
                    .FirstOrDefaultAsync(n => n.Id == existingLink.NodeId, cancellationToken);
            }

            var candidates = await LoadCandidatesAsync(discovered, linkedNode?.Id, createdNodes, cancellationToken);
            var decision = _reconciliation.Reconcile(discovered, candidates, linkedNode?.Id);
            var node = decision.TargetNode;

            var wasCreatedEarlierInJob = createdNodeIds.Contains(node.Id);
            if (decision.IsNew && !wasCreatedEarlierInJob)
            {
                _context.InfrastructureNodes.Add(node);
                createdNodes.Add(node);
                createdNodeIds.Add(node.Id);
                result.NodesCreated++;
            }
            else if (decision.MetadataChanged)
            {
                result.NodesUpdated++;
            }
            else
            {
                result.NodesUnchanged++;
            }

            node.LastSeen = now;
            node.LastSeenJobId = jobId;
            node.IsStale = false;
            node.StaleSince = null;

            if (existingLink == null)
            {
                existingLink = new NodeSourceLink
                {
                    Id = Guid.NewGuid(),
                    IntegrationId = integrationId,
                    NodeId = node.Id,
                    ExternalId = externalId,
                    SourceSystem = string.IsNullOrWhiteSpace(discovered.SourceSystem) ? integration.AdapterType : discovered.SourceSystem,
                    NodeType = node.NodeType,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    LastSeenJobId = jobId,
                    IsStale = false
                };

                _context.NodeSourceLinks.Add(existingLink);
                linksByExternalId[externalId] = existingLink;
                links.Add(existingLink);
                result.SourceLinksCreated++;
            }
            else
            {
                existingLink.NodeId = node.Id;
                existingLink.NodeType = node.NodeType;
                existingLink.LastSeenAt = now;
                existingLink.LastSeenJobId = jobId;
                existingLink.IsStale = false;
                existingLink.UpdatedAt = now;
            }

            seenLinkIds.Add(existingLink.Id);
            existingLink.PropertiesJson = JsonSerializer.Serialize(discovered.Properties);
            externalToNodeId[externalId] = node.Id;

            if (decision.RequiresReview && decision.Candidates.Count > 0)
            {
                await PersistReviewAsync(
                    integrationId,
                    jobId,
                    discovered,
                    decision,
                    reviewsBySignature,
                    result,
                    now,
                    cancellationToken);

                if (decision.IsNew && decision.Candidates.Count > 0)
                {
                    await EnsurePossibleDuplicateRelationshipAsync(
                        node.Id,
                        decision.Candidates[0].NodeId,
                        integration,
                        jobId,
                        decision.Reason,
                        relationshipIndex,
                        result,
                        now,
                        cancellationToken);
                }
            }
        }

        // The (source, target, type) key is unique across all integrations. Two
        // sources that reconcile onto the same nodes (or a re-created
        // integration) would otherwise insert a duplicate and fail the run.
        var touchedNodeIds = externalToNodeId.Values.Distinct().ToList();
        if (touchedNodeIds.Count > 0 && discovery.Relationships.Count > 0)
        {
            var foreignRelationships = await _context.InfrastructureRelationships
                .Where(r => r.IntegrationId != integrationId &&
                            touchedNodeIds.Contains(r.SourceNodeId) &&
                            touchedNodeIds.Contains(r.TargetNodeId))
                .ToListAsync(cancellationToken);

            foreach (var foreign in foreignRelationships)
                relationshipIndex.TryAdd((foreign.SourceNodeId, foreign.TargetNodeId, foreign.RelationshipType), foreign);
        }

        foreach (var relationship in discovery.Relationships)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!externalToNodeId.TryGetValue(relationship.SourceExternalId, out var sourceNodeId) ||
                !externalToNodeId.TryGetValue(relationship.TargetExternalId, out var targetNodeId))
            {
                result.Warnings.Add(
                    $"Skipped relationship '{relationship.RelationshipType}' because an endpoint was not present in the discovery result.");
                continue;
            }

            if (sourceNodeId == targetNodeId)
                continue;

            var relationshipType = ParseRelationshipType(relationship.RelationshipType);
            var confidence = ParseConfidence(relationship.ConfidenceLevel);
            var key = (sourceNodeId, targetNodeId, relationshipType);

            if (!relationshipIndex.TryGetValue(key, out var existing))
            {
                existing = new InfrastructureRelationship
                {
                    Id = Guid.NewGuid(),
                    SourceNodeId = sourceNodeId,
                    TargetNodeId = targetNodeId,
                    RelationshipType = relationshipType,
                    ConfidenceLevel = confidence,
                    Evidence = relationship.Evidence,
                    SourceSystem = string.IsNullOrWhiteSpace(relationship.SourceSystem) ? integration.AdapterType : relationship.SourceSystem,
                    IntegrationId = integrationId,
                    PropertiesJson = JsonSerializer.Serialize(relationship.Properties),
                    FirstSeen = now,
                    LastSeen = now,
                    LastSeenJobId = jobId,
                    IsStale = false
                };

                _context.InfrastructureRelationships.Add(existing);
                relationshipIndex[key] = existing;
                result.RelationshipsCreated++;
            }
            else
            {
                var changed = existing.Evidence != relationship.Evidence ||
                              existing.ConfidenceLevel != confidence ||
                              existing.IsStale;

                existing.Evidence = relationship.Evidence;
                existing.ConfidenceLevel = confidence;
                existing.LastSeen = now;
                existing.LastSeenJobId = jobId;
                existing.IsStale = false;
                existing.StaleSince = null;
                existing.UpdatedAt = now;
                existing.PropertiesJson = JsonSerializer.Serialize(relationship.Properties);

                if (changed)
                    result.RelationshipsUpdated++;
            }
        }

        // Source links that were not returned by this full discovery become stale.
        // A partial view proves nothing about absent objects, so skip it then.
        foreach (var link in links.Where(l => isCompleteSnapshot && !seenLinkIds.Contains(l.Id)))
        {
            if (!link.IsStale)
            {
                link.IsStale = true;
                link.UpdatedAt = now;
                result.SourceLinksStale++;
            }
        }

        // A node is stale only when every source link that references it is stale.
        var affectedNodeIds = isCompleteSnapshot
            ? links.Select(l => l.NodeId).Distinct().ToList()
            : new List<Guid>();
        if (affectedNodeIds.Count > 0)
        {
            var allLinks = await _context.NodeSourceLinks
                .Where(l => affectedNodeIds.Contains(l.NodeId))
                .ToListAsync(cancellationToken);

            var linksByNode = allLinks.GroupBy(l => l.NodeId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var nodeId in affectedNodeIds)
            {
                if (!linksByNode.TryGetValue(nodeId, out var nodeLinks) ||
                    nodeLinks.Any(l => !l.IsStale))
                {
                    continue;
                }

                var node = await _context.InfrastructureNodes
                    .FirstOrDefaultAsync(n => n.Id == nodeId, cancellationToken);

                if (node == null || node.IsStale)
                    continue;

                node.IsStale = true;
                node.StaleSince = now;
                node.UpdatedAt = now;
                result.NodesMarkedStale++;
            }
        }

        // Relationships belonging to this integration that were not observed in
        // this run are marked stale, but retained for historical graph context.
        foreach (var relationship in existingRelationships.Where(r =>
                     isCompleteSnapshot && r.LastSeenJobId != jobId && !r.IsStale))
        {
            relationship.IsStale = true;
            relationship.StaleSince = now;
            relationship.UpdatedAt = now;
            result.RelationshipsMarkedStale++;
        }

        integration.LastSyncAt = now;
        integration.DiscoveredEntitiesCount = links.Count(l => !l.IsStale);
        integration.UpdatedAt = now;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Ingested discovery job {JobId}: {Created} nodes created, {Updated} updated, {Relationships} relationships, {Reviews} reviews",
            jobId, result.NodesCreated, result.NodesUpdated, result.RelationshipsCreated, result.ReviewsCreated);

        return result;
    }

    private async Task<List<InfrastructureNode>> LoadCandidatesAsync(
        DiscoveredNodeContract discovered,
        Guid? linkedNodeId,
        IReadOnlyList<InfrastructureNode> createdNodes,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<Guid, InfrastructureNode>();

        void Add(IEnumerable<InfrastructureNode> nodes)
        {
            foreach (var node in nodes)
                candidates[node.Id] = node;
        }

        if (linkedNodeId.HasValue)
        {
            var linked = await _context.InfrastructureNodes
                .FirstOrDefaultAsync(n => n.Id == linkedNodeId.Value, cancellationToken);
            if (linked != null)
                candidates[linked.Id] = linked;
        }

        if (!string.IsNullOrWhiteSpace(discovered.BiosUuid))
        {
            var value = discovered.BiosUuid.Trim().Trim('{', '}').ToLowerInvariant();
            Add(await _context.InfrastructureNodes
                .Where(n => n.BiosUuid != string.Empty && n.BiosUuid.ToLower().Contains(value))
                .Take(25)
                .ToListAsync(cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(discovered.VmInstanceUuid))
        {
            var value = discovered.VmInstanceUuid.Trim().Trim('{', '}').ToLowerInvariant();
            Add(await _context.InfrastructureNodes
                .Where(n => n.VmInstanceUuid != string.Empty && n.VmInstanceUuid.ToLower().Contains(value))
                .Take(25)
                .ToListAsync(cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(discovered.DomainSid))
        {
            var value = discovered.DomainSid.Trim().ToLowerInvariant();
            Add(await _context.InfrastructureNodes
                .Where(n => n.DomainSid != string.Empty && n.DomainSid.ToLower() == value)
                .Take(25)
                .ToListAsync(cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(discovered.FQDN))
        {
            var value = discovered.FQDN.Trim().TrimEnd('.').ToLowerInvariant();
            Add(await _context.InfrastructureNodes
                .Where(n => n.FQDN != string.Empty && n.FQDN.ToLower() == value)
                .Take(25)
                .ToListAsync(cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(discovered.IPAddress))
        {
            var value = discovered.IPAddress.Trim();
            Add(await _context.InfrastructureNodes
                .Where(n => n.IPAddress == value)
                .Take(25)
                .ToListAsync(cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(discovered.MacAddress))
        {
            var value = NormalizeMac(discovered.MacAddress);
            if (value.Length == 12)
            {
                Add(await _context.InfrastructureNodes
                    .Where(n => n.MacAddress != null && n.MacAddress != string.Empty &&
                                n.MacAddress.ToLower().Replace(":", "").Replace("-", "").Replace(".", "").Contains(value))
                    .Take(25)
                    .ToListAsync(cancellationToken));
            }
        }

        var shortName = ShortName(discovered);
        if (shortName.Length > 0)
        {
            Add(await _context.InfrastructureNodes
                .Where(n => n.Name.ToLower() == shortName ||
                            n.Name.ToLower().StartsWith(shortName + ".") ||
                            (n.FQDN != string.Empty && n.FQDN.ToLower().StartsWith(shortName + ".")))
                .Take(50)
                .ToListAsync(cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(discovered.Name))
        {
            var value = discovered.Name.Trim().ToLowerInvariant();
            Add(await _context.InfrastructureNodes
                .Where(n => n.Name.ToLower() == value)
                .Take(25)
                .ToListAsync(cancellationToken));
        }

        foreach (var created in createdNodes)
        {
            if (CouldMatch(created, discovered))
                candidates[created.Id] = created;
        }

        return candidates.Values.ToList();
    }

    private static bool CouldMatch(InfrastructureNode node, DiscoveredNodeContract discovered)
    {
        if (!string.IsNullOrWhiteSpace(discovered.BiosUuid) &&
            NormalizeUuid(node.BiosUuid) == NormalizeUuid(discovered.BiosUuid))
            return true;

        if (!string.IsNullOrWhiteSpace(discovered.VmInstanceUuid) &&
            NormalizeUuid(node.VmInstanceUuid) == NormalizeUuid(discovered.VmInstanceUuid))
            return true;

        if (!string.IsNullOrWhiteSpace(discovered.DomainSid) &&
            string.Equals(node.DomainSid, discovered.DomainSid, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(discovered.FQDN) &&
            string.Equals(node.FQDN, discovered.FQDN, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(discovered.IPAddress) &&
            string.Equals(node.IPAddress, discovered.IPAddress, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(discovered.MacAddress) &&
            NormalizeMac(node.MacAddress) == NormalizeMac(discovered.MacAddress))
            return true;

        var shortName = ShortName(discovered);
        return shortName.Length > 0 && ShortName(node).Equals(shortName, StringComparison.OrdinalIgnoreCase);
    }

    private async Task PersistReviewAsync(
        Guid integrationId,
        Guid jobId,
        DiscoveredNodeContract discovered,
        ReconciliationResult decision,
        Dictionary<string, ReconciliationReview> reviewsBySignature,
        IngestionResult result,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var candidateIds = decision.Candidates
            .Select(c => c.NodeId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        var signature = ComputeReviewSignature(integrationId, discovered.ExternalId, decision.MatchKey, candidateIds);
        var discoveredJson = JsonSerializer.Serialize(discovered);
        var candidateSummary = string.Join(", ", decision.Candidates
            .Take(5)
            .Select(c => string.IsNullOrWhiteSpace(c.FQDN) ? c.Name : $"{c.Name} ({c.FQDN})"));

        if (reviewsBySignature.TryGetValue(signature, out var review))
        {
            review.DiscoveryJobId = jobId;
            review.CreatedNodeId = decision.TargetNode.Id;
            review.CandidateNodeId = candidateIds.FirstOrDefault();
            review.CandidateNodeIdsJson = JsonSerializer.Serialize(candidateIds);
            review.CandidateSummary = candidateSummary;
            review.Confidence = decision.Confidence.ToString();
            review.MatchKey = decision.MatchKey;
            review.MatchScore = decision.MatchScore;
            review.Reason = decision.Reason;
            review.DiscoveredNodeJson = discoveredJson;
            review.SourceSystem = string.IsNullOrWhiteSpace(discovered.SourceSystem) ? "Unknown" : discovered.SourceSystem;
            review.UpdatedAt = now;
            result.ReviewsUpdated++;
            return;
        }

        review = new ReconciliationReview
        {
            Id = Guid.NewGuid(),
            Status = ReconciliationReviewStatus.Pending,
            IntegrationId = integrationId,
            DiscoveryJobId = jobId,
            CreatedNodeId = decision.TargetNode.Id,
            CandidateNodeId = candidateIds.FirstOrDefault(),
            CandidateNodeIdsJson = JsonSerializer.Serialize(candidateIds),
            CandidateSummary = candidateSummary,
            SourceSystem = string.IsNullOrWhiteSpace(discovered.SourceSystem) ? "Unknown" : discovered.SourceSystem,
            MatchKey = decision.MatchKey,
            Confidence = decision.Confidence.ToString(),
            MatchScore = decision.MatchScore,
            Reason = decision.Reason,
            DiscoveredNodeJson = discoveredJson,
            Signature = signature,
            CreatedAt = now
        };

        _context.ReconciliationReviews.Add(review);
        reviewsBySignature[signature] = review;
        result.ReviewsCreated++;
        await Task.CompletedTask;
    }

    private async Task EnsurePossibleDuplicateRelationshipAsync(
        Guid createdNodeId,
        Guid candidateNodeId,
        Integration integration,
        Guid jobId,
        string reason,
        Dictionary<(Guid, Guid, RelationshipType), InfrastructureRelationship> relationshipIndex,
        IngestionResult result,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var key = (createdNodeId, candidateNodeId, RelationshipType.PossibleDuplicate);
        if (relationshipIndex.ContainsKey(key))
            return;

        var exists = await _context.InfrastructureRelationships
            .AnyAsync(r => r.SourceNodeId == createdNodeId &&
                           r.TargetNodeId == candidateNodeId &&
                           r.RelationshipType == RelationshipType.PossibleDuplicate,
                cancellationToken);

        if (exists)
            return;

        var relationship = new InfrastructureRelationship
        {
            Id = Guid.NewGuid(),
            SourceNodeId = createdNodeId,
            TargetNodeId = candidateNodeId,
            RelationshipType = RelationshipType.PossibleDuplicate,
            ConfidenceLevel = ConfidenceLevel.Inferred,
            Evidence = reason,
            SourceSystem = integration.AdapterType,
            IntegrationId = integration.Id,
            FirstSeen = now,
            LastSeen = now,
            LastSeenJobId = jobId,
            IsStale = false
        };

        _context.InfrastructureRelationships.Add(relationship);
        relationshipIndex[key] = relationship;
        result.RelationshipsCreated++;
    }

    private static string ComputeReviewSignature(
        Guid integrationId,
        string externalId,
        string matchKey,
        IEnumerable<Guid> candidateIds)
    {
        var raw = $"{integrationId:N}|{externalId.Trim().ToLowerInvariant()}|{matchKey}|" +
                  string.Join(",", candidateIds.OrderBy(id => id));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static RelationshipType ParseRelationshipType(string value)
    {
        return Enum.TryParse<RelationshipType>(value, true, out var parsed)
            ? parsed
            : RelationshipType.ConnectedTo;
    }

    private static ConfidenceLevel ParseConfidence(string value)
    {
        return Enum.TryParse<ConfidenceLevel>(value, true, out var parsed)
            ? parsed
            : ConfidenceLevel.Observed;
    }

    private static string ShortName(DiscoveredNodeContract discovered)
    {
        var value = string.IsNullOrWhiteSpace(discovered.Name) ? discovered.FQDN : discovered.Name;
        return ShortName(value);
    }

    private static string ShortName(InfrastructureNode node)
    {
        var value = string.IsNullOrWhiteSpace(node.Name) ? node.FQDN : node.Name;
        return ShortName(value);
    }

    private static string ShortName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var trimmed = value.Trim().TrimEnd('.');
        var dot = trimmed.IndexOf('.');
        return (dot > 0 ? trimmed[..dot] : trimmed).ToLowerInvariant();
    }

    private static string NormalizeUuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var trimmed = value.Trim().Trim('{', '}');
        return Guid.TryParse(trimmed, out var guid) ? guid.ToString("D") : trimmed.ToLowerInvariant();
    }

    private static string NormalizeMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var chars = value.Where(Uri.IsHexDigit).Select(char.ToLowerInvariant).ToArray();
        return chars.Length == 12 ? new string(chars) : value.Trim().ToLowerInvariant();
    }
}
