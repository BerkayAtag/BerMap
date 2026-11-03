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

    [HttpPost("run")]
    public async Task<ActionResult<ImpactAnalysisResultDto>> RunSimulation([FromBody] SimulationRunRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _simulationEngine.RunSimulationAsync(request, cancellationToken);
        return Ok(result);
    }
}
