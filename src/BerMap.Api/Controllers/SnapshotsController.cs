using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Application.Services;
using BerMap.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BerMap.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SnapshotsController : ControllerBase
{
    private readonly ISnapshotService _snapshotService;
    private readonly IAuditService _auditService;

    public SnapshotsController(ISnapshotService snapshotService, IAuditService auditService)
    {
        _snapshotService = snapshotService;
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<ActionResult<List<SnapshotSummaryDto>>> List(CancellationToken cancellationToken)
    {
        return Ok(await _snapshotService.ListSnapshotsAsync(cancellationToken));
    }

    [Authorize(Policy = AuthorizationPolicies.Operate)]
    [HttpPost]
    public async Task<ActionResult<SnapshotSummaryDto>> Create(
        [FromBody] CreateSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        var createdBy = this.GetActor();
        var snapshot = await _snapshotService.CreateSnapshotAsync(request, createdBy, cancellationToken);
        await _auditService.LogAsync(
            createdBy, createdBy, "Snapshot.Created", "Snapshot", snapshot.Id.ToString(),
            JsonSerializer.Serialize(new
            {
                snapshot.Name,
                snapshot.SnapshotVersion,
                snapshot.NodeCount,
                snapshot.RelationshipCount
            }),
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty, true, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = snapshot.Id }, snapshot);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SnapshotSummaryDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var snapshot = await _snapshotService.GetSnapshotAsync(id, cancellationToken);
        return snapshot == null ? NotFound() : Ok(snapshot);
    }

    [HttpGet("{id:guid}/graph")]
    public async Task<ActionResult<SnapshotGraphDto>> GetGraph(Guid id, CancellationToken cancellationToken)
    {
        var graph = await _snapshotService.GetSnapshotGraphAsync(id, cancellationToken);
        return graph == null ? NotFound() : Ok(graph);
    }

    [HttpGet("compare")]
    public async Task<ActionResult<SnapshotDiffDto>> Compare(
        [FromQuery] Guid? a,
        [FromQuery] Guid? b,
        CancellationToken cancellationToken)
    {
        // Nullable so a missing parameter is a 400 from the service, not a
        // lookup of the empty id that ends in 404.
        return Ok(await _snapshotService.CompareSnapshotsAsync(a ?? Guid.Empty, b ?? Guid.Empty, cancellationToken));
    }

    [HttpGet("{id:guid}/verify")]
    public async Task<ActionResult<SnapshotVerificationDto>> Verify(Guid id, CancellationToken cancellationToken)
    {
        var verification = await _snapshotService.VerifySnapshotAsync(id, cancellationToken);
        return verification == null ? NotFound() : Ok(verification);
    }
}
