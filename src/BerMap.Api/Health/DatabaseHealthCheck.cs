using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Infrastructure.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BerMap.Api.Health;

/// <summary>
/// Readiness check that verifies the API can reach PostgreSQL and that the
/// schema is current. It does not create or migrate the schema itself.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly BerMapDbContext _context;

    public DatabaseHealthCheck(BerMapDbContext context)
    {
        _context = context;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _context.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("PostgreSQL connection failed.");

            // Reachable but not on the current schema is not ready: every
            // request would fail with "relation does not exist".
            var pending = (await _context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            return pending.Count == 0
                ? HealthCheckResult.Healthy("PostgreSQL connection succeeded.")
                : HealthCheckResult.Unhealthy($"Database schema is out of date ({pending.Count} pending migration(s)).");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL connection failed.", ex);
        }
    }
}
