using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BerMap.Infrastructure.Services;

/// <summary>
/// Builds the dashboard summary exclusively from persisted discovery data. No
/// sample or placeholder values are returned; an empty database yields zeros.
/// </summary>
public sealed class DashboardService : IDashboardService
{
    private readonly BerMapDbContext _context;

    public DashboardService(BerMapDbContext context)
    {
        _context = context;
    }

    public async Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var nodes = _context.InfrastructureNodes.AsNoTracking();
        var integrations = _context.Integrations.AsNoTracking().Where(i => i.DeletedAt == null);

        var totalEntities = await nodes.CountAsync(cancellationToken);
        var healthy = await nodes.CountAsync(n => n.Status == "healthy" && !n.IsStale, cancellationToken);
        var warning = await nodes.CountAsync(n => n.Status == "warning" && !n.IsStale, cancellationToken);
        var critical = await nodes.CountAsync(n => n.Status == "critical" && !n.IsStale, cancellationToken);
        var stale = await nodes.CountAsync(n => n.IsStale, cancellationToken);
        var unknown = Math.Max(0, totalEntities - healthy - warning - critical - stale);

        var totalIntegrations = await integrations.CountAsync(cancellationToken);
        var activeIntegrations = await integrations.CountAsync(
            i => i.IsEnabled && i.Status != IntegrationStatus.Inactive && i.Status != IntegrationStatus.Error,
            cancellationToken);

        var pendingReviews = await _context.ReconciliationReviews
            .AsNoTracking()
            .CountAsync(r => r.Status == ReconciliationReviewStatus.Pending, cancellationToken);

        var criticalDependencies = await _context.InfrastructureRelationships
            .AsNoTracking()
            .CountAsync(r => !r.IsStale &&
                (r.SourceNode.Status == "critical" || r.TargetNode.Status == "critical"),
                cancellationToken);

        var discoveryWindow = DateTime.UtcNow.AddHours(-24);
        var recentDiscoveries = await nodes.CountAsync(n => n.FirstSeen >= discoveryWindow, cancellationToken);

        var lastNodeSeen = await nodes.MaxAsync(n => (DateTime?)n.LastSeen, cancellationToken);
        var lastJobCompleted = await _context.DiscoveryJobs
            .AsNoTracking()
            .Where(j => j.CompletedAt != null)
            .MaxAsync(j => j.CompletedAt, cancellationToken);
        var lastDiscoveryAt = new[] { lastNodeSeen, lastJobCompleted }.Where(d => d.HasValue).Max();

        // Only real changes, newest first: a node's first discovery, or a
        // status transition recorded by ingestion. A node that is merely
        // re-seen by the next discovery run is not a change.
        var recentNodes = await nodes
            .OrderByDescending(n => n.StatusChangedAt ?? n.FirstSeen)
            .ThenBy(n => n.Id)
            .Take(10)
            .Select(n => new
            {
                n.Id,
                n.Name,
                n.NodeType,
                n.DiscoverySource,
                n.Status,
                n.FirstSeen,
                n.StatusChangedAt
            })
            .ToListAsync(cancellationToken);

        var recentChanges = recentNodes.Select(n => new RecentChangeDto
        {
            Id = n.Id,
            EntityName = n.Name,
            EntityType = n.NodeType.ToString(),
            ChangeType = n.StatusChangedAt.HasValue ? "StatusChanged" : "Discovered",
            Source = n.DiscoverySource,
            Status = n.Status,
            Timestamp = n.StatusChangedAt ?? n.FirstSeen
        }).ToList();

        return new DashboardSummaryDto
        {
            TotalEntities = totalEntities,
            HealthyEntities = healthy,
            WarningEntities = warning,
            CriticalEntities = critical,
            UnknownEntities = unknown,
            StaleEntities = stale,
            ActiveIntegrations = activeIntegrations,
            TotalIntegrations = totalIntegrations,
            PendingReconciliationReviews = pendingReviews,
            CriticalDependenciesCount = criticalDependencies,
            RecentDiscoveries = recentDiscoveries,
            LastDiscoveryAt = lastDiscoveryAt,
            RecentChanges = recentChanges
        };
    }
}
