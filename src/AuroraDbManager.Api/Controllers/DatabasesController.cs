using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Connectivity;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Api.Controllers;

[ApiController]
[Produces("application/json")]
[Authorize(Policy = AuroraPolicies.Viewer)]
public sealed class DatabasesController(DatabaseService databases, InstanceConnectivityService connectivity) : ControllerBase
{
    private const string InstanceDatabasesRoute = "api/v1/instances/{instanceId:guid}/databases";
    private const string DatabaseRoute = "api/v1/databases/{id:guid}";

    /// <summary>Creates a database in a running instance, in the background.</summary>
    /// <remarks>
    /// The database is stored with status <c>creating</c> together with a <c>create_database</c>
    /// job, which creates it in the instance's database server. It becomes <c>ready</c> once it
    /// exists there, or <c>failed</c> when the job has used all its attempts. Follow the job with
    /// <c>GET /api/v1/jobs/{id}</c>; the <c>Location</c> header points to the database. The instance
    /// must be <c>running</c>; otherwise the request is rejected with <c>409 INSTANCE_NOT_READY</c>.
    /// The name must be unique within the instance, start with a lowercase letter and contain only
    /// lowercase letters, digits and underscores, up to 63 characters.
    /// </remarks>
    [Authorize(Policy = AuroraPolicies.Operator)]
    [HttpPost(InstanceDatabasesRoute)]
    [ProducesResponseType<DatabaseOperationResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        Guid instanceId, CreateDatabaseRequest request, CancellationToken cancellationToken)
    {
        var result = await databases.CreateAsync(instanceId, request, cancellationToken);

        return result.Status switch
        {
            CreateDatabaseStatus.Accepted =>
                AcceptedAtAction(nameof(Get), new { id = result.Operation!.Database.Id }, result.Operation),
            CreateDatabaseStatus.NameInvalid => BadRequest(ApiErrorResponse.Create(
                ErrorCodes.DatabaseNameInvalid,
                result.NameError!)),
            CreateDatabaseStatus.InstanceNotReady => InstanceNotReady(),
            CreateDatabaseStatus.AlreadyExists => Conflict(ApiErrorResponse.Create(
                ErrorCodes.DatabaseAlreadyExists,
                "The instance already has a database with this name.")),
            _ => InstanceNotFound()
        };
    }

    /// <summary>Lists the databases of an instance, newest first.</summary>
    [HttpGet(InstanceDatabasesRoute)]
    [ProducesResponseType<DatabaseListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(
        Guid instanceId, [FromQuery] ListDatabasesQuery query, CancellationToken cancellationToken)
    {
        var page = await databases.ListAsync(instanceId, query, cancellationToken);
        return page is null ? InstanceNotFound() : Ok(page);
    }

    /// <summary>Returns a single database.</summary>
    [HttpGet(DatabaseRoute)]
    [ProducesResponseType<DatabaseResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var database = await databases.GetAsync(id, cancellationToken);
        return database is null ? DatabaseNotFound() : Ok(database);
    }

    /// <summary>Says how the database is reached.</summary>
    /// <remarks>
    /// The database is in its instance's server, so it is reached where the instance is: see
    /// <c>GET /api/v1/instances/{id}/connection</c>. In addition the response has connection URIs
    /// for this database, in which the literal placeholder <c>&lt;password&gt;</c> stands where
    /// the password goes. No password is ever part of the response.
    /// </remarks>
    [HttpGet(DatabaseRoute + "/connection")]
    [ProducesResponseType<DatabaseConnectionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Connection(Guid id, CancellationToken cancellationToken)
    {
        var connection = await connectivity.GetForDatabaseAsync(id, cancellationToken);
        return connection is null ? DatabaseNotFound() : Ok(connection);
    }

    /// <summary>Deletes a database and its data, in the background.</summary>
    /// <remarks>
    /// Only a <c>ready</c> database can be deleted. It becomes <c>deleting</c> and a
    /// <c>delete_database</c> job drops it from the instance's database server; the database stays
    /// visible until that has succeeded and returns <c>404</c> afterwards. If the job fails for good
    /// the database becomes <c>failed</c> and is kept. A database that is <c>creating</c>,
    /// <c>deleting</c> or <c>failed</c> is rejected with <c>409 DATABASE_CREATING</c>,
    /// <c>DATABASE_DELETING</c> or <c>DATABASE_FAILED</c>, one whose instance is not
    /// <c>running</c> with <c>409 INSTANCE_NOT_READY</c>, one with an unfinished backup with
    /// <c>409 BACKUP_OPERATION_IN_PROGRESS</c>, and one that is being restored with
    /// <c>409 RESTORE_OPERATION_IN_PROGRESS</c>. Deleting a database removes the records of its
    /// backups; the backup files stay in the backup storage.
    /// </remarks>
    [Authorize(Policy = AuroraPolicies.Operator)]
    [HttpDelete(DatabaseRoute)]
    [ProducesResponseType<DatabaseOperationResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await databases.DeleteAsync(id, cancellationToken);

        return result.Status switch
        {
            DeleteDatabaseStatus.Accepted => Accepted(result.Operation),
            DeleteDatabaseStatus.Creating => Conflict(ApiErrorResponse.Create(
                ErrorCodes.DatabaseCreating,
                "Database cannot be deleted while it is being created.")),
            DeleteDatabaseStatus.Deleting => Conflict(ApiErrorResponse.Create(
                ErrorCodes.DatabaseDeleting,
                "Database is already being deleted.")),
            DeleteDatabaseStatus.Failed => Conflict(ApiErrorResponse.Create(
                ErrorCodes.DatabaseFailed,
                "Database is in a failed state and cannot be deleted.")),
            DeleteDatabaseStatus.InstanceNotReady => InstanceNotReady(),
            DeleteDatabaseStatus.BackupInProgress => Conflict(ApiErrorResponse.Create(
                ErrorCodes.BackupOperationInProgress,
                "Database cannot be deleted while a backup of it is in progress.")),
            DeleteDatabaseStatus.RestoreInProgress => Conflict(ApiErrorResponse.Create(
                ErrorCodes.RestoreOperationInProgress,
                "Database cannot be deleted while it is being restored.")),
            _ => DatabaseNotFound()
        };
    }

    private ConflictObjectResult InstanceNotReady() =>
        Conflict(ApiErrorResponse.Create(
            ErrorCodes.InstanceNotReady,
            "Database operations need a running instance."));

    private NotFoundObjectResult InstanceNotFound() =>
        NotFound(ApiErrorResponse.Create(ErrorCodes.InstanceNotFound, "Instance was not found."));

    private NotFoundObjectResult DatabaseNotFound() =>
        NotFound(ApiErrorResponse.Create(ErrorCodes.DatabaseNotFound, "Database was not found."));
}
