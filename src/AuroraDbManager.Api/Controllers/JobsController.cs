using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Errors;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Api.Controllers;

[ApiController]
[Route("api/v1/jobs")]
[Produces("application/json")]
public sealed class JobsController(JobService jobs) : ControllerBase
{
    /// <summary>Lists background jobs, newest first.</summary>
    /// <remarks>
    /// The operational history of everything the server did in the background: provisioning,
    /// database creation and deletion, backups and restores. Filter by <c>status</c>, <c>type</c>,
    /// <c>instanceId</c> and <c>databaseId</c>, in any combination; a failed job carries its
    /// stable error code. The result is always one page.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<JobListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] ListJobsQuery query, CancellationToken cancellationToken)
    {
        return Ok(await jobs.ListAsync(query, cancellationToken));
    }

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
