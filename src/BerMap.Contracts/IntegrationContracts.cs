using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BerMap.Contracts;

/// <summary>
/// Core contract implemented by every infrastructure discovery adapter.
/// Adapters are read-only: they must never create, modify or delete objects in the
/// source system. They only execute search/read operations and return a normalized
/// graph snapshot to the BerMap ingestion pipeline.
/// </summary>
public interface IIntegrationAdapter
{
    /// <summary>Stable adapter type identifier, e.g. "ActiveDirectory" or "VMware".</summary>
    string AdapterType { get; }

    /// <summary>Human readable adapter name shown in the Web Console.</summary>
    string DisplayName { get; }

    /// <summary>Semantic adapter version.</summary>
    string Version { get; }

    AdapterCapabilities GetCapabilities();

    /// <summary>JSON Schema and secret field metadata used by the UI.</summary>
    AdapterConfigurationSchema GetConfigurationSchema();

    /// <summary>
    /// Verify connectivity, TLS trust and read-only authentication.
    /// Implementations must return a failed result instead of throwing for
    /// expected connectivity/authentication problems.
    /// </summary>
    Task<ConnectionTestResult> TestConnectionAsync(
        AdapterExecutionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a full discovery. The result is a complete, self-consistent graph
    /// fragment. Partial data must be returned together with errors/warnings and
    /// <see cref="DiscoveryResult.Success"/> set to false.
    /// </summary>
    Task<DiscoveryResult> DiscoverAsync(
        AdapterExecutionContext context,
        IProgress<DiscoveryProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runtime inputs supplied by the discovery orchestrator. Secrets are decrypted
/// from the credential vault immediately before the adapter call and are never
/// persisted in the integration configuration JSON.
/// </summary>
public sealed class AdapterExecutionContext
{
    public Guid IntegrationId { get; init; }
    public string IntegrationName { get; init; } = string.Empty;
    public string ConfigurationJson { get; init; } = "{}";

    /// <summary>Primary secret (password / API token). Null when the source allows anonymous read.</summary>
    public string? Secret { get; init; }

    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxConcurrency { get; init; } = 8;
}

public sealed class ConnectionTestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public TimeSpan Latency { get; set; }
    public string? ServerVersion { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDetail { get; set; }
    public List<string> Warnings { get; set; } = new();
    public Dictionary<string, string> Details { get; set; } = new();
}

public sealed class AdapterCapabilities
{
    public bool SupportsFullDiscovery { get; set; } = true;
    public bool SupportsIncremental { get; set; }
    public bool SupportsCredentialsValidation { get; set; } = true;
    public bool SupportsHealthCheck { get; set; } = true;
    public bool SupportsMetrics { get; set; }
    public int MaxConcurrency { get; set; } = 1;
    public TimeSpan EstimatedDiscoveryDuration { get; set; } = TimeSpan.FromMinutes(5);
    public List<string> SupportedEntityTypes { get; set; } = new();
    public List<string> SupportedRelationshipTypes { get; set; } = new();
}

public sealed class AdapterConfigurationSchema
{
    /// <summary>JSON Schema (draft 2020-12) describing non-secret configuration fields.</summary>
    public string JsonSchema { get; set; } = "{}";

    /// <summary>Field names that must be stored in the encrypted credential vault.</summary>
    public List<string> SecretFields { get; set; } = new();

    /// <summary>Human readable notes shown in the integration wizard.</summary>
    public List<string> Notes { get; set; } = new();
}

public sealed class DiscoveredNodeContract
{
    /// <summary>Stable identifier inside the source system (objectGUID, MoRef, ...).</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Name of a <c>BerMap.Domain.Enums.NodeType</c> value.</summary>
    public string NodeType { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string FQDN { get; set; } = string.Empty;
    public string? IPAddress { get; set; }
    public string? MacAddress { get; set; }
    public string Status { get; set; } = "healthy";

    /// <summary>SMBIOS / BIOS UUID. Immutable hardware identity.</summary>
    public string BiosUuid { get; set; } = string.Empty;

    /// <summary>vCenter instance UUID (unique per VM, survives cloning/migration).</summary>
    public string VmInstanceUuid { get; set; } = string.Empty;

    /// <summary>Active Directory objectSid (domain-scoped machine identity).</summary>
    public string DomainSid { get; set; } = string.Empty;

    /// <summary>External id of the parent container (OU, domain, cluster, datacenter, ...).</summary>
    public string? ParentExternalId { get; set; }

    /// <summary>Human readable parent name used for Name+Type+Parent reconciliation.</summary>
    public string? ParentName { get; set; }

    /// <summary>Discovery source identifier, e.g. "ActiveDirectory" or "VMware".</summary>
    public string SourceSystem { get; set; } = string.Empty;

    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    public List<string> Aliases { get; set; } = new();

    public Dictionary<string, string> Properties { get; set; } = new();
}

public sealed class DiscoveredRelationshipContract
{
    public string SourceExternalId { get; set; } = string.Empty;
    public string TargetExternalId { get; set; } = string.Empty;

    /// <summary>Name of a <c>BerMap.Domain.Enums.RelationshipType</c> value.</summary>
    public string RelationshipType { get; set; } = string.Empty;

    public string ConfidenceLevel { get; set; } = "Verified";
    public string Evidence { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Properties { get; set; } = new();
}

public sealed class DiscoveryResult
{
    public bool Success { get; set; }
    public string Status { get; set; } = "Completed";

    /// <summary>
    /// Integration that produced this result. Set by the orchestrator before ingestion.
    /// Collector-submitted batches must provide this so source links and stale detection
    /// remain scoped to the correct integration.
    /// </summary>
    public Guid? IntegrationId { get; set; }

    public string? SourceVersion { get; set; }
    public List<DiscoveredNodeContract> Nodes { get; set; } = new();
    public List<DiscoveredRelationshipContract> Relationships { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public Dictionary<string, int> Statistics { get; set; } = new();
    public TimeSpan Duration { get; set; }
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}

public sealed class DiscoveryProgress
{
    public string Phase { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int Percent { get; set; }
    public int NodesDiscovered { get; set; }
    public int RelationshipsDiscovered { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Thrown for invalid adapter configuration. The message is safe to return to the
/// caller and must not contain credentials.
/// </summary>
public sealed class AdapterConfigurationException : Exception
{
    public AdapterConfigurationException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class CollectorHeartbeat
{
    public Guid CollectorId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public double CpuUsagePercent { get; set; }
    public double MemoryUsagePercent { get; set; }
    public int QueuedMessagesCount { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
