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
}
