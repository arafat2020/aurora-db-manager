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
    /// job, which runs the database in a Docker container. The instance becomes <c>running</c> once
    /// the database accepts connections, or <c>failed</c> when the job has used all its attempts. Follow the job with <c>GET /api/v1/jobs/{id}</c>; the
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

    /// <summary>Deletes an instance, its database server and all of its data.</summary>
    /// <remarks>
    /// An instance in status <c>provisioning</c> cannot be deleted; the request is rejected with
    /// <c>409 INSTANCE_PROVISIONING</c>, and neither can one with a database that is being created
    /// or deleted (<c>409 DATABASE_OPERATION_IN_PROGRESS</c>) or backed up
    /// (<c>409 BACKUP_OPERATION_IN_PROGRESS</c>). The instance's databases are destroyed together
    /// with its data volume, and the records of their backups are removed; backup files are kept. If the instance's Docker resources cannot be removed, for
    /// example because Docker is unavailable, the request fails with <c>503</c> and nothing is deleted.
    /// </remarks>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return await instances.DeleteAsync(id, cancellationToken) switch
            {
                DeleteInstanceResult.Deleted => NoContent(),
                DeleteInstanceResult.Provisioning => Conflict(ApiErrorResponse.Create(
                    ErrorCodes.InstanceProvisioning,
                    "Instance cannot be deleted while provisioning is in progress.")),
                DeleteInstanceResult.DatabaseOperationInProgress => Conflict(ApiErrorResponse.Create(
                    ErrorCodes.DatabaseOperationInProgress,
                    "Instance cannot be deleted while one of its databases is being created or deleted.")),
                DeleteInstanceResult.BackupInProgress => Conflict(ApiErrorResponse.Create(
                    ErrorCodes.BackupOperationInProgress,
                    "Instance cannot be deleted while one of its databases is being backed up.")),
                _ => InstanceNotFound()
            };
        }
        catch (InstanceProvisioningException exception)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                ApiErrorResponse.Create(exception.Code, exception.Message));
        }
    }

    private NotFoundObjectResult InstanceNotFound() =>
        NotFound(ApiErrorResponse.Create(ErrorCodes.InstanceNotFound, "Instance was not found."));
}
