using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Errors;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Api.Controllers;

[ApiController]
[Produces("application/json")]
public sealed class BackupsController(BackupService backups) : ControllerBase
{
    private const string DatabaseBackupsRoute = "api/v1/databases/{databaseId:guid}/backups";
    private const string BackupRoute = "api/v1/backups/{id:guid}";

    /// <summary>Backs up a database, in the background.</summary>
    /// <remarks>
    /// The backup is stored with status <c>pending</c> together with a <c>backup_database</c> job,
    /// which dumps the database to the server's local backup storage. The backup becomes
    /// <c>running</c> when the job picks it up, then <c>completed</c>, with its size, or
    /// <c>failed</c> when the job has used all its attempts. Follow the job with
    /// <c>GET /api/v1/jobs/{id}</c>; the <c>Location</c> header points to the backup. The request
    /// takes no body: where and how a backup is stored is not for the client to choose. The
    /// database must be <c>ready</c> (<c>409 DATABASE_NOT_READY</c>) and its instance
    /// <c>running</c> (<c>409 INSTANCE_NOT_READY</c>), and a database has one unfinished backup
    /// at a time (<c>409 BACKUP_OPERATION_IN_PROGRESS</c>).
    /// </remarks>
    [HttpPost(DatabaseBackupsRoute)]
    [ProducesResponseType<CreateBackupResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(Guid databaseId, CancellationToken cancellationToken)
    {
        var result = await backups.CreateAsync(databaseId, cancellationToken);

        return result.Status switch
        {
            CreateBackupStatus.Accepted =>
                AcceptedAtAction(nameof(Get), new { id = result.Operation!.Backup.Id }, result.Operation),
            CreateBackupStatus.DatabaseNotReady => Conflict(ApiErrorResponse.Create(
                ErrorCodes.DatabaseNotReady,
                "Only a ready database can be backed up.")),
            CreateBackupStatus.InstanceNotReady => Conflict(ApiErrorResponse.Create(
                ErrorCodes.InstanceNotReady,
                "Backups need a running instance.")),
            CreateBackupStatus.BackupInProgress => Conflict(ApiErrorResponse.Create(
                ErrorCodes.BackupOperationInProgress,
                "The database already has a backup in progress.")),
            _ => DatabaseNotFound()
        };
    }

    /// <summary>Lists the backups of a database, newest first.</summary>
    [HttpGet(DatabaseBackupsRoute)]
    [ProducesResponseType<BackupListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(
        Guid databaseId, [FromQuery] ListBackupsQuery query, CancellationToken cancellationToken)
    {
        var page = await backups.ListAsync(databaseId, query, cancellationToken);
        return page is null ? DatabaseNotFound() : Ok(page);
    }

    /// <summary>Returns a single backup.</summary>
    [HttpGet(BackupRoute)]
    [ProducesResponseType<BackupResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var backup = await backups.GetAsync(id, cancellationToken);
        return backup is null
            ? NotFound(ApiErrorResponse.Create(ErrorCodes.BackupNotFound, "Backup was not found."))
            : Ok(backup);
    }

    private NotFoundObjectResult DatabaseNotFound() =>
        NotFound(ApiErrorResponse.Create(ErrorCodes.DatabaseNotFound, "Database was not found."));
}
