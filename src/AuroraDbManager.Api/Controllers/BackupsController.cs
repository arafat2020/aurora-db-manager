using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Restores;
using AuroraDbManager.Api.Errors;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Api.Controllers;

[ApiController]
[Produces("application/json")]
public sealed class BackupsController(BackupService backups, RestoreService restores) : ControllerBase
{
    private const string DatabaseBackupsRoute = "api/v1/databases/{databaseId:guid}/backups";
    private const string BackupRoute = "api/v1/backups/{id:guid}";
    private const string RestoreRoute = "api/v1/backups/{id:guid}/restore";

    /// <summary>Backs up a database, in the background.</summary>
    /// <remarks>
    /// The backup is stored with status <c>pending</c> together with a <c>backup_database</c> job,
    /// which dumps the database to the server's local backup storage. The backup becomes
    /// <c>running</c> when the job picks it up, then <c>completed</c>, with its size and the
    /// SHA-256 of the stored backup, which was read back from the storage and verified, or
    /// <c>failed</c> when the job has used all its attempts. Follow the job with
    /// <c>GET /api/v1/jobs/{id}</c>; the <c>Location</c> header points to the backup. The request
    /// takes no body: where and how a backup is stored is not for the client to choose. The
    /// database must be <c>ready</c> (<c>409 DATABASE_NOT_READY</c>) and its instance
    /// <c>running</c> (<c>409 INSTANCE_NOT_READY</c>), and a database has one unfinished backup
    /// at a time (<c>409 BACKUP_OPERATION_IN_PROGRESS</c>) and is not backed up while it is
    /// being restored (<c>409 RESTORE_OPERATION_IN_PROGRESS</c>).
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
            CreateBackupStatus.RestoreInProgress => Conflict(ApiErrorResponse.Create(
                ErrorCodes.RestoreOperationInProgress,
                "The database cannot be backed up while it is being restored.")),
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

    /// <summary>Restores a backup into the database it was made of, replacing that database's contents, in the background.</summary>
    /// <remarks>
    /// <b>Destructive.</b> Everything in the database is removed and replaced with what the backup
    /// contains: tables, data and other objects created or changed since the backup are lost, and
    /// clients connected to the database are disconnected. The target is always the backup's own
    /// database; the request takes no body, and no other database, instance or location can be
    /// named. A <c>restore_database</c> job is created; its status is the restore's status. Follow
    /// it with <c>GET /api/v1/jobs/{id}</c>, which the <c>Location</c> header points to. If the job
    /// fails, the database may be left empty or partly restored; restoring again repairs that.
    /// Before anything in the database is changed, the backup is fetched and checked against its
    /// recorded size and SHA-256; a backup that does not match is not restored. Backups completed
    /// before checksums were recorded are checked by size and format only.
    /// The backup is read from the storage it was made in, whichever storage new backups go to
    /// now. The backup must be <c>completed</c> (<c>409 BACKUP_NOT_COMPLETED</c>) and the server
    /// must have settings for that storage (<c>409 BACKUP_STORAGE_NOT_CONFIGURED</c>), its database
    /// <c>ready</c> (<c>409 DATABASE_NOT_READY</c>) and the instance <c>running</c>
    /// (<c>409 INSTANCE_NOT_READY</c>). A database has one restore at a time
    /// (<c>409 RESTORE_OPERATION_IN_PROGRESS</c>) and is not restored while it is being backed up
    /// (<c>409 BACKUP_OPERATION_IN_PROGRESS</c>).
    /// </remarks>
    [HttpPost(RestoreRoute)]
    [ProducesResponseType<RestoreResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
    {
        var result = await restores.CreateAsync(id, cancellationToken);

        return result.Status switch
        {
            CreateRestoreStatus.Accepted => AcceptedAtAction(
                nameof(JobsController.Get), "Jobs", new { id = result.Operation!.Job.Id }, result.Operation),
            CreateRestoreStatus.BackupNotCompleted => Conflict(ApiErrorResponse.Create(
                ErrorCodes.BackupNotCompleted,
                "Only a completed backup can be restored.")),
            CreateRestoreStatus.StorageNotConfigured => Conflict(ApiErrorResponse.Create(
                ErrorCodes.BackupStorageNotConfigured,
                "The backup storage the backup belongs to is not configured on the server.")),
            CreateRestoreStatus.DatabaseNotReady => Conflict(ApiErrorResponse.Create(
                ErrorCodes.DatabaseNotReady,
                "A backup can only be restored into a ready database.")),
            CreateRestoreStatus.InstanceNotReady => Conflict(ApiErrorResponse.Create(
                ErrorCodes.InstanceNotReady,
                "Restores need a running instance.")),
            CreateRestoreStatus.RestoreInProgress => Conflict(ApiErrorResponse.Create(
                ErrorCodes.RestoreOperationInProgress,
                "The database is already being restored.")),
            CreateRestoreStatus.BackupInProgress => Conflict(ApiErrorResponse.Create(
                ErrorCodes.BackupOperationInProgress,
                "The database cannot be restored while it is being backed up.")),
            _ => NotFound(ApiErrorResponse.Create(ErrorCodes.BackupNotFound, "Backup was not found."))
        };
    }

    private NotFoundObjectResult DatabaseNotFound() =>
        NotFound(ApiErrorResponse.Create(ErrorCodes.DatabaseNotFound, "Database was not found."));
}
