using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;

namespace BerMap.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GraphController : ControllerBase
{
    private readonly IGraphService _graphService;

    public GraphController(IGraphService graphService)
    {
        _graphService = graphService;
    }

    [HttpGet("topology")]
    public async Task<ActionResult<GraphQueryResultDto>> GetTopology([FromQuery] Guid? centerNodeId, [FromQuery] int depth = 2, CancellationToken cancellationToken = default)
    {
        var result = await _graphService.GetTopologyGraphAsync(centerNodeId, depth, cancellationToken);
        return Ok(result);
    }

    [HttpGet("search")]
    public async Task<ActionResult> Search([FromQuery] string q, CancellationToken cancellationToken = default)
    {
        var nodes = await _graphService.SearchNodesAsync(q, cancellationToken);
        return Ok(nodes);
    }

    [HttpGet("node/{id:guid}")]
    public async Task<ActionResult<InfrastructureNodeDto>> GetNode(Guid id, CancellationToken cancellationToken = default)
    {
        var node = await _graphService.GetNodeDetailsAsync(id, cancellationToken);
        if (node == null)
            return NotFound();

        return Ok(node);
    }
}
