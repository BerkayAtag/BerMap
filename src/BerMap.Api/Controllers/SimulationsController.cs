using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;

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
    /// Runs one scenario against the live graph. A missing target or a scenario
    /// that cannot fail the target's node type is rejected with 400
    /// (invalid_simulation); an unknown target node returns 404.
    /// </summary>
    [HttpPost("run")]
    public async Task<ActionResult<ImpactAnalysisResultDto>> RunSimulation([FromBody] SimulationRunRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _simulationEngine.RunSimulationAsync(request, cancellationToken);
        return Ok(result);
    }
}
