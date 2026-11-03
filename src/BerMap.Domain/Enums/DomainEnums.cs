namespace BerMap.Domain.Enums;

public enum NodeType
{
    Datacenter,
    Site,
    Forest,
    Domain,
    OrganizationalUnit,
    Cluster,
    Hypervisor,
    VirtualMachine,
    PhysicalServer,
    Computer,
    OperatingSystem,
    Application,
    Service,
    Database,
    DomainController,
    Storage,
    Datastore,
    NetworkDevice,
    Network,
    Endpoint,
    ResourcePool,
    Group
}

public enum RelationshipType
{
    HostedOn,
    ConnectedTo,
    DependsOn,
    MemberOf,
    RunsOn,
    UsesDatabase,
    ReplicatesTo,
    ManagedBy,
    ConnectsThrough,
    ProtectedBy,
    Contains,
    Trusts,
    UsesStorage,
    MountedOn,
    PossibleDuplicate
}

public enum ConfidenceLevel
{
    Verified,
    Observed,
    Inferred
}

public enum IntegrationStatus
{
    Active,
    Inactive,
    Error,
    Testing,
    Degraded
}

public enum CollectorStatus
{
    Online,
    Offline,
    Error,
    Registering
}

public enum SimulationStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum DiscoveryJobStatus
{
    Queued,
    Running,
    Completed,
    PartialSuccess,
    Failed,
    Cancelled
}

public enum ReconciliationReviewStatus
{
    Pending,
    Merged,
    Dismissed
}

public enum UserRole
{
    PlatformAdministrator,
    IntegrationAdministrator,
    InfrastructureOperator,
    SecurityAuditor,
    ReadOnlyViewer
}
