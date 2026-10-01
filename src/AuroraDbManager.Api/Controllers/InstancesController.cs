using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Errors;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Api.Controllers;

[ApiController]
[Route("api/v1/instances")]
[Produces("application/json")]
public sealed class InstancesController(InstanceService instances) : ControllerBase
{
    /// <summary>Creates an instance and starts provisioning it in the background.</summary>
    /// <remarks>
    /// The instance is stored with status <c>provisioning</c> together with a <c>provision_instance</c>
    /// job. The instance becomes <c>running</c> when the job completes, or <c>failed</c> when the job
    /// has used all its attempts. Follow the job with <c>GET /api/v1/jobs/{id}</c>; the
    /// <c>Location</c> header points to the instance.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<CreateInstanceResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(CreateInstanceRequest request, CancellationToken cancellationToken)
    {
        var created = await instances.CreateAsync(request, cancellationToken);
        return AcceptedAtAction(nameof(Get), new { id = created.Instance.Id }, created);
    }

    /// <summary>Lists instances, newest first.</summary>
    [HttpGet]
    [ProducesResponseType<InstanceListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] ListInstancesQuery query, CancellationToken cancellationToken)
    {
        return Ok(await instances.ListAsync(query, cancellationToken));
    }

    /// <summary>Returns a single instance.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<InstanceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var instance = await instances.GetAsync(id, cancellationToken);
        return instance is null ? InstanceNotFound() : Ok(instance);
    }

    /// <summary>Deletes an instance metadata record and its jobs.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        return await instances.DeleteAsync(id, cancellationToken) ? NoContent() : InstanceNotFound();
    }

    private NotFoundObjectResult InstanceNotFound() =>
        NotFound(ApiErrorResponse.Create(ErrorCodes.InstanceNotFound, "Instance was not found."));
}
