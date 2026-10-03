using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Backups;

namespace AuroraDbManager.Api.Application.Backups;

/// <param name="Id">Unique identifier of the backup.</param>
/// <param name="DatabaseId">The database that was backed up.</param>
/// <param name="Status">State of the backup: <c>pending</c>, <c>running</c>, <c>completed</c> or <c>failed</c>.</param>
/// <param name="StorageType">Where the backup is kept: <c>local</c> or <c>s3</c>.</param>
/// <param name="SizeBytes">Size of the finished backup in bytes; null until it is <c>completed</c>.</param>
/// <param name="CreatedAt">UTC time the backup was requested.</param>
/// <param name="CompletedAt">UTC time the backup reached <c>completed</c> or <c>failed</c>; null until then.</param>
/// <param name="Error">Why the backup is <c>failed</c>; null in every other status.</param>
// Where exactly the artifact is (a path on the server, or a bucket and key) is deliberately
// absent: it is the server's configuration and of no use to a client.
public sealed record BackupResponse(
    Guid Id,
    Guid DatabaseId,
    BackupStatus Status,
    BackupStorageType StorageType,
    long? SizeBytes,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    BackupErrorResponse? Error)
{
    public static BackupResponse From(Backup backup) => new(
        backup.Id,
        backup.DatabaseId,
        backup.Status,
        backup.StorageType,
        backup.SizeBytes,
        backup.CreatedAt,
        backup.CompletedAt,
        backup.ErrorCode is null ? null : new BackupErrorResponse(backup.ErrorCode, backup.ErrorMessage ?? string.Empty));
}

/// <param name="Code">Stable, machine-readable error code, e.g. <c>BACKUP_CONNECTION_FAILED</c>.</param>
/// <param name="Message">Human-readable description of the failure.</param>
public sealed record BackupErrorResponse(string Code, string Message);

/// <param name="Items">Backups on the requested page, newest first.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Requested number of items per page.</param>
/// <param name="TotalCount">Total number of backups of the database across all pages.</param>
public sealed record BackupListResponse(
    IReadOnlyList<BackupResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <param name="Backup">The backup, in status <c>pending</c>.</param>
/// <param name="Job">The job producing it. Poll <c>GET /api/v1/jobs/{id}</c> to follow it.</param>
public sealed record CreateBackupResponse(BackupResponse Backup, JobResponse Job);
