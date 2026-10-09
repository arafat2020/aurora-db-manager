using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Connectivity;
using AuroraDbManager.Api.Application.Credentials;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Api.Controllers;

[ApiController]
[Route("api/v1/instances")]
[Produces("application/json")]
[Authorize(Policy = AuroraPolicies.Viewer)]
public sealed class InstancesController(
    InstanceService instances,
    InstanceHealthService health,
    InstanceConnectivityService connectivity,
    CredentialRotationService credentials) : ControllerBase
{
    /// <summary>Creates an instance and starts provisioning it in the background.</summary>
    /// <remarks>
    /// The instance is stored with status <c>provisioning</c> together with a <c>provision_instance</c>
    /// job, which runs the database in a Docker container. The instance becomes <c>running</c> once
    /// the database accepts connections, or <c>failed</c> when the job has used all its attempts. Follow the job with <c>GET /api/v1/jobs/{id}</c>; the
    /// <c>Location</c> header points to the instance.
    /// </remarks>
    [Authorize(Policy = AuroraPolicies.Admin)]
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

    /// <summary>Reports how the instance's database server is doing right now.</summary>
    /// <remarks>
    /// Looks at the instance's container and asks its database whether it accepts connections,
    /// once, at the time of the request. The answer is <c>200</c> whatever the health is; it is
    /// in <c>status</c>. <c>instanceStatus</c> is the instance's status on record and may disagree
    /// with what was observed: this request never changes it, and repairs nothing.
    /// </remarks>
    [HttpGet("{id:guid}/health")]
    [ProducesResponseType<InstanceHealthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Health(Guid id, CancellationToken cancellationToken)
    {
        var report = await health.GetAsync(id, cancellationToken);
        return report is null ? InstanceNotFound() : Ok(report);
    }

    /// <summary>Says how the instance's database server is reached.</summary>
    /// <remarks>
    /// <c>internal</c> is where the server is for containers attached to the instance network, and
    /// is always there. <c>external</c> says whether the server's port is published on the Docker
    /// host, and if so on which port and where clients are told to connect. A published port is
    /// not thereby reachable from anywhere: that also depends on the address ports are bound to
    /// (<c>bindAddress</c>) and on firewalls and networks, which Aurora does not configure. No
    /// password is ever part of the response.
    /// </remarks>
    [HttpGet("{id:guid}/connection")]
    [ProducesResponseType<InstanceConnectionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Connection(Guid id, CancellationToken cancellationToken)
    {
        var connection = await connectivity.GetAsync(id, cancellationToken);
        return connection is null ? InstanceNotFound() : Ok(connection);
    }

    /// <summary>Publishes the instance's database port on a port of the Docker host.</summary>
    /// <remarks>
    /// Aurora picks the host port, from the configured range; a request cannot choose a port or an
    /// address. The port is bound to the server's configured address (<c>ExternalAccess:BindAddress</c>,
    /// by default <c>127.0.0.1</c>). <b>The instance's database server is restarted</b>, on the same
    /// data, and is unavailable until it accepts connections again; the response is sent when it
    /// does. The instance must be <c>running</c> (<c>409 INSTANCE_NOT_READY</c>) with no job in
    /// progress (<c>409 DATABASE_OPERATION_IN_PROGRESS</c>, <c>BACKUP_OPERATION_IN_PROGRESS</c>,
    /// <c>RESTORE_OPERATION_IN_PROGRESS</c>), and not have external access already
    /// (<c>409 EXTERNAL_ACCESS_ALREADY_ENABLED</c>). If no port of the range is free the request
    /// fails with <c>409 PORT_ALLOCATION_FAILED</c>; if Docker cannot apply the change, with
    /// <c>503</c> and for instance <c>DOCKER_PORT_CONFIGURATION_FAILED</c>. A request that fails
    /// changes nothing: the server keeps running, or is put back, as it was.
    /// </remarks>
    [Authorize(Policy = AuroraPolicies.Admin)]
    [HttpPost("{id:guid}/external-access")]
    [ProducesResponseType<InstanceConnectionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> EnableExternalAccess(Guid id, CancellationToken cancellationToken) =>
        ExternalAccessChanged(await connectivity.EnableAsync(id, cancellationToken));

    /// <summary>Stops publishing the instance's database port.</summary>
    /// <remarks>
    /// The host port is given up and may be allocated to another instance afterwards. <b>The
    /// instance's database server is restarted</b>, on the same data. The same conditions apply as
    /// for enabling; an instance without external access is answered with
    /// <c>409 EXTERNAL_ACCESS_ALREADY_DISABLED</c>.
    /// </remarks>
    [Authorize(Policy = AuroraPolicies.Admin)]
    [HttpDelete("{id:guid}/external-access")]
    [ProducesResponseType<InstanceConnectionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> DisableExternalAccess(Guid id, CancellationToken cancellationToken) =>
        ExternalAccessChanged(await connectivity.DisableAsync(id, cancellationToken));

    private IActionResult ExternalAccessChanged(ExternalAccessResult result)
    {
        if (result.Status == ExternalAccessStatus.Changed)
        {
            return Ok(result.Connection);
        }

        if (result.Status == ExternalAccessStatus.NotFound)
        {
            return InstanceNotFound();
        }

        var (status, code, message) = ExternalAccessErrors.For(result);
        return StatusCode(status, ApiErrorResponse.Create(code, message));
    }

    /// <summary>Describes the credential Aurora manages for the instance. Never the password.</summary>
    /// <remarks>
    /// The credential is the instance's database administrator, <c>postgres</c> or <c>root</c>, the
    /// account everything of Aurora connects with. Its password is generated by Aurora, stored
    /// encrypted, and not returned here. <c>result</c> says whether the password of the last completed
    /// rotation can still be retrieved, once, from <c>POST …/credentials/rotate/{jobId}/result</c>. <c>rotation</c> says where it stands: <c>idle</c>,
    /// <c>in_progress</c>, or <c>incomplete</c> when the last rotation failed part-way, in which
    /// case rotating again finishes it.
    /// </remarks>
    [HttpGet("{id:guid}/credentials")]
    [ProducesResponseType<InstanceCredentialResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Credential(Guid id, CancellationToken cancellationToken)
    {
        var credential = await credentials.GetAsync(id, cancellationToken);
        return credential is null ? InstanceNotFound() : Ok(credential);
    }

    /// <summary>Rotates the password of the instance's database administrator.</summary>
    /// <remarks>
    /// Aurora generates a new password, changes the administrator's password to it in the database
    /// server, and stores it in place of the old one. <b>The new password is not returned here</b>,
    /// and the request cannot choose it: once the job has completed it can be retrieved, once,
    /// from <c>POST …/credentials/rotate/{jobId}/result</c>. Once the rotation has
    /// succeeded the old password no longer works: anything outside Aurora that connects as the
    /// administrator with it stops being able to. The server is not restarted and no data is touched.
    /// The work is done by a <c>rotate_credential</c> job; follow it with <c>GET /api/v1/jobs/{id}</c>.
    /// The instance must be <c>running</c> (<c>409 INSTANCE_NOT_READY</c>), have no rotation
    /// unfinished (<c>409 CREDENTIAL_ROTATION_IN_PROGRESS</c>) and no database being created,
    /// deleted, backed up or restored (<c>409 DATABASE_OPERATION_IN_PROGRESS</c>,
    /// <c>BACKUP_OPERATION_IN_PROGRESS</c>, <c>RESTORE_OPERATION_IN_PROGRESS</c>). A job that fails
    /// says why in its error: <c>CREDENTIAL_ROTATION_DATABASE_FAILED</c>,
    /// <c>CREDENTIAL_ROTATION_VERIFICATION_FAILED</c>, <c>CREDENTIAL_ROTATION_SECRET_STORE_FAILED</c>,
    /// <c>CREDENTIAL_ROTATION_RECOVERY_REQUIRED</c>, or the code of the connection failure.
    /// </remarks>
    [Authorize(Policy = AuroraPolicies.Operator)]
    [HttpPost("{id:guid}/credentials/rotate")]
    [ProducesResponseType<CredentialRotationResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RotateCredential(Guid id, CancellationToken cancellationToken)
    {
        var result = await credentials.RequestAsync(id, cancellationToken);
        if (result.Status == RotateCredentialStatus.Accepted)
        {
            return AcceptedAtAction(nameof(JobsController.Get), "Jobs", new { id = result.Operation!.Job.Id }, result.Operation);
        }

        if (result.Status == RotateCredentialStatus.NotFound)
        {
            return InstanceNotFound();
        }

        var (status, code, message) = CredentialRotationErrors.For(result.Status);
        return StatusCode(status, ApiErrorResponse.Create(code, message));
    }

    /// <summary>Returns the new password of a completed rotation. Once.</summary>
    /// <remarks>
    /// The only endpoint that returns an instance's password. It answers for a
    /// <c>rotate_credential</c> job of this instance that has <c>completed</c>, with the
    /// administrator's username and the password that rotation put in place, and from then on never
    /// again: a second request, by anyone, gets <c>409 CREDENTIAL_RESULT_ALREADY_RETRIEVED</c>, and of
    /// requests made at the same moment one is answered with the password. Any operator or
    /// administrator may retrieve it, not only the one who asked for the rotation. It can be
    /// retrieved for a limited time after the rotation completed (<c>Credentials:ResultTtlMinutes</c>,
    /// by default 15 minutes), after which the answer is <c>409 CREDENTIAL_RESULT_EXPIRED</c>; the
    /// password itself goes on working. A rotation that failed or is unfinished, or whose password a
    /// newer rotation has replaced, has nothing to retrieve (<c>409 CREDENTIAL_RESULT_NOT_AVAILABLE</c>).
    /// A job that is not a rotation of this instance is <c>404 JOB_NOT_FOUND</c>. It is a
    /// <c>POST</c> because it changes what the next request gets. The response is not to be cached or logged.
    /// </remarks>
    [Authorize(Policy = AuroraPolicies.Operator)]
    [HttpPost("{id:guid}/credentials/rotate/{jobId:guid}/result")]
    [ProducesResponseType<CredentialRotationResultResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RetrieveRotationResult(Guid id, Guid jobId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await credentials.RetrieveResultAsync(id, jobId, cancellationToken);
        if (result.Status == RetrieveCredentialStatus.Retrieved)
        {
            return Ok(result.Result);
        }

        if (result.Status == RetrieveCredentialStatus.NotFound)
        {
            return NotFound(ApiErrorResponse.Create(ErrorCodes.JobNotFound, "Job was not found."));
        }

        var (status, code, message) = CredentialRotationErrors.For(result.Status);
        return StatusCode(status, ApiErrorResponse.Create(code, message));
    }

    /// <summary>Deletes an instance, its database server and all of its data.</summary>
    /// <remarks>
    /// An instance in status <c>provisioning</c> cannot be deleted; the request is rejected with
    /// <c>409 INSTANCE_PROVISIONING</c>, and neither can one with a database that is being created
    /// or deleted (<c>409 DATABASE_OPERATION_IN_PROGRESS</c>) or backed up
    /// (<c>409 BACKUP_OPERATION_IN_PROGRESS</c>) or restored (<c>409 RESTORE_OPERATION_IN_PROGRESS</c>), or whose password is being rotated (<c>409 CREDENTIAL_ROTATION_IN_PROGRESS</c>). The instance's databases are destroyed together
    /// with its data volume, and the records of their backups are removed; backup files are kept. If the instance's Docker resources cannot be removed, for
    /// example because Docker is unavailable, the request fails with <c>503</c> and nothing is deleted.
    /// </remarks>
    [Authorize(Policy = AuroraPolicies.Admin)]
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
                DeleteInstanceResult.RestoreInProgress => Conflict(ApiErrorResponse.Create(
                    ErrorCodes.RestoreOperationInProgress,
                    "Instance cannot be deleted while one of its databases is being restored.")),
                DeleteInstanceResult.CredentialRotationInProgress => Conflict(ApiErrorResponse.Create(
                    ErrorCodes.CredentialRotationInProgress,
                    "Instance cannot be deleted while its database password is being rotated.")),
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
