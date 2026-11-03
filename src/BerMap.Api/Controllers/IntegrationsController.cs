using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace BerMap.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class IntegrationsController : ControllerBase
{
    private readonly IIntegrationService _integrationService;
    private readonly IIntegrationAdapterRegistry _registry;

    public IntegrationsController(
        IIntegrationService integrationService,
        IIntegrationAdapterRegistry registry)
    {
        _integrationService = integrationService;
        _registry = registry;
    }

    [HttpGet]
    public async Task<ActionResult<List<IntegrationDto>>> List(CancellationToken cancellationToken)
    {
        return Ok(await _integrationService.ListAsync(cancellationToken));
    }

    [HttpGet("adapters")]
    public ActionResult<IReadOnlyList<AdapterDescriptorDto>> GetAdapters()
    {
        return Ok(_registry.GetDescriptors());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<IntegrationDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var integration = await _integrationService.GetAsync(id, cancellationToken);
        return integration == null ? NotFound() : Ok(integration);
    }

    [HttpPost]
    public async Task<ActionResult<IntegrationDto>> Create(
        [FromBody] CreateIntegrationRequest request,
        CancellationToken cancellationToken)
    {
        var integration = await _integrationService.CreateAsync(request, this.GetActor(), cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = integration.Id }, integration);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<IntegrationDto>> Update(
        Guid id,
        [FromBody] UpdateIntegrationRequest request,
        CancellationToken cancellationToken)
    {
        var integration = await _integrationService.UpdateAsync(id, request, this.GetActor(), cancellationToken);
        return integration == null ? NotFound() : Ok(integration);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await _integrationService.DeleteAsync(id, this.GetActor(), cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>Tests a draft integration configuration without saving it.</summary>
    [HttpPost("test-connection")]
    public async Task<ActionResult<ConnectionTestResult>> TestDraft(
        [FromBody] TestConnectionRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _integrationService.TestDraftAsync(request, cancellationToken));
    }

    /// <summary>Tests a saved integration using the stored credential, or an override.</summary>
    [HttpPost("{id:guid}/test")]
    public async Task<ActionResult<ConnectionTestResult>> TestExisting(
        Guid id,
        [FromBody] TestExistingIntegrationRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _integrationService.TestAsync(
            id, request?.Secret, this.GetActor(), cancellationToken);

        return result == null ? NotFound() : Ok(result);
    }

    [HttpPost("{id:guid}/discover")]
    public async Task<ActionResult<DiscoveryJobDto>> StartDiscovery(
        Guid id,
        [FromBody] StartDiscoveryRequest? request,
        CancellationToken cancellationToken)
    {
        var job = await _integrationService.StartDiscoveryAsync(
            id, request ?? new StartDiscoveryRequest(), this.GetActor(), cancellationToken);

        return job == null ? NotFound() : AcceptedAtAction(nameof(GetJob), new { jobId = job.Id }, job);
    }

    [HttpGet("jobs")]
    public async Task<ActionResult<List<DiscoveryJobDto>>> ListJobs(
        [FromQuery] Guid? integrationId,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _integrationService.ListJobsAsync(integrationId, limit, cancellationToken));
    }

    [HttpGet("jobs/{jobId:guid}")]
    public async Task<ActionResult<DiscoveryJobDto>> GetJob(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _integrationService.GetJobAsync(jobId, cancellationToken);
        return job == null ? NotFound() : Ok(job);
    }

    [HttpPost("jobs/{jobId:guid}/cancel")]
    public async Task<IActionResult> CancelJob(Guid jobId, CancellationToken cancellationToken)
    {
        var cancelled = await _integrationService.CancelJobAsync(jobId, this.GetActor(), cancellationToken);
        return cancelled ? Accepted() : Conflict(new { message = "Job is not queued or running." });
    }
}
