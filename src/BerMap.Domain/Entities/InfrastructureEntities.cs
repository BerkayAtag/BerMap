using System;
using System.Collections.Generic;
using BerMap.Domain.Enums;

namespace BerMap.Domain.Entities;

public class InfrastructureNode : Entity
{
    public NodeType NodeType { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FQDN { get; set; } = string.Empty;
    public string? IPAddress { get; set; }
    public string? MacAddress { get; set; }
    public string Status { get; set; } = "healthy"; // healthy, warning, critical, unknown
    public string DiscoverySource { get; set; } = string.Empty; // ActiveDirectory, VMware, Zabbix, SCCM
    public string PropertiesJson { get; set; } = "{}"; // JSONB dynamic metadata
    public string BiosUuid { get; set; } = string.Empty;
    public string VmInstanceUuid { get; set; } = string.Empty;
    public string DomainSid { get; set; } = string.Empty;
    public bool IsStale { get; set; }
    public DateTime? StaleSince { get; set; }
    public Guid? LastSeenJobId { get; set; }
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When discovery last reported a different status than the stored one.
    /// Null until the status changes for the first time after discovery.
    /// </summary>
    public DateTime? StatusChangedAt { get; set; }

    public long SnapshotVersion { get; set; } = 1;

    // Navigation properties for relationships
    public ICollection<InfrastructureRelationship> OutgoingRelationships { get; set; } = new List<InfrastructureRelationship>();
    public ICollection<InfrastructureRelationship> IncomingRelationships { get; set; } = new List<InfrastructureRelationship>();
    public ICollection<NodeSourceLink> SourceLinks { get; set; } = new List<NodeSourceLink>();
}

public class InfrastructureRelationship : Entity
{
    public Guid SourceNodeId { get; set; }
    public InfrastructureNode SourceNode { get; set; } = null!;

    public Guid TargetNodeId { get; set; }
    public InfrastructureNode TargetNode { get; set; } = null!;

    public RelationshipType RelationshipType { get; set; }
    public ConfidenceLevel ConfidenceLevel { get; set; } = ConfidenceLevel.Verified;
    public string Evidence { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public Guid? IntegrationId { get; set; }
    public string PropertiesJson { get; set; } = "{}";
    public Guid? LastSeenJobId { get; set; }
    public bool IsStale { get; set; }
    public DateTime? StaleSince { get; set; }
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public long SnapshotVersion { get; set; } = 1;
}

public class Snapshot : Entity
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public int NodeCount { get; set; }
    public int RelationshipCount { get; set; }
    public bool IsImmutable { get; set; } = true;

    /// <summary>SHA-256 of the canonical captured graph. Used to detect tampering.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Monotonic graph version assigned at capture time.</summary>
    public long SnapshotVersion { get; set; } = 1;

    public Guid? SourceJobId { get; set; }
    public DateTime GraphCapturedAt { get; set; } = DateTime.UtcNow;
    public string WarningsJson { get; set; } = "[]";

    public ICollection<SnapshotNode> Nodes { get; set; } = new List<SnapshotNode>();
    public ICollection<SnapshotRelationship> Relationships { get; set; } = new List<SnapshotRelationship>();
}

/// <summary>
/// Immutable copy of an InfrastructureNode at snapshot capture time.
/// Rows are insert-only; the snapshot service never updates or deletes them.
/// </summary>
public class SnapshotNode : Entity
{
    public Guid SnapshotId { get; set; }
    public Snapshot Snapshot { get; set; } = null!;

    /// <summary>Original InfrastructureNode.Id at capture time.</summary>
    public Guid NodeId { get; set; }

    public NodeType NodeType { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FQDN { get; set; } = string.Empty;
    public string? IPAddress { get; set; }
    public string? MacAddress { get; set; }
    public string Status { get; set; } = "healthy";
    public string DiscoverySource { get; set; } = string.Empty;
    public string PropertiesJson { get; set; } = "{}";
    public string BiosUuid { get; set; } = string.Empty;
    public string VmInstanceUuid { get; set; } = string.Empty;
    public string DomainSid { get; set; } = string.Empty;
    public bool IsStale { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public long GraphVersion { get; set; }
}

/// <summary>
/// Immutable copy of an InfrastructureRelationship at snapshot capture time.
/// </summary>
public class SnapshotRelationship : Entity
{
    public Guid SnapshotId { get; set; }
    public Snapshot Snapshot { get; set; } = null!;

    /// <summary>Original InfrastructureRelationship.Id at capture time.</summary>
    public Guid RelationshipId { get; set; }

    /// <summary>Original node ids. Snapshot diffing uses these together with node match keys.</summary>
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }

    public RelationshipType RelationshipType { get; set; }
    public ConfidenceLevel ConfidenceLevel { get; set; } = ConfidenceLevel.Verified;
    public string Evidence { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public bool IsStale { get; set; }
    public string PropertiesJson { get; set; } = "{}";
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public long GraphVersion { get; set; }
}

/// <summary>
/// Maps a source-system identity to a reconciled BerMap node. This is the stable
/// link used to avoid duplicate nodes when a source has no FQDN or hardware UUID.
/// </summary>
public class NodeSourceLink : Entity
{
    public Guid IntegrationId { get; set; }
    public Integration Integration { get; set; } = null!;

    public Guid NodeId { get; set; }
    public InfrastructureNode Node { get; set; } = null!;

    public string ExternalId { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public NodeType NodeType { get; set; }
    public Guid? LastSeenJobId { get; set; }
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public bool IsStale { get; set; }
    public string PropertiesJson { get; set; } = "{}";
}

public class Simulation : Entity
{
    public string Name { get; set; } = string.Empty;
    public Guid SnapshotId { get; set; }
    public string ScenarioType { get; set; } = string.Empty; // host_failure, datastore_outage, network_cut
    public string ParametersJson { get; set; } = "{}";
    public SimulationStatus Status { get; set; } = SimulationStatus.Pending;
    public string ResultsJson { get; set; } = "{}";
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? CompletedAt { get; set; }
}

public class Integration : Entity
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AdapterType { get; set; } = string.Empty; // ActiveDirectory, VMware, Zabbix, SCCM
    public string AdapterVersion { get; set; } = string.Empty;
    public IntegrationStatus Status { get; set; } = IntegrationStatus.Active;
    public string ConfigurationJson { get; set; } = "{}";
    public int ConfigurationVersion { get; set; } = 1;
    public string CredentialReference { get; set; } = string.Empty;
    public DateTime? LastSyncAt { get; set; }
    public DateTime? LastTestAt { get; set; }
    public bool? LastTestSuccess { get; set; }
    public string LastError { get; set; } = string.Empty;
    public DateTime? LastErrorAt { get; set; }
    public int DiscoveredEntitiesCount { get; set; }
    public string ScheduleCron { get; set; } = "0 */6 * * *"; // every 6h default
    public int TimeoutSeconds { get; set; } = 1800;
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Data arrives from a remote collector. The server never tests or
    /// discovers such an integration itself, and the scheduler skips it.
    /// </summary>
    public bool CollectorManaged { get; set; }

    public Guid? LastJobId { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public class Collector : Entity
{
    public string Name { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string IPAddress { get; set; } = string.Empty;
    public CollectorStatus Status { get; set; } = CollectorStatus.Online;
    public string Version { get; set; } = "1.0.0";
    public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;
    public string SupportedAdaptersJson { get; set; } = "[]";
    public string QueueMetricsJson { get; set; } = "{}";
}

public class AuditLog : Entity
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
    public string IpAddress { get; set; } = string.Empty;
    public bool Success { get; set; } = true;
}

public class DiscoveryJob : Entity
{
    public Guid IntegrationId { get; set; }
    public Integration Integration { get; set; } = null!;
    public string JobType { get; set; } = "Full"; // Full, Incremental
    public DiscoveryJobStatus Status { get; set; } = DiscoveryJobStatus.Queued;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int EntitiesDiscovered { get; set; }
    public int RelationshipsDiscovered { get; set; }
    public int ProgressPercent { get; set; }
    public string CurrentPhase { get; set; } = string.Empty;
    public string CurrentMessage { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public long? DurationMs { get; set; }
    public string ErrorsJson { get; set; } = "[]";
    public string WarningsJson { get; set; } = "[]";
    public string StatisticsJson { get; set; } = "{}";
}

/// <summary>
/// Persistent review item created when reconciliation detects a possible duplicate.
/// The system never auto-merges on hostname alone; an administrator resolves it.
/// </summary>
public class ReconciliationReview : Entity
{
    public ReconciliationReviewStatus Status { get; set; } = ReconciliationReviewStatus.Pending;
    public Guid? IntegrationId { get; set; }
    public Guid? DiscoveryJobId { get; set; }
    public Guid? CreatedNodeId { get; set; }
    public Guid? CandidateNodeId { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public string MatchKey { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;
    public double MatchScore { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string DiscoveredNodeJson { get; set; } = "{}";
    public string CandidateNodeIdsJson { get; set; } = "[]";
    public string CandidateSummary { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public DateTime? ReviewedAt { get; set; }
    public string ReviewedBy { get; set; } = string.Empty;
    public string ResolutionNotes { get; set; } = string.Empty;
}
