using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Errors;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Api.Controllers;

[ApiController]
[Route("api/v1/jobs")]
[Produces("application/json")]
public sealed class JobsController(JobService jobs) : ControllerBase
{
    /// <summary>Returns the current state of a background job.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<JobResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(id, cancellationToken);
        return job is null
            ? NotFound(ApiErrorResponse.Create(ErrorCodes.JobNotFound, "Job was not found."))
            : Ok(job);
    }
}
