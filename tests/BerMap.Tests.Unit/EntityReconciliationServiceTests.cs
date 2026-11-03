using System;
using System.Collections.Generic;
using Xunit;
using BerMap.Application.Services.Reconciliation;
using BerMap.Contracts;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;

namespace BerMap.Tests.Unit;

public class EntityReconciliationServiceTests
{
    private readonly EntityReconciliationService _service = new();

    [Fact]
    public void Reconcile_ExactBiosUuidMatch_MergesEntities()
    {
        // Arrange
        var biosUuid = "42189b21-4a11-82e1-90a1-8912410a1122";
        var existingNodes = new List<InfrastructureNode>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "srv-core-01",
                FQDN = "srv-core-01.corp.local",
                BiosUuid = biosUuid,
                DiscoverySource = "ActiveDirectory",
                PropertiesJson = "{}"
            }
        };

        var discoveredFromVMware = new DiscoveredNodeContract
        {
            Name = "vm-core-01",
            FQDN = "srv-core-01.corp.local",
            BiosUuid = biosUuid,
            NodeType = "VirtualMachine",
            IPAddress = "10.20.4.15",
            Properties = new Dictionary<string, string>
            {
                ["DiscoverySource"] = "VMware",
                ["MemoryGb"] = "32"
            }
        };

        // Act
        var result = _service.Reconcile(discoveredFromVMware, existingNodes);

        // Assert
        Assert.Equal(ReconciliationConfidence.ExactHardwareMatch, result.Confidence);
        Assert.False(result.IsNew);
        Assert.False(result.RequiresReview);
        Assert.Contains("VMware", result.TargetNode.DiscoverySource);
        Assert.Equal("10.20.4.15", result.TargetNode.IPAddress);
    }

    [Fact]
    public void Reconcile_HostnameCollisionWithDifferentDomains_RejectsAutoMergeAndFlagsForReview()
    {
        // Arrange: Section 14 rule - "Tek bir hostname eşleşmesiyle kayıtlar otomatik birleştirilmemelidir"
        var existingNodes = new List<InfrastructureNode>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "web01.corp.local",
                FQDN = "web01.corp.local",
                IPAddress = "10.10.1.5",
                DiscoverySource = "ActiveDirectory",
                PropertiesJson = "{}"
            }
        };

        var discoveredCollision = new DiscoveredNodeContract
        {
            Name = "web01.dmz.local",
            FQDN = "web01.dmz.local",
            IPAddress = "192.168.100.50",
            NodeType = "VirtualMachine",
            Properties = new Dictionary<string, string>
            {
                ["DiscoverySource"] = "VMware"
            }
        };

        // Act
        var result = _service.Reconcile(discoveredCollision, existingNodes);

        // Assert
        Assert.Equal(ReconciliationConfidence.AmbiguousConflict, result.Confidence);
        Assert.True(result.IsNew, "Must create separate entity rather than corrupting existing");
        Assert.True(result.RequiresReview, "Must require administrator review");
        Assert.Contains("collision", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reconcile_StrongFqdnMatch_MergesEntity()
    {
        // Arrange
        var fqdn = "sql-cluster01.fin.local";
        var existingNodes = new List<InfrastructureNode>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "sql-cluster01",
                FQDN = fqdn,
                DiscoverySource = "ActiveDirectory",
                PropertiesJson = "{}"
            }
        };

        var discoveredNode = new DiscoveredNodeContract
        {
            Name = "sql-cluster01",
            FQDN = fqdn,
            NodeType = "Database",
            IPAddress = "10.20.8.21",
            Properties = new Dictionary<string, string>
            {
                ["DiscoverySource"] = "SQL"
            }
        };

        // Act
        var result = _service.Reconcile(discoveredNode, existingNodes);

        // Assert
        Assert.Equal(ReconciliationConfidence.StrongFqdnMatch, result.Confidence);
        Assert.False(result.IsNew);
        Assert.False(result.RequiresReview);
        Assert.Equal("10.20.8.21", result.TargetNode.IPAddress);
    }

    [Fact]
    public void Reconcile_VmInstanceUuidMatch_MergesEntity()
    {
        var vmUuid = "501e5b7c-2c3a-4d5f-9a10-112233445566";
        var existingNodes = new List<InfrastructureNode>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "app01",
                FQDN = "app01.corp.local",
                VmInstanceUuid = vmUuid,
                DiscoverySource = "VMware",
                PropertiesJson = "{}"
            }
        };

        var discovered = new DiscoveredNodeContract
        {
            Name = "app01-renamed",
            FQDN = "app01.corp.local",
            NodeType = "VirtualMachine",
            VmInstanceUuid = vmUuid,
            SourceSystem = "VMware"
        };

        var result = _service.Reconcile(discovered, existingNodes);

        Assert.Equal(ReconciliationConfidence.ExactHardwareMatch, result.Confidence);
        Assert.Equal("VmInstanceUuid", result.MatchKey);
        Assert.False(result.IsNew);
        Assert.False(result.RequiresReview);
    }

    [Fact]
    public void Reconcile_DomainSidMatch_MergesEntity()
    {
        const string sid = "S-1-5-21-1111-2222-3333-1001";
        var existingNodes = new List<InfrastructureNode>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "srv01",
                FQDN = "srv01.corp.local",
                DomainSid = sid,
                DiscoverySource = "ActiveDirectory",
                PropertiesJson = "{}"
            }
        };

        var discovered = new DiscoveredNodeContract
        {
            Name = "srv01",
            FQDN = "srv01.corp.local",
            NodeType = "Computer",
            DomainSid = sid,
            SourceSystem = "ActiveDirectory"
        };

        var result = _service.Reconcile(discovered, existingNodes);

        Assert.Equal(ReconciliationConfidence.StrongDomainSidMatch, result.Confidence);
        Assert.Equal("DomainSid", result.MatchKey);
        Assert.False(result.IsNew);
    }

    [Fact]
    public void Reconcile_IpAndMacMatch_MergesEntity()
    {
        var existingNodes = new List<InfrastructureNode>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "legacy-name",
                FQDN = "legacy-name.corp.local",
                IPAddress = "10.10.5.20",
                MacAddress = "00:50:56:AA:BB:CC",
                DiscoverySource = "ActiveDirectory",
                PropertiesJson = "{}"
            }
        };

        var discovered = new DiscoveredNodeContract
        {
            Name = "new-name",
            NodeType = "Computer",
            IPAddress = "10.10.5.20",
            MacAddress = "0050.56aa.bbcc",
            SourceSystem = "VMware"
        };

        var result = _service.Reconcile(discovered, existingNodes);

        Assert.Equal(ReconciliationConfidence.StrongNetworkIdentityMatch, result.Confidence);
        Assert.Equal("IpMac", result.MatchKey);
        Assert.False(result.IsNew);
    }

    [Fact]
    public void Reconcile_NameTypeParent_RequiresReviewAndCreatesSeparateNode()
    {
        var parentId = "OU=Servers,DC=corp,DC=local";
        var existing = new InfrastructureNode
        {
            Id = Guid.NewGuid(),
            Name = "srv01",
            FQDN = "srv01.corp.local",
            NodeType = NodeType.Computer,
            DiscoverySource = "ActiveDirectory",
            PropertiesJson = $"{{\"ParentExternalId\":\"{parentId}\"}}"
        };

        var discovered = new DiscoveredNodeContract
        {
            Name = "srv01",
            NodeType = "Computer",
            ParentExternalId = parentId,
            ParentName = "Servers",
            SourceSystem = "ActiveDirectory"
        };

        var result = _service.Reconcile(discovered, new List<InfrastructureNode> { existing });

        Assert.Equal(ReconciliationConfidence.ProbableNameParentMatch, result.Confidence);
        Assert.True(result.IsNew);
        Assert.True(result.RequiresReview);
        Assert.NotEqual(existing.Id, result.TargetNode.Id);
        Assert.Contains(result.Candidates, c => c.NodeId == existing.Id);
    }

    [Fact]
    public void Reconcile_HostnameOnlyMatch_FlagsReviewWithoutMerging()
    {
        var existing = new InfrastructureNode
        {
            Id = Guid.NewGuid(),
            Name = "srv01",
            FQDN = "srv01.corp.local",
            NodeType = NodeType.Computer,
            DiscoverySource = "ActiveDirectory",
            PropertiesJson = "{}"
        };

        var discovered = new DiscoveredNodeContract
        {
            Name = "srv01",
            NodeType = "Computer",
            SourceSystem = "Collector"
        };

        var result = _service.Reconcile(discovered, new List<InfrastructureNode> { existing });

        Assert.Equal(ReconciliationConfidence.AmbiguousConflict, result.Confidence);
        Assert.True(result.IsNew);
        Assert.True(result.RequiresReview);
        Assert.NotEqual(existing.Id, result.TargetNode.Id);
    }
}
