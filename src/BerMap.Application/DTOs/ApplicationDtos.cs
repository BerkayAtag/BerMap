using System;
using System.Collections.Generic;

namespace BerMap.Application.DTOs;

public class DashboardSummaryDto
{
    public int TotalEntities { get; set; }
    public int HealthyEntities { get; set; }
    public int WarningEntities { get; set; }
    public int CriticalEntities { get; set; }
    public int UnknownEntities { get; set; }
    public int StaleEntities { get; set; }
    public int ActiveIntegrations { get; set; }
    public int TotalIntegrations { get; set; }
    public int PendingReconciliationReviews { get; set; }
    public int CriticalDependenciesCount { get; set; }
    public int RecentDiscoveries { get; set; }
    public DateTime? LastDiscoveryAt { get; set; }
    public List<RecentChangeDto> RecentChanges { get; set; } = new();
}

public class RecentChangeDto
{
    public Guid Id { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Status { get; set; } = "healthy";
    public DateTime Timestamp { get; set; }
}

public class InfrastructureNodeDto
{
    public Guid Id { get; set; }
    public string NodeType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FQDN { get; set; } = string.Empty;
    public string? IPAddress { get; set; }
    public string? MacAddress { get; set; }
    public string Status { get; set; } = "healthy";
    public string DiscoverySource { get; set; } = string.Empty;
    public string BiosUuid { get; set; } = string.Empty;
    public string VmInstanceUuid { get; set; } = string.Empty;
    public string DomainSid { get; set; } = string.Empty;
    public bool IsStale { get; set; }
    public DateTime? StaleSince { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public List<string> SourceSystems { get; set; } = new();
    public Dictionary<string, string> Properties { get; set; } = new();
}

public class InfrastructureRelationshipDto
{
    public Guid Id { get; set; }
    public Guid SourceNodeId { get; set; }
    public string SourceNodeName { get; set; } = string.Empty;
    public Guid TargetNodeId { get; set; }
    public string TargetNodeName { get; set; } = string.Empty;
    public string RelationshipType { get; set; } = string.Empty;
    public string ConfidenceLevel { get; set; } = "Verified";
    public string Evidence { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public bool IsStale { get; set; }
}

public class GraphQueryResultDto
{
    public List<InfrastructureNodeDto> Nodes { get; set; } = new();
    public List<InfrastructureRelationshipDto> Relationships { get; set; } = new();

    /// <summary>True when the graph had more nodes than the response cap.</summary>
    public bool IsTruncated { get; set; }
}

public class SimulationRunRequest
{
    public Guid TargetNodeId { get; set; }
    public string ScenarioType { get; set; } = "host_failure"; // host_failure, datastore_outage, network_cut
    public Guid? SnapshotId { get; set; }
}

public class ImpactAnalysisResultDto
{
    public Guid TargetNodeId { get; set; }
    public string TargetNodeName { get; set; } = string.Empty;
    public string ScenarioType { get; set; } = string.Empty;
    public int RiskScore { get; set; } // 0-100
    public List<ImpactedEntityDto> ConfirmedImpacts { get; set; } = new();
    public List<ImpactedEntityDto> ProbableImpacts { get; set; } = new();
    public List<ImpactedEntityDto> PossibleImpacts { get; set; } = new();
    public ClusterHeadroomDto ClusterHeadroom { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

public class ImpactedEntityDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string ImpactLevel { get; set; } = "Confirmed";
    public string DependencyPath { get; set; } = string.Empty;
}

public class ClusterHeadroomDto
{
    public double RemainingCpuPercent { get; set; }
    public double RemainingRamPercent { get; set; }
    public bool HasCapacityForFailover { get; set; }
    public string BottleneckResource { get; set; } = "None";
}
