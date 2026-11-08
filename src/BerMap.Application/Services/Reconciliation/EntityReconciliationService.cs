using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BerMap.Application.DTOs;
using BerMap.Contracts;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;

namespace BerMap.Application.Services.Reconciliation;

public enum ReconciliationConfidence
{
    ExactSourceIdentity,      // Stable (integration, externalId) source link
    ExactHardwareMatch,       // BIOS UUID / VM Instance UUID
    StrongDomainSidMatch,     // Active Directory objectSid
    StrongFqdnMatch,          // Fully qualified domain name
    StrongNetworkIdentityMatch, // IP + MAC
    ProbableNameParentMatch,  // Name + type + parent container
    AmbiguousConflict,        // Possible duplicate; never auto-merged
    NewEntity                 // No existing match
}

public sealed class ReconciliationCandidate
{
    public Guid NodeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FQDN { get; set; } = string.Empty;
    public string MatchKey { get; set; } = string.Empty;
    public double Score { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class ReconciliationResult
{
    public ReconciliationConfidence Confidence { get; set; }
    public InfrastructureNode TargetNode { get; set; } = null!;
    public bool IsNew { get; set; }
    public bool RequiresReview { get; set; }
    public bool MetadataChanged { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string MatchKey { get; set; } = string.Empty;
    public double MatchScore { get; set; }
    public List<ReconciliationCandidate> Candidates { get; set; } = new();
}

public interface IEntityReconciliationService
{
    ReconciliationResult Reconcile(
        DiscoveredNodeContract discovered,
        IEnumerable<InfrastructureNode> existingNodes,
        Guid? sourceLinkedNodeId = null);
}

/// <summary>
/// Deterministic, explainable entity reconciliation.
///
/// A short hostname alone is never sufficient to merge records. Every decision
/// returns the match key, score, candidates and human-readable evidence so the
/// ingestion pipeline can persist an auditable review when confidence is low.
/// </summary>
public class EntityReconciliationService : IEntityReconciliationService
{
    private const double MinimumAutoMergeScore = 0.80;

    public ReconciliationResult Reconcile(
        DiscoveredNodeContract discovered,
        IEnumerable<InfrastructureNode> existingNodes,
        Guid? sourceLinkedNodeId = null)
    {
        var existingList = existingNodes
            .Where(n => n != null)
            .GroupBy(n => n.Id)
            .Select(g => g.First())
            .ToList();

        // Rule 0: stable source identity. This is the strongest possible link and
        // keeps re-discovery idempotent even when the source has no FQDN/UUID.
        if (sourceLinkedNodeId.HasValue)
        {
            var linked = existingList.FirstOrDefault(n => n.Id == sourceLinkedNodeId.Value);
            if (linked != null)
            {
                var changed = MergeDiscoveredMetadata(linked, discovered, "Matched via stable source link", "ExactSourceIdentity",
                    refreshFromSameSource: true);
                return Result(
                    ReconciliationConfidence.ExactSourceIdentity,
                    linked,
                    isNew: false,
                    requiresReview: false,
                    reason: $"Stable source identity link (integration + externalId '{discovered.ExternalId}').",
                    matchKey: "SourceLink",
                    score: 1.0,
                    candidates: new[] { Candidate(linked, "SourceLink", 1.0, "Stable source link") },
                    changed: changed);
            }
        }

        var biosUuid = NormalizeUuid(discovered.BiosUuid);
        var vmInstanceUuid = NormalizeUuid(discovered.VmInstanceUuid);
        var domainSid = NormalizeSid(discovered.DomainSid);
        var fqdn = NormalizeFqdn(discovered.FQDN);
        var ip = NormalizeIp(discovered.IPAddress);
        var mac = NormalizeMac(discovered.MacAddress);
        var shortName = ShortName(discovered);

        // Rule 1: BIOS UUID.
        if (biosUuid.Length > 0)
        {
            var matches = existingList
                .Where(n => NormalizeUuid(n.BiosUuid) == biosUuid)
                .ToList();

            if (matches.Count == 1)
            {
                var candidate = matches[0];
                if (HasHardwareConflict(discovered, candidate, out var conflict))
                    return Ambiguous(discovered, candidate, "BiosUuid", 0.95, conflict);

                var changed = MergeDiscoveredMetadata(candidate, discovered, "Matched via BIOS UUID", "ExactHardwareMatch");
                return Result(
                    ReconciliationConfidence.ExactHardwareMatch,
                    candidate,
                    isNew: false,
                    requiresReview: false,
                    reason: $"Exact BIOS UUID match: {discovered.BiosUuid}",
                    matchKey: "BiosUuid",
                    score: 1.0,
                    candidates: matches.Select(n => Candidate(n, "BiosUuid", 1.0, "BIOS UUID")),
                    changed: changed);
            }

            if (matches.Count > 1)
            {
                return Ambiguous(
                    discovered,
                    matches,
                    "BiosUuid",
                    0.95,
                    $"BIOS UUID '{discovered.BiosUuid}' is present on {matches.Count} existing nodes.");
            }
        }

        // Rule 2: VM instance UUID.
        if (vmInstanceUuid.Length > 0)
        {
            var matches = existingList
                .Where(n => NormalizeUuid(n.VmInstanceUuid) == vmInstanceUuid)
                .ToList();

            if (matches.Count == 1)
            {
                var candidate = matches[0];
                if (HasHardwareConflict(discovered, candidate, out var conflict))
                    return Ambiguous(discovered, candidate, "VmInstanceUuid", 0.95, conflict);

                var changed = MergeDiscoveredMetadata(candidate, discovered, "Matched via VM instance UUID", "ExactHardwareMatch");
                return Result(
                    ReconciliationConfidence.ExactHardwareMatch,
                    candidate,
                    isNew: false,
                    requiresReview: false,
                    reason: $"Exact VM instance UUID match: {discovered.VmInstanceUuid}",
                    matchKey: "VmInstanceUuid",
                    score: 1.0,
                    candidates: matches.Select(n => Candidate(n, "VmInstanceUuid", 1.0, "VM instance UUID")),
                    changed: changed);
            }

            if (matches.Count > 1)
            {
                return Ambiguous(
                    discovered,
                    matches,
                    "VmInstanceUuid",
                    0.95,
                    $"VM instance UUID '{discovered.VmInstanceUuid}' is present on {matches.Count} existing nodes.");
            }
        }

        // Rule 3: Active Directory domain SID.
        if (domainSid.Length > 0)
        {
            var matches = existingList
                .Where(n => NormalizeSid(n.DomainSid) == domainSid)
                .ToList();

            if (matches.Count == 1)
            {
                var candidate = matches[0];
                if (HasHardwareConflict(discovered, candidate, out var conflict))
                    return Ambiguous(discovered, candidate, "DomainSid", 0.92, conflict);

                var changed = MergeDiscoveredMetadata(candidate, discovered, "Matched via domain SID", "StrongDomainSidMatch");
                return Result(
                    ReconciliationConfidence.StrongDomainSidMatch,
                    candidate,
                    isNew: false,
                    requiresReview: false,
                    reason: $"Exact domain SID match: {discovered.DomainSid}",
                    matchKey: "DomainSid",
                    score: 0.98,
                    candidates: matches.Select(n => Candidate(n, "DomainSid", 0.98, "Domain SID")),
                    changed: changed);
            }

            if (matches.Count > 1)
            {
                return Ambiguous(
                    discovered,
                    matches,
                    "DomainSid",
                    0.92,
                    $"Domain SID '{discovered.DomainSid}' is present on {matches.Count} existing nodes.");
            }
        }

        // Rule 4: normalized FQDN.
        if (fqdn.Length > 0)
        {
            var matches = existingList
                .Where(n => NormalizeFqdn(n.FQDN) == fqdn)
                .ToList();

            if (matches.Count == 1)
            {
                var candidate = matches[0];
                if (HasHardwareConflict(discovered, candidate, out var conflict))
                    return Ambiguous(discovered, candidate, "FQDN", 0.85, conflict);

                var changed = MergeDiscoveredMetadata(candidate, discovered, "Matched via normalized FQDN", "StrongFqdnMatch");
                return Result(
                    ReconciliationConfidence.StrongFqdnMatch,
                    candidate,
                    isNew: false,
                    requiresReview: false,
                    reason: $"FQDN match: {discovered.FQDN}",
                    matchKey: "FQDN",
                    score: 0.95,
                    candidates: matches.Select(n => Candidate(n, "FQDN", 0.95, "FQDN")),
                    changed: changed);
            }

            if (matches.Count > 1)
            {
                return Ambiguous(
                    discovered,
                    matches,
                    "FQDN",
                    0.85,
                    $"FQDN '{discovered.FQDN}' is present on {matches.Count} existing nodes.");
            }
        }

        // Rule 5: IP + MAC network identity.
        if (ip.Length > 0 && mac.Length > 0)
        {
            var both = existingList
                .Where(n => NormalizeIp(n.IPAddress) == ip && NormalizeMac(n.MacAddress) == mac)
                .ToList();

            if (both.Count == 1)
            {
                var candidate = both[0];
                var changed = MergeDiscoveredMetadata(candidate, discovered, "Matched via IP + MAC", "StrongNetworkIdentityMatch");
                return Result(
                    ReconciliationConfidence.StrongNetworkIdentityMatch,
                    candidate,
                    isNew: false,
                    requiresReview: false,
                    reason: $"IP + MAC match: {discovered.IPAddress} / {discovered.MacAddress}",
                    matchKey: "IpMac",
                    score: 0.93,
                    candidates: both.Select(n => Candidate(n, "IpMac", 0.93, "IP + MAC")),
                    changed: changed);
            }

            if (both.Count > 1)
            {
                return Ambiguous(
                    discovered,
                    both,
                    "IpMac",
                    0.9,
                    $"IP + MAC pair '{discovered.IPAddress} / {discovered.MacAddress}' is present on {both.Count} existing nodes.");
            }

            var sameIpDifferentMac = existingList
                .Where(n => NormalizeIp(n.IPAddress) == ip &&
                            NormalizeMac(n.MacAddress).Length > 0 &&
                            NormalizeMac(n.MacAddress) != mac)
                .ToList();

            if (sameIpDifferentMac.Count > 0)
            {
                return Ambiguous(
                    discovered,
                    sameIpDifferentMac,
                    "IpConflict",
                    0.6,
                    $"IP address '{discovered.IPAddress}' is already assigned to a node with a different MAC address.");
            }

            var sameMacDifferentIp = existingList
                .Where(n => NormalizeMac(n.MacAddress) == mac && NormalizeIp(n.IPAddress) != ip)
                .ToList();

            if (sameMacDifferentIp.Count > 0)
            {
                return Ambiguous(
                    discovered,
                    sameMacDifferentIp,
                    "MacMoved",
                    0.7,
                    $"MAC address '{discovered.MacAddress}' is known on a node with a different IP address.");
            }
        }

        // Rule 6: Name + type + parent container. Parent is mandatory: this rule
        // must never degrade into a hostname-only match.
        var nodeType = ParseNodeType(discovered.NodeType);
        var parentExternalId = GetDiscoveredParentExternalId(discovered);
        var parentName = discovered.ParentName?.Trim() ?? string.Empty;

        if (shortName.Length > 0 && (parentExternalId.Length > 0 || parentName.Length > 0))
        {
            var matches = existingList
                .Where(n => n.NodeType == nodeType)
                .Where(n => ShortName(n).Equals(shortName, StringComparison.OrdinalIgnoreCase))
                .Where(n => ParentMatches(n, parentExternalId, parentName))
                .ToList();

            if (matches.Count == 1)
            {
                var reason = $"Name + type + parent match for '{discovered.Name}' in '{parentName}'. Confidence below auto-merge threshold; review required.";
                return Result(
                    ReconciliationConfidence.ProbableNameParentMatch,
                    CreateNewNode(discovered),
                    isNew: true,
                    requiresReview: true,
                    reason: reason,
                    matchKey: "NameTypeParent",
                    score: 0.78,
                    candidates: matches.Select(n => Candidate(n, "NameTypeParent", 0.78, "Name + type + parent")),
                    changed: true);
            }

            if (matches.Count > 1)
            {
                return Ambiguous(
                    discovered,
                    matches,
                    "NameTypeParent",
                    0.78,
                    $"{matches.Count} nodes share name, type and parent '{parentName}'.");
            }
        }

        // Rule 7: short-hostname collision. This is intentionally a review path,
        // never an automatic merge (ADR-002 / graph model section 6.3).
        if (shortName.Length > 0)
        {
            var collisions = existingList
                .Where(n => ShortName(n).Equals(shortName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (collisions.Count > 0)
            {
                var detail = string.Join(", ", collisions.Take(3).Select(n =>
                    string.IsNullOrWhiteSpace(n.FQDN) ? n.Name : n.FQDN));
                return Ambiguous(
                    discovered,
                    collisions,
                    "HostnameCollision",
                    0.35,
                    $"Short hostname collision '{shortName}' with {detail}. Different domains/identities are not auto-merged.");
            }
        }

        // Rule 8: truly new entity.
        var newNode = CreateNewNode(discovered);
        return Result(
            ReconciliationConfidence.NewEntity,
            newNode,
            isNew: true,
            requiresReview: false,
            reason: "No stable identity match found; created a new entity.",
            matchKey: "None",
            score: 0.0,
            candidates: Array.Empty<ReconciliationCandidate>(),
            changed: true);
    }

    private static ReconciliationResult Ambiguous(
        DiscoveredNodeContract discovered,
        InfrastructureNode candidate,
        string matchKey,
        double score,
        string reason)
    {
        return Ambiguous(discovered, new[] { candidate }, matchKey, score, reason);
    }

    private static ReconciliationResult Ambiguous(
        DiscoveredNodeContract discovered,
        IReadOnlyList<InfrastructureNode> candidates,
        string matchKey,
        double score,
        string reason)
    {
        var newNode = CreateNewNode(discovered);
        return Result(
            ReconciliationConfidence.AmbiguousConflict,
            newNode,
            isNew: true,
            requiresReview: true,
            reason: reason,
            matchKey: matchKey,
            score: score,
            candidates: candidates.Select(n => Candidate(n, matchKey, score, reason)),
            changed: true);
    }

    private static ReconciliationResult Result(
        ReconciliationConfidence confidence,
        InfrastructureNode target,
        bool isNew,
        bool requiresReview,
        string reason,
        string matchKey,
        double score,
        IEnumerable<ReconciliationCandidate> candidates,
        bool changed)
    {
        var candidateList = candidates.ToList();
        if (candidateList.Count == 0 && !isNew)
            candidateList.Add(Candidate(target, matchKey, score, reason));

        return new ReconciliationResult
        {
            Confidence = confidence,
            TargetNode = target,
            IsNew = isNew,
            RequiresReview = requiresReview || confidence == ReconciliationConfidence.ProbableNameParentMatch,
            MetadataChanged = changed,
            Reason = reason,
            MatchKey = matchKey,
            MatchScore = score,
            Candidates = candidateList
        };
    }

    private static ReconciliationCandidate Candidate(
        InfrastructureNode node,
        string matchKey,
        double score,
        string reason) => new()
    {
        NodeId = node.Id,
        Name = node.Name,
        FQDN = node.FQDN,
        MatchKey = matchKey,
        Score = score,
        Reason = reason
    };

    private static InfrastructureNode CreateNewNode(DiscoveredNodeContract discovered)
    {
        var node = new InfrastructureNode
        {
            Id = Guid.NewGuid(),
            Name = discovered.Name,
            FQDN = discovered.FQDN,
            IPAddress = discovered.IPAddress,
            MacAddress = discovered.MacAddress,
            NodeType = ParseNodeType(discovered.NodeType),
            Status = NodeStatusRules.Normalize(discovered.Status),
            DiscoverySource = ResolveSourceName(discovered),
            BiosUuid = discovered.BiosUuid,
            VmInstanceUuid = discovered.VmInstanceUuid,
            DomainSid = discovered.DomainSid,
            FirstSeen = discovered.DiscoveredAt == default ? DateTime.UtcNow : discovered.DiscoveredAt,
            LastSeen = DateTime.UtcNow,
            PropertiesJson = "{}"
        };

        MergeDiscoveredMetadata(node, discovered, "Created from discovery", "NewEntity");
        return node;
    }

    /// <param name="refreshFromSameSource">
    /// True when the source is re-reporting its own object (matched through its
    /// stable source link). Its reported values are then current and replace the
    /// stored ones: metrics such as CpuUsagePercent or PowerState, and — while
    /// the node has no other source — a renamed or re-addressed object's name,
    /// FQDN, IP and MAC. Merged multi-source nodes keep their identity fields so
    /// two sources that spell a name differently do not overwrite each other on
    /// every run. Any other match only fills blanks.
    /// </param>
    private static bool MergeDiscoveredMetadata(
        InfrastructureNode existing,
        DiscoveredNodeContract discovered,
        string evidence,
        string confidence,
        bool refreshFromSameSource = false)
    {
        var before = SnapshotComparable(existing);

        existing.LastSeen = DateTime.UtcNow;
        existing.IsStale = false;
        existing.StaleSince = null;

        var reportingSource = ResolveSourceName(discovered);
        var existingSources = existing.DiscoverySource
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var refreshIdentity = refreshFromSameSource &&
                              (existingSources.Length == 0 ||
                               (existingSources.Length == 1 &&
                                existingSources[0].Equals(reportingSource, StringComparison.OrdinalIgnoreCase)));

        string? Merge(string? current, string? reported) =>
            !string.IsNullOrWhiteSpace(reported) && (refreshIdentity || string.IsNullOrWhiteSpace(current))
                ? reported
                : current;

        existing.Name = Merge(existing.Name, discovered.Name) ?? string.Empty;
        existing.FQDN = Merge(existing.FQDN, discovered.FQDN) ?? string.Empty;
        existing.IPAddress = Merge(existing.IPAddress, discovered.IPAddress);
        existing.MacAddress = Merge(existing.MacAddress, discovered.MacAddress);

        if (string.IsNullOrWhiteSpace(existing.BiosUuid) && !string.IsNullOrWhiteSpace(discovered.BiosUuid))
            existing.BiosUuid = discovered.BiosUuid;

        if (string.IsNullOrWhiteSpace(existing.VmInstanceUuid) && !string.IsNullOrWhiteSpace(discovered.VmInstanceUuid))
            existing.VmInstanceUuid = discovered.VmInstanceUuid;

        if (string.IsNullOrWhiteSpace(existing.DomainSid) && !string.IsNullOrWhiteSpace(discovered.DomainSid))
            existing.DomainSid = discovered.DomainSid;

        var discoveredType = ParseNodeType(discovered.NodeType);
        if (ShouldUpgradeNodeType(existing.NodeType, discoveredType))
            existing.NodeType = discoveredType;

        // The status of an existing node is not merged here: a node reported
        // by several sources shows the worst status across them, which only
        // ingestion can derive (from the status kept on each source link, see
        // NodeStatusRules). A new node starts with its first source's status.

        var sourceName = ResolveSourceName(discovered);
        if (!string.IsNullOrWhiteSpace(sourceName) &&
            !existing.DiscoverySource.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(s => s.Equals(sourceName, StringComparison.OrdinalIgnoreCase)))
        {
            existing.DiscoverySource = string.IsNullOrWhiteSpace(existing.DiscoverySource)
                ? sourceName
                : $"{existing.DiscoverySource} + {sourceName}";
        }

        MergeProperties(existing, discovered, evidence, confidence, refreshFromSameSource, refreshIdentity);
        return SnapshotComparable(existing) != before;
    }

    private static void MergeProperties(
        InfrastructureNode node,
        DiscoveredNodeContract discovered,
        string evidence,
        string confidence,
        bool refreshFromSameSource,
        bool refreshIdentity)
    {
        JsonObject props;
        try
        {
            props = JsonNode.Parse(string.IsNullOrWhiteSpace(node.PropertiesJson) ? "{}" : node.PropertiesJson) as JsonObject
                ?? new JsonObject();
        }
        catch (JsonException)
        {
            props = new JsonObject();
        }

        foreach (var (key, value) in discovered.Properties)
        {
            if (string.IsNullOrWhiteSpace(key) || ParentKeys.Contains(key))
                continue;

            // A source re-reporting its own object refreshes every value it
            // reports (live metrics must not freeze at the first discovery);
            // a match from another source only adds what is missing.
            if (refreshFromSameSource || key.StartsWith("_", StringComparison.Ordinal) || !props.ContainsKey(key))
                props[key] = value;
        }

        // The parent is part of the node's identity (rule 6 compares it), and
        // two sources of one node report different parents (an AD OU, a
        // vSphere host). Like name and FQDN it is replaced only while the node
        // has this single source; otherwise the first reported parent stays,
        // so the sources do not overwrite each other on every run.
        void MergeParent(string key, string? reported)
        {
            if (string.IsNullOrWhiteSpace(reported))
                return;
            var current = props[key] is JsonValue currentValue && currentValue.TryGetValue<string>(out var text)
                ? text
                : null;
            if (refreshIdentity || string.IsNullOrWhiteSpace(current))
                props[key] = reported;
        }

        MergeParent("ParentExternalId", !string.IsNullOrWhiteSpace(discovered.ParentExternalId)
            ? discovered.ParentExternalId
            : discovered.Properties.GetValueOrDefault("ParentExternalId"));
        MergeParent("ParentName", !string.IsNullOrWhiteSpace(discovered.ParentName)
            ? discovered.ParentName
            : discovered.Properties.GetValueOrDefault("ParentName"));

        props["_lastReconciliationEvidence"] = evidence;
        props["_lastReconciliationConfidence"] = confidence;
        props["_lastReconciliationAt"] = DateTime.UtcNow.ToString("O");

        if (!string.IsNullOrWhiteSpace(discovered.SourceSystem))
        {
            var systems = props["_sourceSystems"] as JsonArray ?? new JsonArray();
            if (!systems.Any(x => string.Equals(x?.GetValue<string>(), discovered.SourceSystem, StringComparison.OrdinalIgnoreCase)))
                systems.Add(discovered.SourceSystem);
            props["_sourceSystems"] = systems;
        }

        node.PropertiesJson = props.ToJsonString();
    }

    private static readonly HashSet<string> ParentKeys = new(StringComparer.Ordinal) { "ParentExternalId", "ParentName" };

    private static string SnapshotComparable(InfrastructureNode node)
    {
        return string.Join('\u001f',
            node.Name,
            node.FQDN,
            node.IPAddress,
            node.MacAddress,
            node.NodeType,
            node.Status,
            node.DiscoverySource,
            node.BiosUuid,
            node.VmInstanceUuid,
            node.DomainSid,
            node.IsStale,
            ComparableProperties(node.PropertiesJson));
    }

    /// <summary>
    /// Reconciliation bookkeeping is refreshed on every run. It is excluded from
    /// change detection so an unchanged re-discovery is reported as unchanged.
    /// </summary>
    private static string ComparableProperties(string propertiesJson)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(propertiesJson) ? "{}" : propertiesJson);
            if (node is JsonObject obj)
            {
                foreach (var key in obj.Select(kv => kv.Key)
                             .Where(k => k.StartsWith("_lastReconciliation", StringComparison.Ordinal))
                             .ToList())
                {
                    obj.Remove(key);
                }

                return obj.ToJsonString();
            }

            return node?.ToJsonString() ?? "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private static bool ShouldUpgradeNodeType(NodeType existing, NodeType discovered)
    {
        if (existing == discovered)
            return false;

        // Never downgrade a specific VM/DC classification to a generic computer.
        if (existing is NodeType.DomainController or NodeType.VirtualMachine &&
            discovered is NodeType.Computer or NodeType.PhysicalServer)
            return false;

        if (existing is NodeType.Computer or NodeType.PhysicalServer &&
            discovered is NodeType.VirtualMachine or NodeType.DomainController)
            return true;

        if (existing is NodeType.VirtualMachine && discovered is NodeType.DomainController)
            return true;

        return false;
    }

    private static bool HasHardwareConflict(
        DiscoveredNodeContract discovered,
        InfrastructureNode existing,
        out string conflict)
    {
        var bios = NormalizeUuid(discovered.BiosUuid);
        var existingBios = NormalizeUuid(existing.BiosUuid);
        if (bios.Length > 0 && existingBios.Length > 0 && bios != existingBios)
        {
            conflict = $"Conflicting BIOS UUIDs ({discovered.BiosUuid} vs {existing.BiosUuid}).";
            return true;
        }

        var vmUuid = NormalizeUuid(discovered.VmInstanceUuid);
        var existingVmUuid = NormalizeUuid(existing.VmInstanceUuid);
        if (vmUuid.Length > 0 && existingVmUuid.Length > 0 && vmUuid != existingVmUuid)
        {
            conflict = $"Conflicting VM instance UUIDs ({discovered.VmInstanceUuid} vs {existing.VmInstanceUuid}).";
            return true;
        }

        var sid = NormalizeSid(discovered.DomainSid);
        var existingSid = NormalizeSid(existing.DomainSid);
        if (sid.Length > 0 && existingSid.Length > 0 && sid != existingSid)
        {
            conflict = $"Conflicting domain SIDs ({discovered.DomainSid} vs {existing.DomainSid}).";
            return true;
        }

        conflict = string.Empty;
        return false;
    }

    private static bool ParentMatches(InfrastructureNode node, string parentExternalId, string parentName)
    {
        if (parentExternalId.Length == 0 && parentName.Length == 0)
            return false;

        var nodeParentExternalId = GetNodeProperty(node, "ParentExternalId");
        var nodeParentName = GetNodeProperty(node, "ParentName");

        var externalMatches = parentExternalId.Length > 0 &&
                              nodeParentExternalId.Length > 0 &&
                              parentExternalId.Equals(nodeParentExternalId, StringComparison.OrdinalIgnoreCase);

        var nameMatches = parentName.Length > 0 &&
                          nodeParentName.Length > 0 &&
                          parentName.Equals(nodeParentName, StringComparison.OrdinalIgnoreCase);

        return externalMatches || nameMatches;
    }

    private static string GetDiscoveredParentExternalId(DiscoveredNodeContract discovered)
    {
        if (!string.IsNullOrWhiteSpace(discovered.ParentExternalId))
            return discovered.ParentExternalId.Trim();

        return discovered.Properties.TryGetValue("ParentExternalId", out var value)
            ? value?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static string GetNodeProperty(InfrastructureNode node, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(node.PropertiesJson) ? "{}" : node.PropertiesJson);
            return doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static NodeType ParseNodeType(string? value)
    {
        return Enum.TryParse<NodeType>(value, true, out var parsed)
            ? parsed
            : NodeType.VirtualMachine;
    }

    private static string ResolveSourceName(DiscoveredNodeContract discovered)
    {
        if (!string.IsNullOrWhiteSpace(discovered.SourceSystem))
            return discovered.SourceSystem.Trim();

        return discovered.Properties.TryGetValue("DiscoverySource", out var source) && !string.IsNullOrWhiteSpace(source)
            ? source.Trim()
            : "Collector";
    }

    private static string ShortName(DiscoveredNodeContract discovered)
    {
        var candidate = !string.IsNullOrWhiteSpace(discovered.Name)
            ? discovered.Name
            : discovered.FQDN;
        return ShortName(candidate);
    }

    private static string ShortName(InfrastructureNode node)
    {
        var candidate = !string.IsNullOrWhiteSpace(node.Name)
            ? node.Name
            : node.FQDN;
        return ShortName(candidate);
    }

    private static string ShortName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var trimmed = value.Trim().TrimEnd('.');
        var dot = trimmed.IndexOf('.');
        return (dot > 0 ? trimmed[..dot] : trimmed).ToLowerInvariant();
    }

    private static string NormalizeFqdn(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static string NormalizeUuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var trimmed = value.Trim().Trim('{', '}');
        return Guid.TryParse(trimmed, out var guid)
            ? guid.ToString("D")
            : trimmed.ToLowerInvariant();
    }

    private static string NormalizeSid(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }

    private static string NormalizeIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return IPAddress.TryParse(value.Trim(), out var address)
            ? address.ToString()
            : value.Trim().ToLowerInvariant();
    }

    private static string NormalizeMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var chars = value.Where(Uri.IsHexDigit).Select(char.ToLowerInvariant).ToArray();
        return chars.Length == 12 ? new string(chars) : value.Trim().ToLowerInvariant();
    }
}

/// <summary>
/// Status of a node that several sources report. Each source link keeps the
/// status its source last reported (<see cref="ReportedStatusKey"/> in the
/// link's properties) and the node shows the worst of them across its live
/// links. Two sources that disagree (vCenter "warning", AD "healthy") then no
/// longer flip the node on every run, and StatusChangedAt marks only a change
/// of that effective status.
/// </summary>
public static class NodeStatusRules
{
    public const string ReportedStatusKey = "_status";

    public static string Normalize(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "healthy" or "ok" or "online" or "poweredon" or "powered_on" => "healthy",
            "warning" or "degraded" or "suspended" => "warning",
            "critical" or "error" or "offline" or "poweredoff" or "powered_off" => "critical",
            _ => "unknown"
        };
    }

    /// <summary>
    /// The worst reported status, or null when no source reported one.
    /// "unknown" carries no information, so it only wins when nothing else was reported.
    /// </summary>
    public static string? Effective(IEnumerable<string?> reportedStatuses) => reportedStatuses
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Select(Normalize)
        .OrderByDescending(Severity)
        .FirstOrDefault();

    /// <summary>The link properties to persist: the source's properties plus its normalised status.</summary>
    public static string LinkProperties(IReadOnlyDictionary<string, string> properties, string? reportedStatus)
    {
        var copy = new Dictionary<string, string>(properties);
        if (string.IsNullOrWhiteSpace(reportedStatus))
            copy.Remove(ReportedStatusKey);
        else
            copy[ReportedStatusKey] = Normalize(reportedStatus);
        return JsonSerializer.Serialize(copy);
    }

    /// <summary>The status a source link recorded, or null for links written before it was kept.</summary>
    public static string? Reported(string? linkPropertiesJson)
    {
        if (string.IsNullOrWhiteSpace(linkPropertiesJson))
            return null;

        try
        {
            return JsonNode.Parse(linkPropertiesJson) is JsonObject props &&
                   props[ReportedStatusKey] is JsonValue value &&
                   value.TryGetValue<string>(out var status) &&
                   !string.IsNullOrWhiteSpace(status)
                ? status
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int Severity(string status) => status switch
    {
        "critical" => 3,
        "warning" => 2,
        "healthy" => 1,
        _ => 0
    };
}
