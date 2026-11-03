using System;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.Interfaces;
using BerMap.Domain.Entities;
using BerMap.Infrastructure.Data;
using Microsoft.AspNetCore.Http;

namespace BerMap.Infrastructure.Services;

/// <summary>
/// Writes tamper-evident application audit records. The IP address is resolved
/// from the current HTTP request when the service is used inside the API; worker
/// and collector paths leave it empty.
/// </summary>
public sealed class AuditService : IAuditService
{
    private readonly BerMapDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuditService(BerMapDbContext context, IHttpContextAccessor httpContextAccessor)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task LogAsync(
        string userId,
        string username,
        string action,
        string entityType,
        string entityId,
        string detailsJson,
        string ipAddress,
        bool success = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
            ipAddress = ResolveIpAddress();

        _context.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? string.Empty,
            Username = username ?? string.Empty,
            Action = action,
            EntityType = entityType,
            EntityId = entityId ?? string.Empty,
            DetailsJson = string.IsNullOrWhiteSpace(detailsJson) ? "{}" : detailsJson,
            IpAddress = ipAddress,
            Success = success,
            CreatedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync(cancellationToken);
    }

    private string ResolveIpAddress()
    {
        var connection = _httpContextAccessor.HttpContext?.Connection;
        return connection?.RemoteIpAddress?.ToString() ?? string.Empty;
    }
}
