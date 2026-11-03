using System;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Infrastructure.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BerMap.Api.Health;

/// <summary>
/// Readiness check that verifies the API can actually reach PostgreSQL. It does
/// not create or migrate the schema.
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
            var canConnect = await _context.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("PostgreSQL connection succeeded.")
                : HealthCheckResult.Unhealthy("PostgreSQL connection failed.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL connection failed.", ex);
        }
    }
}
