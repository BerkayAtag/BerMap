using System;
using System.Linq;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Application.Services.Reconciliation;
using BerMap.Contracts;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using BerMap.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BerMap.Tests.Unit;

/// <summary>Status-change tracking, the dashboard's recent changes and inventory filters/reviews.</summary>
public class ChangeTrackingAndInventoryTests
{
    private static BerMapDbContext CreateContext() => new(new DbContextOptionsBuilder<BerMapDbContext>()
        .UseInMemoryDatabase($"changes-{Guid.NewGuid():N}")
        .Options);

    private static InventoryService CreateInventory(BerMapDbContext context) => new(
        context, new AuditService(context, new HttpContextAccessor()), NullLogger<InventoryService>.Instance);

    private static DiscoveryResult Batch(Guid integrationId, string status) => new()
    {
        Success = true,
        IntegrationId = integrationId,
        SourceVersion = "test",
        Nodes =
        {
            new DiscoveredNodeContract
            {
                ExternalId = "ad-1",
                NodeType = "Computer",
                Name = "srv01",
                FQDN = "srv01.corp.local",
                SourceSystem = "ActiveDirectory",
                Status = status
            }
        }
    };

    [Fact]
    public async Task Ingestion_SetsStatusChangedAt_OnlyOnRealTransitions()
    {
        await using var context = CreateContext();
        var integration = new Integration { Name = "AD", AdapterType = "ActiveDirectory", Status = IntegrationStatus.Active };
        context.Integrations.Add(integration);
        await context.SaveChangesAsync();
        var ingestion = new DiscoveryIngestionService(
            context, new EntityReconciliationService(), NullLogger<DiscoveryIngestionService>.Instance);

        await ingestion.IngestAsync(integration.Id, Guid.NewGuid(), Batch(integration.Id, "healthy"));
        var node = await context.InfrastructureNodes.SingleAsync();
        Assert.Null(node.StatusChangedAt); // first discovery is not a status change

        await ingestion.IngestAsync(integration.Id, Guid.NewGuid(), Batch(integration.Id, "online")); // normalises to healthy
        Assert.Null((await context.InfrastructureNodes.SingleAsync()).StatusChangedAt);

        await ingestion.IngestAsync(integration.Id, Guid.NewGuid(), Batch(integration.Id, "critical"));
        node = await context.InfrastructureNodes.SingleAsync();
        Assert.Equal("critical", node.Status);
        Assert.NotNull(node.StatusChangedAt);
    }

    [Fact]
    public async Task RecentChanges_ListOnlyDiscoveriesAndStatusChanges_NewestFirst()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;

        // Discovered long ago and merely re-seen just now: not a recent change.
        var reSeen = new InfrastructureNode
        {
            Name = "re-seen", NodeType = NodeType.Computer, Status = "healthy",
            FirstSeen = now.AddDays(-5), LastSeen = now
        };
        var changed = new InfrastructureNode
        {
            Name = "changed", NodeType = NodeType.Computer, Status = "critical",
            FirstSeen = now.AddDays(-3), LastSeen = now, StatusChangedAt = now.AddMinutes(-10)
        };
        var discovered = new InfrastructureNode
        {
            Name = "new", NodeType = NodeType.VirtualMachine, Status = "healthy",
            FirstSeen = now.AddHours(-1), LastSeen = now
        };
        context.InfrastructureNodes.AddRange(reSeen, changed, discovered);
        await context.SaveChangesAsync();

        var summary = await new DashboardService(context).GetSummaryAsync();

        Assert.Equal(new[] { changed.Id, discovered.Id, reSeen.Id }, summary.RecentChanges.Select(c => c.Id));
        Assert.Equal("StatusChanged", summary.RecentChanges[0].ChangeType);
        Assert.Equal(changed.StatusChangedAt, summary.RecentChanges[0].Timestamp);
        Assert.Equal("Discovered", summary.RecentChanges[1].ChangeType);
        Assert.Equal(discovered.FirstSeen, summary.RecentChanges[1].Timestamp);
        Assert.Equal(reSeen.FirstSeen, summary.RecentChanges[2].Timestamp);
    }

    [Theory]
    [InlineData("NotAType", null)]
    [InlineData("42", null)]
    [InlineData(null, "broken")]
    public async Task Inventory_RejectsUnknownFilterValues(string? nodeType, string? status)
    {
        await using var context = CreateContext();

        var error = await Assert.ThrowsAsync<IntegrationValidationException>(() =>
            CreateInventory(context).ListNodesAsync(new InventoryQueryRequest { NodeType = nodeType, Status = status }));

        Assert.Equal("invalid_filter", error.Code);
    }

    [Fact]
    public async Task Inventory_AcceptsKnownFilters_AndOrdersNamesCaseInsensitively()
    {
        await using var context = CreateContext();
        foreach (var name in new[] { "beta", "Alpha", "alpha2", "Charlie" })
            context.InfrastructureNodes.Add(new InfrastructureNode { Name = name, NodeType = NodeType.Computer, Status = "healthy" });
        context.InfrastructureNodes.Add(new InfrastructureNode { Name = "vm", NodeType = NodeType.VirtualMachine, Status = "warning" });
        await context.SaveChangesAsync();

        var page = await CreateInventory(context).ListNodesAsync(new InventoryQueryRequest { NodeType = "computer", Status = "Healthy" });

        Assert.Equal(new[] { "Alpha", "alpha2", "beta", "Charlie" }, page.Items.Select(n => n.Name));
    }

    [Fact]
    public async Task DismissingAReview_RemovesThePossibleDuplicateEdge()
    {
        await using var context = CreateContext();
        var created = new InfrastructureNode { Name = "srv01", NodeType = NodeType.Computer };
        var candidate = new InfrastructureNode { Name = "SRV01", NodeType = NodeType.VirtualMachine };
        var unrelated = new InfrastructureNode { Name = "host", NodeType = NodeType.Hypervisor };
        context.InfrastructureNodes.AddRange(created, candidate, unrelated);
        context.InfrastructureRelationships.AddRange(
            new InfrastructureRelationship
            {
                SourceNodeId = created.Id, TargetNodeId = candidate.Id, RelationshipType = RelationshipType.PossibleDuplicate
            },
            new InfrastructureRelationship
            {
                SourceNodeId = candidate.Id, TargetNodeId = unrelated.Id, RelationshipType = RelationshipType.HostedOn
            });
        var review = new ReconciliationReview { CreatedNodeId = created.Id, CandidateNodeId = candidate.Id };
        context.ReconciliationReviews.Add(review);
        await context.SaveChangesAsync();

        var resolved = await CreateInventory(context).ResolveReviewAsync(
            review.Id, new ResolveReconciliationReviewRequest { Resolution = "Dismissed" }, "admin");

        Assert.Equal("Dismissed", resolved!.Status);
        var remaining = Assert.Single(context.InfrastructureRelationships);
        Assert.Equal(RelationshipType.HostedOn, remaining.RelationshipType);
    }
}
