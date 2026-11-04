using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Api.Security;
using BerMap.Application.DTOs;
using BerMap.Application.Services.Export;
using BerMap.Domain.Entities;
using BerMap.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BerMap.Api.Controllers;

/// <summary>
/// The security audit trail. It contains failed sign-ins with the attempted
/// usernames, client addresses and change details, so it is restricted to
/// administrators and security auditors.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = AuthorizationPolicies.ViewAudit)]
public class AuditController : ControllerBase
{
    /// <summary>Upper bound of one export; larger ranges must be narrowed with filters.</summary>
    public const int MaxExportRows = 50_000;

    private readonly BerMapDbContext _context;

    public AuditController(BerMapDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<AuditPageDto>> List(
        [FromQuery] AuditQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);

        var query = Filter(_context.AuditLogs.AsNoTracking(), request);
        var total = await query.CountAsync(cancellationToken);

        var items = await Order(query)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ToDto)
            .ToListAsync(cancellationToken);

        return Ok(new AuditPageDto { Items = items, TotalCount = total, Page = page, PageSize = pageSize });
    }

    /// <summary>
    /// The filtered audit trail as CSV (UTF-8 with BOM for spreadsheets),
    /// newest first, capped at <see cref="MaxExportRows"/> rows.
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] AuditQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var rows = await Order(Filter(_context.AuditLogs.AsNoTracking(), request))
            .Take(MaxExportRows)
            .Select(ToDto)
            .ToListAsync(cancellationToken);

        var csv = new StringBuilder();
        csv.Append(CsvWriter.Row(new[]
        {
            "createdAt", "username", "userId", "action", "entityType", "entityId", "success", "ipAddress", "details"
        })).Append("\r\n");

        foreach (var row in rows)
        {
            csv.Append(CsvWriter.Row(new[]
            {
                row.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                row.Username,
                row.UserId,
                row.Action,
                row.EntityType,
                row.EntityId,
                row.Success ? "true" : "false",
                row.IpAddress,
                row.DetailsJson
            })).Append("\r\n");
        }

        var fileName = $"bermap-audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        return File(CsvWriter.ToUtf8WithBom(csv), "text/csv; charset=utf-8", fileName);
    }

    internal static IQueryable<AuditLog> Filter(IQueryable<AuditLog> query, AuditQueryRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Action))
        {
            var action = request.Action.Trim();
            query = query.Where(a => a.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(request.EntityType))
        {
            var entityType = request.EntityType.Trim();
            query = query.Where(a => a.EntityType == entityType);
        }

        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            var username = request.Username.Trim().ToLower();
            query = query.Where(a => a.Username.ToLower().Contains(username));
        }

        if (request.Success.HasValue)
        {
            var success = request.Success.Value;
            query = query.Where(a => a.Success == success);
        }

        if (request.From.HasValue)
        {
            var from = ToUtc(request.From.Value);
            query = query.Where(a => a.CreatedAt >= from);
        }

        if (request.To.HasValue)
        {
            // A date without a time ("2026-11-05") includes that whole day.
            var to = ToUtc(request.To.Value);
            if (to.TimeOfDay == TimeSpan.Zero)
            {
                var endExclusive = to.AddDays(1);
                query = query.Where(a => a.CreatedAt < endExclusive);
            }
            else
            {
                query = query.Where(a => a.CreatedAt <= to);
            }
        }

        return query;
    }

    private static IQueryable<AuditLog> Order(IQueryable<AuditLog> query) =>
        query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id);

    private static readonly System.Linq.Expressions.Expression<Func<AuditLog, AuditLogDto>> ToDto = a => new AuditLogDto
    {
        Id = a.Id,
        UserId = a.UserId,
        Username = a.Username,
        Action = a.Action,
        EntityType = a.EntityType,
        EntityId = a.EntityId,
        DetailsJson = a.DetailsJson,
        IpAddress = a.IpAddress,
        Success = a.Success,
        CreatedAt = a.CreatedAt
    };

    // Query-string dates without an offset are taken as UTC; PostgreSQL
    // timestamptz parameters must be UTC.
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
