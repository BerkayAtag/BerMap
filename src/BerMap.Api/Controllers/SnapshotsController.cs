using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
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

    public SnapshotsController(ISnapshotService snapshotService)
    {
        _snapshotService = snapshotService;
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
        [FromQuery] Guid a,
        [FromQuery] Guid b,
        CancellationToken cancellationToken)
    {
        return Ok(await _snapshotService.CompareSnapshotsAsync(a, b, cancellationToken));
    }

    [HttpGet("{id:guid}/verify")]
    public async Task<ActionResult<SnapshotVerificationDto>> Verify(Guid id, CancellationToken cancellationToken)
    {
        var verification = await _snapshotService.VerifySnapshotAsync(id, cancellationToken);
        return verification == null ? NotFound() : Ok(verification);
    }
}
