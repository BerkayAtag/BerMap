using System;
using System.Linq;
using System.Threading.Tasks;
using BerMap.Application.Services.Reconciliation;
using BerMap.Contracts;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using BerMap.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BerMap.Tests.Unit;

public class DiscoveryIngestionServiceTests
{
    private static BerMapDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<BerMapDbContext>()
            .UseInMemoryDatabase($"ingestion-{Guid.NewGuid():N}")
            .Options;

        return new BerMapDbContext(options);
    }

    private static async Task<Integration> SeedIntegrationAsync(BerMapDbContext context)
    {
        var integration = new Integration
        {
            Name = "AD Production",
            AdapterType = "ActiveDirectory",
            Status = IntegrationStatus.Active
        };

        context.Integrations.Add(integration);
        await context.SaveChangesAsync();
        return integration;
    }

    private static DiscoveryIngestionService CreateService(BerMapDbContext context)
    {
        return new DiscoveryIngestionService(
            context,
            new EntityReconciliationService(),
            NullLogger<DiscoveryIngestionService>.Instance);
    }

    private static DiscoveryResult BuildBatch(Guid integrationId, params DiscoveredNodeContract[] nodes)
    {
        return new DiscoveryResult
        {
            Success = true,
            IntegrationId = integrationId,
            SourceVersion = "test",
            Nodes = nodes.ToList()
        };
    }

    [Fact]
    public async Task Ingest_CreatesNodesLinksAndRelationships()
    {
        await using var context = CreateContext();
        var integration = await SeedIntegrationAsync(context);
        var service = CreateService(context);
        var jobId = Guid.NewGuid();

        var batch = BuildBatch(integration.Id,
            new DiscoveredNodeContract
            {
                ExternalId = "ad-1",
                NodeType = "Computer",
                Name = "srv01",
                FQDN = "srv01.corp.local",
                DomainSid = "S-1-5-21-1000",
                SourceSystem = "ActiveDirectory",
                Status = "healthy"
            },
            new DiscoveredNodeContract
            {
                ExternalId = "ad-2",
                NodeType = "OrganizationalUnit",
                Name = "Servers",
                FQDN = "OU=Servers,DC=corp,DC=local",
                SourceSystem = "ActiveDirectory"
            });

        batch.Relationships.Add(new DiscoveredRelationshipContract
        {
            SourceExternalId = "ad-2",
            TargetExternalId = "ad-1",
            RelationshipType = "Contains",
            SourceSystem = "ActiveDirectory"
        });

        var result = await service.IngestAsync(integration.Id, jobId, batch);

        Assert.Equal(2, result.NodesCreated);
        Assert.Equal(1, result.RelationshipsCreated);
        Assert.Equal(2, result.SourceLinksCreated);
        Assert.Equal(0, result.ReviewsCreated);

        Assert.Equal(2, await context.InfrastructureNodes.CountAsync());
        Assert.Equal(1, await context.InfrastructureRelationships.CountAsync());
        Assert.Equal(2, await context.NodeSourceLinks.CountAsync());
        Assert.All(await context.InfrastructureNodes.ToListAsync(), n => Assert.Equal(jobId, n.LastSeenJobId));
    }

    [Fact]
    public async Task Ingest_IsIdempotentForStableSourceIdentity()
    {
        await using var context = CreateContext();
        var integration = await SeedIntegrationAsync(context);
        var service = CreateService(context);

        var node = new DiscoveredNodeContract
        {
            ExternalId = "vm-101",
            NodeType = "VirtualMachine",
            Name = "app01",
            FQDN = "app01.corp.local",
            BiosUuid = "42189b21-4a11-82e1-90a1-8912410a1122",
            SourceSystem = "VMware"
        };

        var first = await service.IngestAsync(
            integration.Id, Guid.NewGuid(), BuildBatch(integration.Id, node));
        var second = await service.IngestAsync(
            integration.Id, Guid.NewGuid(), BuildBatch(integration.Id, node));

        Assert.Equal(1, first.NodesCreated);
        Assert.Equal(0, second.NodesCreated);
        Assert.Equal(1, second.NodesUnchanged);
        Assert.Equal(1, await context.InfrastructureNodes.CountAsync());
        Assert.Equal(1, await context.NodeSourceLinks.CountAsync());
    }

    [Fact]
    public async Task Ingest_MarksUnseenSourceLinksAndNodesStale()
    {
        await using var context = CreateContext();
        var integration = await SeedIntegrationAsync(context);
        var service = CreateService(context);

        var firstNode = new DiscoveredNodeContract
        {
            ExternalId = "ad-1",
            NodeType = "Computer",
            Name = "srv01",
            FQDN = "srv01.corp.local",
            SourceSystem = "ActiveDirectory"
        };

        var secondNode = new DiscoveredNodeContract
        {
            ExternalId = "ad-2",
            NodeType = "Computer",
            Name = "srv02",
            FQDN = "srv02.corp.local",
            SourceSystem = "ActiveDirectory"
        };

        await service.IngestAsync(
            integration.Id, Guid.NewGuid(), BuildBatch(integration.Id, firstNode, secondNode));

        var result = await service.IngestAsync(
            integration.Id, Guid.NewGuid(), BuildBatch(integration.Id, firstNode));

        Assert.Equal(1, result.SourceLinksStale);
        Assert.Equal(1, result.NodesMarkedStale);

        var staleNode = await context.InfrastructureNodes.SingleAsync(n => n.Name == "srv02");
        Assert.True(staleNode.IsStale);
        Assert.NotNull(staleNode.StaleSince);

        var activeNode = await context.InfrastructureNodes.SingleAsync(n => n.Name == "srv01");
        Assert.False(activeNode.IsStale);
    }

    [Fact]
    public async Task Ingest_HostnameCollisionAcrossDomains_CreatesReviewInsteadOfMerge()
    {
        await using var context = CreateContext();
        var integration = await SeedIntegrationAsync(context);
        var service = CreateService(context);

        await service.IngestAsync(
            integration.Id,
            Guid.NewGuid(),
            BuildBatch(integration.Id, new DiscoveredNodeContract
            {
                ExternalId = "ad-corp-1",
                NodeType = "Computer",
                Name = "srv01",
                FQDN = "srv01.corp.local",
                DomainSid = "S-1-5-21-1000",
                SourceSystem = "ActiveDirectory"
            }));

        var result = await service.IngestAsync(
            integration.Id,
            Guid.NewGuid(),
            BuildBatch(integration.Id, new DiscoveredNodeContract
            {
                ExternalId = "ad-fin-1",
                NodeType = "Computer",
                Name = "srv01",
                FQDN = "srv01.fin.local",
                DomainSid = "S-1-5-21-2000",
                SourceSystem = "ActiveDirectory"
            }));

        Assert.Equal(1, result.NodesCreated);
        Assert.Equal(1, result.ReviewsCreated);
        Assert.Equal(2, await context.InfrastructureNodes.CountAsync());

        var review = await context.ReconciliationReviews.SingleAsync();
        Assert.Equal(ReconciliationReviewStatus.Pending, review.Status);
        Assert.Equal("HostnameCollision", review.MatchKey);
        Assert.NotEqual(Guid.Empty, review.CandidateNodeId);
    }

    [Fact]
    public async Task Ingest_SkipsNodesWithoutExternalIdAndReportsWarning()
    {
        await using var context = CreateContext();
        var integration = await SeedIntegrationAsync(context);
        var service = CreateService(context);

        var result = await service.IngestAsync(
            integration.Id,
            Guid.NewGuid(),
            BuildBatch(integration.Id, new DiscoveredNodeContract
            {
                Name = "no-id",
                FQDN = "no-id.corp.local",
                NodeType = "Computer"
            }));

        Assert.Equal(0, result.NodesCreated);
        Assert.Single(result.Warnings);
        Assert.Empty(await context.InfrastructureNodes.ToListAsync());
    }

    [Fact]
    public async Task Ingest_PartialRun_DoesNotMarkAbsentObjectsStale()
    {
        await using var context = CreateContext();
        var integration = await SeedIntegrationAsync(context);
        var service = CreateService(context);

        await service.IngestAsync(
            integration.Id,
            Guid.NewGuid(),
            BuildBatch(integration.Id,
                new DiscoveredNodeContract { ExternalId = "a", NodeType = "Computer", Name = "srv-a", FQDN = "srv-a.corp.local", SourceSystem = "ActiveDirectory" },
                new DiscoveredNodeContract { ExternalId = "b", NodeType = "Computer", Name = "srv-b", FQDN = "srv-b.corp.local", SourceSystem = "ActiveDirectory" }));

        // A later run whose computer search failed half-way only returns srv-a.
        var partial = BuildBatch(integration.Id,
            new DiscoveredNodeContract { ExternalId = "a", NodeType = "Computer", Name = "srv-a", FQDN = "srv-a.corp.local", SourceSystem = "ActiveDirectory" });
        partial.Success = false;
        partial.Status = "PartialSuccess";
        partial.Errors.Add("Computer search timed out.");

        var result = await service.IngestAsync(integration.Id, Guid.NewGuid(), partial, isCompleteSnapshot: false);

        Assert.Equal(0, result.NodesMarkedStale);
        Assert.Equal(0, result.SourceLinksStale);
        Assert.False((await context.InfrastructureNodes.SingleAsync(n => n.Name == "srv-b")).IsStale);
    }

    [Fact]
    public async Task Ingest_DefaultOverload_TreatsFailedResultAsIncomplete()
    {
        await using var context = CreateContext();
        var integration = await SeedIntegrationAsync(context);
        var service = CreateService(context);

        await service.IngestAsync(
            integration.Id,
            Guid.NewGuid(),
            BuildBatch(integration.Id,
                new DiscoveredNodeContract { ExternalId = "a", NodeType = "Computer", Name = "srv-a", FQDN = "srv-a.corp.local", SourceSystem = "ActiveDirectory" }));

        var failed = BuildBatch(integration.Id);
        failed.Success = false;
        failed.Status = "Failed";

        var result = await service.IngestAsync(integration.Id, Guid.NewGuid(), failed);

        Assert.Equal(0, result.NodesMarkedStale);
        Assert.False((await context.InfrastructureNodes.SingleAsync()).IsStale);
    }

    [Fact]
    public async Task Ingest_RelationshipAlreadyOwnedByAnotherIntegration_IsReusedNotDuplicated()
    {
        await using var context = CreateContext();
        var first = await SeedIntegrationAsync(context);
        var second = await SeedIntegrationAsync(context);
        var service = CreateService(context);

        DiscoveryResult Batch(Guid integrationId)
        {
            var batch = BuildBatch(integrationId,
                new DiscoveredNodeContract { ExternalId = "dc", NodeType = "DomainController", Name = "dc01", FQDN = "dc01.corp.local", DomainSid = "S-1-5-21-1-1000", SourceSystem = "ActiveDirectory" },
                new DiscoveredNodeContract { ExternalId = "dom", NodeType = "Domain", Name = "corp.local", FQDN = "corp.local", DomainSid = "S-1-5-21-1", SourceSystem = "ActiveDirectory" });
            batch.Relationships.Add(new DiscoveredRelationshipContract
            {
                SourceExternalId = "dc",
                TargetExternalId = "dom",
                RelationshipType = "MemberOf",
                ConfidenceLevel = "High",
                SourceSystem = "ActiveDirectory"
            });
            return batch;
        }

        await service.IngestAsync(first.Id, Guid.NewGuid(), Batch(first.Id));
        var result = await service.IngestAsync(second.Id, Guid.NewGuid(), Batch(second.Id));

        Assert.Equal(0, result.RelationshipsCreated);
        Assert.Equal(1, await context.InfrastructureRelationships.CountAsync());
    }

    [Fact]
    public async Task Ingest_MultiBatchSnapshot_KeepsEarlierBatchesAndLinksAcrossThem()
    {
        await using var context = CreateContext();
        var integration = await SeedIntegrationAsync(context);
        var service = CreateService(context);
        var jobId = Guid.NewGuid();

        await service.IngestAsync(integration.Id, jobId,
            BuildBatch(integration.Id,
                new DiscoveredNodeContract { ExternalId = "host-1", NodeType = "Hypervisor", Name = "esx01", FQDN = "esx01.corp.local", SourceSystem = "VMware" }),
            isCompleteSnapshot: false);

        var final = BuildBatch(integration.Id,
            new DiscoveredNodeContract { ExternalId = "vm-1", NodeType = "VirtualMachine", Name = "app01", FQDN = "app01.corp.local", SourceSystem = "VMware" });
        final.Relationships.Add(new DiscoveredRelationshipContract
        {
            SourceExternalId = "vm-1",
            TargetExternalId = "host-1",
            RelationshipType = "HostedOn",
            SourceSystem = "VMware"
        });

        var result = await service.IngestAsync(integration.Id, jobId, final, isCompleteSnapshot: true);

        Assert.Equal(1, result.RelationshipsCreated);
        Assert.Equal(0, result.NodesMarkedStale);
        Assert.False((await context.InfrastructureNodes.SingleAsync(n => n.Name == "esx01")).IsStale);
    }

    [Fact]
    public async Task Ingest_SameSourceRediscovery_RefreshesMetricsAndIdentity()
    {
        await using var context = CreateContext();
        var integration = await SeedIntegrationAsync(context);
        var service = CreateService(context);

        DiscoveredNodeContract Vm(string name, string ip, string cpu) => new()
        {
            ExternalId = "vm-42", NodeType = "VirtualMachine", Name = name, FQDN = name + ".corp.local",
            IPAddress = ip, SourceSystem = "VMware",
            Properties = new() { ["CpuUsagePercent"] = cpu, ["PowerState"] = "POWERED_ON" }
        };

        await service.IngestAsync(integration.Id, Guid.NewGuid(), BuildBatch(integration.Id, Vm("app-01", "10.0.0.1", "40")));
        var second = Vm("app-01-renamed", "10.0.0.2", "85");
        second.Properties["PowerState"] = "POWERED_OFF";
        await service.IngestAsync(integration.Id, Guid.NewGuid(), BuildBatch(integration.Id, second));

        var node = await context.InfrastructureNodes.SingleAsync();
        Assert.Equal("app-01-renamed", node.Name);
        Assert.Equal("app-01-renamed.corp.local", node.FQDN);
        Assert.Equal("10.0.0.2", node.IPAddress);
        Assert.Contains("\"CpuUsagePercent\":\"85\"", node.PropertiesJson);
        Assert.Contains("\"PowerState\":\"POWERED_OFF\"", node.PropertiesJson);
    }

    [Fact]
    public async Task Ingest_MultiSourceNode_KeepsIdentityButRefreshesReportedMetrics()
    {
        await using var context = CreateContext();
        var ad = await SeedIntegrationAsync(context);
        var vcenter = await SeedIntegrationAsync(context);
        var service = CreateService(context);

        await service.IngestAsync(ad.Id, Guid.NewGuid(), BuildBatch(ad.Id, new DiscoveredNodeContract
        {
            ExternalId = "ad-app-01", NodeType = "Computer", Name = "APP-01", FQDN = "app-01.corp.local",
            DomainSid = "S-1-5-21-9-1001", SourceSystem = "ActiveDirectory"
        }));

        DiscoveredNodeContract Vm(string name, string cpu) => new()
        {
            ExternalId = "vm-app-01", NodeType = "VirtualMachine", Name = name, FQDN = "app-01.corp.local",
            SourceSystem = "VMware", Properties = new() { ["CpuUsagePercent"] = cpu }
        };

        await service.IngestAsync(vcenter.Id, Guid.NewGuid(), BuildBatch(vcenter.Id, Vm("app-01 (vSphere)", "30")));
        await service.IngestAsync(vcenter.Id, Guid.NewGuid(), BuildBatch(vcenter.Id, Vm("app-01 (vSphere, renamed)", "77")));

        var node = await context.InfrastructureNodes.SingleAsync();
        Assert.Equal("APP-01", node.Name);
        Assert.Contains("\"CpuUsagePercent\":\"77\"", node.PropertiesJson);
    }
}
