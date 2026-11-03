using System;
using System.Collections.Generic;
using System.Text.Json;
using BerMap.Contracts;

namespace BerMap.Application.DTOs;

/* ===== Adapter descriptors ===== */

public sealed class AdapterDescriptorDto
{
    public string AdapterType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public AdapterCapabilities Capabilities { get; set; } = new();
    public AdapterConfigurationSchema ConfigurationSchema { get; set; } = new();
}

/* ===== Integrations ===== */

public sealed class IntegrationDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AdapterType { get; set; } = string.Empty;
    public string AdapterVersion { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string ScheduleCron { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; }
    public int ConfigurationVersion { get; set; }

    /// <summary>Non-secret configuration only. Secret fields are never returned by the API.</summary>
    public JsonElement Configuration { get; set; }

    public string Endpoint { get; set; } = string.Empty;
    public DateTime? LastSyncAt { get; set; }
    public DateTime? LastTestAt { get; set; }
    public bool? LastTestSuccess { get; set; }
    public string LastError { get; set; } = string.Empty;
    public DateTime? LastErrorAt { get; set; }
    public int DiscoveredEntitiesCount { get; set; }
    public Guid? LastJobId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public AdapterDescriptorDto? Adapter { get; set; }
}

public sealed class CreateIntegrationRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AdapterType { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> Configuration { get; set; } = new();
    public string? Secret { get; set; }
    public string ScheduleCron { get; set; } = "0 */6 * * *";
    public int TimeoutSeconds { get; set; } = 1800;
    public bool IsEnabled { get; set; } = true;
}

public sealed class UpdateIntegrationRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, JsonElement>? Configuration { get; set; }
    public string? Secret { get; set; }
    public bool ClearSecret { get; set; }
    public string? ScheduleCron { get; set; }
    public int? TimeoutSeconds { get; set; }
    public bool? IsEnabled { get; set; }
}

public sealed class TestConnectionRequest
{
    public string AdapterType { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> Configuration { get; set; } = new();
    public string? Secret { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class TestExistingIntegrationRequest
{
    /// <summary>Optional secret override for testing before rotating the stored credential.</summary>
    public string? Secret { get; set; }
}

/* ===== Discovery jobs ===== */

public sealed class DiscoveryJobDto
{
    public Guid Id { get; set; }
    public Guid IntegrationId { get; set; }
    public string IntegrationName { get; set; } = string.Empty;
    public string AdapterType { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int EntitiesDiscovered { get; set; }
    public int RelationshipsDiscovered { get; set; }
    public int ProgressPercent { get; set; }
    public string CurrentPhase { get; set; } = string.Empty;
    public string CurrentMessage { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public long? DurationMs { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public Dictionary<string, int> Statistics { get; set; } = new();
}

public sealed class StartDiscoveryRequest
{
    public string JobType { get; set; } = "Full";
    public string? RequestedBy { get; set; }
}

/* ===== Inventory & reconciliation ===== */

public sealed class InventoryQueryRequest
{
    public string? Query { get; set; }
    public string? NodeType { get; set; }
    public string? Status { get; set; }
    public bool? IsStale { get; set; }
    public bool? NeedsReview { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public sealed class InventoryPageDto
{
    public List<InventoryNodeDto> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
    public int ReconciledCount { get; set; }
    public int SingleSourceCount { get; set; }
    public int PendingReviewCount { get; set; }
}

public sealed class InventoryNodeDto
{
    public Guid Id { get; set; }
    public string NodeType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FQDN { get; set; } = string.Empty;
    public string? IPAddress { get; set; }
    public string? MacAddress { get; set; }
    public string Status { get; set; } = string.Empty;
    public string DiscoverySource { get; set; } = string.Empty;
    public string BiosUuid { get; set; } = string.Empty;
    public string VmInstanceUuid { get; set; } = string.Empty;
    public string DomainSid { get; set; } = string.Empty;
    public bool IsStale { get; set; }
    public DateTime? StaleSince { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public int SourceCount { get; set; }
    public List<string> SourceSystems { get; set; } = new();
    public Dictionary<string, JsonElement> Properties { get; set; } = new();
    public bool NeedsReview { get; set; }
}

public sealed class InventoryNodeDetailDto
{
    public InventoryNodeDto Node { get; set; } = new();
    public List<NodeSourceLinkDto> SourceLinks { get; set; } = new();
    public List<InventoryRelationshipDto> Relationships { get; set; } = new();
    public List<ReconciliationReviewDto> Reviews { get; set; } = new();
}

public sealed class InventoryRelationshipDto
{
    public Guid Id { get; set; }
    public Guid SourceNodeId { get; set; }
    public string SourceNodeName { get; set; } = string.Empty;
    public Guid TargetNodeId { get; set; }
    public string TargetNodeName { get; set; } = string.Empty;
    public string RelationshipType { get; set; } = string.Empty;
    public string ConfidenceLevel { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public bool IsStale { get; set; }
}

public sealed class NodeSourceLinkDto
{
    public Guid Id { get; set; }
    public Guid IntegrationId { get; set; }
    public string IntegrationName { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty;
    public bool IsStale { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}

public sealed class ReconciliationReviewDto
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public Guid? IntegrationId { get; set; }
    public string IntegrationName { get; set; } = string.Empty;
    public Guid? DiscoveryJobId { get; set; }
    public Guid? CreatedNodeId { get; set; }
    public string CreatedNodeName { get; set; } = string.Empty;
    public Guid? CandidateNodeId { get; set; }
    public string CandidateNodeName { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string MatchKey { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;
    public double MatchScore { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string CandidateSummary { get; set; } = string.Empty;
    public JsonElement DiscoveredNode { get; set; }
    public List<Guid> CandidateNodeIds { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string ReviewedBy { get; set; } = string.Empty;
    public string ResolutionNotes { get; set; } = string.Empty;
}

public sealed class ResolveReconciliationReviewRequest
{
    /// <summary>"Merged" or "Dismissed".</summary>
    public string Resolution { get; set; } = string.Empty;
    public Guid? TargetNodeId { get; set; }
    public string? Notes { get; set; }
}

/* ===== Snapshots ===== */

public sealed class CreateSnapshotRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? CreatedBy { get; set; }
    public Guid? SourceJobId { get; set; }
}

public sealed class SnapshotSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public int NodeCount { get; set; }
    public int RelationshipCount { get; set; }
    public bool IsImmutable { get; set; }
    public string Hash { get; set; } = string.Empty;
    public long SnapshotVersion { get; set; }
    public Guid? SourceJobId { get; set; }
    public DateTime GraphCapturedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public sealed class SnapshotNodeDto
{
    public Guid Id { get; set; }
    public Guid NodeId { get; set; }
    public string NodeType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FQDN { get; set; } = string.Empty;
    public string? IPAddress { get; set; }
    public string? MacAddress { get; set; }
    public string Status { get; set; } = string.Empty;
    public string DiscoverySource { get; set; } = string.Empty;
    public string BiosUuid { get; set; } = string.Empty;
    public string VmInstanceUuid { get; set; } = string.Empty;
    public string DomainSid { get; set; } = string.Empty;
    public bool IsStale { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public Dictionary<string, JsonElement> Properties { get; set; } = new();
}

public sealed class SnapshotRelationshipDto
{
    public Guid Id { get; set; }
    public Guid RelationshipId { get; set; }
    public Guid SourceNodeId { get; set; }
    public string SourceNodeName { get; set; } = string.Empty;
    public Guid TargetNodeId { get; set; }
    public string TargetNodeName { get; set; } = string.Empty;
    public string RelationshipType { get; set; } = string.Empty;
    public string ConfidenceLevel { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public bool IsStale { get; set; }
}

public sealed class SnapshotGraphDto
{
    public SnapshotSummaryDto Snapshot { get; set; } = new();
    public List<SnapshotNodeDto> Nodes { get; set; } = new();
    public List<SnapshotRelationshipDto> Relationships { get; set; } = new();
}

public sealed class SnapshotDiffDto
{
    public Guid SnapshotAId { get; set; }
    public string SnapshotAName { get; set; } = string.Empty;
    public Guid SnapshotBId { get; set; }
    public string SnapshotBName { get; set; } = string.Empty;
    public int AddedNodesCount { get; set; }
    public int RemovedNodesCount { get; set; }
    public int ChangedNodesCount { get; set; }
    public int AddedRelationshipsCount { get; set; }
    public int RemovedRelationshipsCount { get; set; }
    public int ChangedRelationshipsCount { get; set; }
    public List<SnapshotNodeChangeDto> AddedNodes { get; set; } = new();
    public List<SnapshotNodeChangeDto> RemovedNodes { get; set; } = new();
    public List<SnapshotNodeFieldChangeDto> ChangedNodes { get; set; } = new();
    public List<SnapshotRelationshipChangeDto> AddedRelationships { get; set; } = new();
    public List<SnapshotRelationshipChangeDto> RemovedRelationships { get; set; } = new();
    public List<SnapshotRelationshipChangeDto> ChangedRelationships { get; set; } = new();
    public List<string> Highlights { get; set; } = new();
}

public sealed class SnapshotNodeChangeDto
{
    public Guid NodeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FQDN { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty;
    public string? IPAddress { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class SnapshotNodeFieldChangeDto
{
    public Guid NodeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty;
    public List<FieldChangeDto> Changes { get; set; } = new();
}

public sealed class FieldChangeDto
{
    public string Field { get; set; } = string.Empty;
    public string? From { get; set; }
    public string? To { get; set; }
}

public sealed class SnapshotRelationshipChangeDto
{
    public Guid RelationshipId { get; set; }
    public string SourceNodeName { get; set; } = string.Empty;
    public string TargetNodeName { get; set; } = string.Empty;
    public string RelationshipType { get; set; } = string.Empty;
    public List<FieldChangeDto> Changes { get; set; } = new();
}

public sealed class SnapshotVerificationDto
{
    public Guid SnapshotId { get; set; }
    public string SnapshotName { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public string StoredHash { get; set; } = string.Empty;
    public string ComputedHash { get; set; } = string.Empty;
    public int StoredNodeCount { get; set; }
    public int ActualNodeCount { get; set; }
    public int StoredRelationshipCount { get; set; }
    public int ActualRelationshipCount { get; set; }
    public List<string> Differences { get; set; } = new();
    public DateTime VerifiedAt { get; set; } = DateTime.UtcNow;
}

/* ===== Ingestion ===== */

public sealed class IngestionResult
{
    public int NodesCreated { get; set; }
    public int NodesUpdated { get; set; }
    public int NodesUnchanged { get; set; }
    public int RelationshipsCreated { get; set; }
    public int RelationshipsUpdated { get; set; }
    public int ReviewsCreated { get; set; }
    public int ReviewsUpdated { get; set; }
    public int SourceLinksCreated { get; set; }
    public int SourceLinksStale { get; set; }
    public int NodesMarkedStale { get; set; }
    public int RelationshipsMarkedStale { get; set; }
    public List<string> Warnings { get; set; } = new();
}
