using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.Services.Integrations;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BerMap.Infrastructure.Services;

/// <summary>
/// Integration health and job housekeeping shared by the integration service,
/// the discovery runner and the collector endpoint.
/// </summary>
public static class IntegrationHealth
{
    /// <summary>A collector job without a batch for this long was abandoned by its collector.</summary>
    public static readonly TimeSpan AbandonedCollectorJobAge = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The status an idle integration shows: Inactive when disabled, otherwise
    /// the outcome of its last finished test or job (a partial run stays
    /// Degraded, a failed one Error), never a guess from the error text.
    /// </summary>
    public static async Task<IntegrationStatus> RestingStatusAsync(
        BerMapDbContext context,
        Integration integration,
        CancellationToken cancellationToken)
    {
        if (!integration.IsEnabled)
            return IntegrationStatus.Inactive;

        var lastJob = await context.DiscoveryJobs
            .AsNoTracking()
            .Where(j => j.IntegrationId == integration.Id && j.CompletedAt != null &&
                        (j.Status == DiscoveryJobStatus.Completed ||
                         j.Status == DiscoveryJobStatus.PartialSuccess ||
                         j.Status == DiscoveryJobStatus.Failed))
            .OrderByDescending(j => j.CompletedAt)
            .Select(j => new { j.Status, j.CompletedAt })
            .FirstOrDefaultAsync(cancellationToken);

        return IntegrationStatusRules.Resting(
            integration.IsEnabled,
            IntegrationStatusRules.LastOutcome(
                integration.LastError,
                integration.LastTestAt,
                integration.LastTestSuccess,
                lastJob?.Status,
                lastJob?.CompletedAt));
    }

    /// <summary>
    /// Collector jobs stay Running between batches. One that has not received
    /// a batch for <see cref="AbandonedCollectorJobAge"/> was abandoned by its
    /// collector (it crashed mid-snapshot) and is failed, so it neither blocks
    /// discovery nor shows as running forever. Changes are tracked, not saved.
    /// </summary>
    public static async Task<int> FailAbandonedCollectorJobsAsync(
        BerMapDbContext context,
        Guid integrationId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var abandonedBefore = now - AbandonedCollectorJobAge;
        var abandoned = await context.DiscoveryJobs
            .Where(j => j.IntegrationId == integrationId && j.JobType == "Collector" &&
                        j.Status == DiscoveryJobStatus.Running &&
                        (j.UpdatedAt ?? j.StartedAt) < abandonedBefore)
            .ToListAsync(cancellationToken);

        foreach (var stale in abandoned)
        {
            stale.Status = DiscoveryJobStatus.Failed;
            stale.CompletedAt = now;
            stale.UpdatedAt = now;
            stale.CurrentPhase = "Failed";
            stale.CurrentMessage = "The collector stopped sending batches before the snapshot was completed.";
        }

        return abandoned.Count;
    }
}
