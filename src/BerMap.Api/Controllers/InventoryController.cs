using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace BerMap.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class InventoryController : ControllerBase
{
    private readonly IInventoryService _inventoryService;

    public InventoryController(IInventoryService inventoryService)
    {
        _inventoryService = inventoryService;
    }

    [HttpGet("nodes")]
    public async Task<ActionResult<InventoryPageDto>> ListNodes(
        [FromQuery] string? query,
        [FromQuery] string? nodeType,
        [FromQuery] string? status,
        [FromQuery] bool? isStale,
        [FromQuery] bool? needsReview,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await _inventoryService.ListNodesAsync(new InventoryQueryRequest
        {
            Query = query,
            NodeType = nodeType,
            Status = status,
            IsStale = isStale,
            NeedsReview = needsReview,
            Page = page,
            PageSize = pageSize
        }, cancellationToken);

        return Ok(result);
    }

    [HttpGet("nodes/{id:guid}")]
    public async Task<ActionResult<InventoryNodeDetailDto>> GetNode(Guid id, CancellationToken cancellationToken)
    {
        var node = await _inventoryService.GetNodeAsync(id, cancellationToken);
        return node == null ? NotFound() : Ok(node);
    }

    [HttpGet("reviews")]
    public async Task<ActionResult<List<ReconciliationReviewDto>>> ListReviews(
        [FromQuery] string? status,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _inventoryService.ListReviewsAsync(status, limit, cancellationToken));
    }

    [HttpPost("reviews/{id:guid}/resolve")]
    public async Task<ActionResult<ReconciliationReviewDto>> ResolveReview(
        Guid id,
        [FromBody] ResolveReconciliationReviewRequest request,
        CancellationToken cancellationToken)
    {
        var review = await _inventoryService.ResolveReviewAsync(id, request, this.GetActor(), cancellationToken);
        return review == null ? NotFound() : Ok(review);
    }
}
