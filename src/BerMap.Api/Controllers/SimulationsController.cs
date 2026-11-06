using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Simulation;

namespace BerMap.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SimulationsController : ControllerBase
{
    private readonly ISimulationEngine _simulationEngine;

    public SimulationsController(ISimulationEngine simulationEngine)
    {
        _simulationEngine = simulationEngine;
    }

    /// <summary>
    /// Runs one scenario against the live graph, or against a snapshot's frozen
    /// graph when <c>snapshotId</c> is set. A missing target or a scenario that
    /// cannot fail the target's node type is rejected with 400
    /// (invalid_simulation); an unknown target node returns 404 (not_found), an
    /// unknown snapshot 404 (snapshot_not_found) and a target the snapshot did
    /// not capture 404 (not_in_snapshot).
    /// </summary>
    [HttpPost("run")]
    public async Task<ActionResult<ImpactAnalysisResultDto>> RunSimulation([FromBody] SimulationRunRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _simulationEngine.RunSimulationAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (SimulationNotFoundException notFound)
        {
            // Same problem shape as the global exception handler, with the
            // specific code instead of the generic not_found.
            return new ObjectResult(new
            {
                type = $"https://bermap.local/errors/{notFound.Code}",
                title = notFound.Message,
                status = StatusCodes.Status404NotFound,
                code = notFound.Code,
                traceId = HttpContext.TraceIdentifier
            })
            {
                StatusCode = StatusCodes.Status404NotFound,
                ContentTypes = { "application/problem+json" }
            };
        }
    }
}
