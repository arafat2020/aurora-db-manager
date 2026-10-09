using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Restores;

/// <summary>
/// Accepts restore requests and hands them to the job system. A request names a backup and
/// nothing else: the database restored into is the one the backup was made of, never one the
/// client picks. Nothing here touches a database engine or the backup storage; the job's handler
/// does that through <see cref="IRestoreManager"/>. The job is the restore: there is no other
/// record of it, and its status is the restore's status.
/// </summary>
public sealed class RestoreService(
    AppDbContext db,
    IBackupStorageResolver storages,
    JobQueue jobQueue,
    IOptions<JobOptions> jobOptions,
    TimeProvider timeProvider,
    ILogger<RestoreService> logger)
{
    public async Task<CreateRestoreResult> CreateAsync(Guid backupId, CancellationToken cancellationToken)
    {
        var backup = await db.Backups.AsNoTracking().FirstOrDefaultAsync(b => b.Id == backupId, cancellationToken);
        if (backup is null)
        {
            return new CreateRestoreResult(CreateRestoreStatus.BackupNotFound);
        }

        if (backup.Status != BackupStatus.Completed)
        {
            return new CreateRestoreResult(CreateRestoreStatus.BackupNotCompleted);
        }

        // The artifact is read from the storage the backup is in, which need not be the server's
        // default for new backups. What is required is that the server can use that storage at all.
        try
        {
            storages.Resolve(backup.StorageType);
        }
        catch (BackupOperationException exception) when (exception.Code == BackupErrorCodes.BackupStorageNotConfigured)
        {
            logger.LogWarning(
                exception, "Backup {BackupId} is in {StorageType} storage, which is not configured", backup.Id, backup.StorageType);
            return new CreateRestoreResult(CreateRestoreStatus.StorageNotConfigured);
        }

        // The target is the backup's own database. A backup row cannot outlive its database, so it is there.
        var database = await db.Databases.AsNoTracking().FirstAsync(d => d.Id == backup.DatabaseId, cancellationToken);
        if (database.Status != DatabaseStatus.Ready)
        {
            return new CreateRestoreResult(CreateRestoreStatus.DatabaseNotReady);
        }

        var instance = await db.Instances.AsNoTracking().FirstAsync(i => i.Id == database.InstanceId, cancellationToken);
        if (instance.Status != InstanceStatus.Running)
        {
            return new CreateRestoreResult(CreateRestoreStatus.InstanceNotReady);
        }

        if (Rejection(await db.UnfinishedJobTypeAsync(database.Id, cancellationToken)) is { } busy)
        {
            return new CreateRestoreResult(busy);
        }

        if (await db.CredentialRotationUnfinishedAsync(database.InstanceId, cancellationToken))
        {
            return new CreateRestoreResult(CreateRestoreStatus.CredentialRotationInProgress);
        }

        var job = Job.Create(
            JobType.RestoreDatabase,
            database.InstanceId,
            jobOptions.Value.MaxAttempts,
            timeProvider.GetUtcNow().UtcDateTime,
            database.Id,
            backup.Id);
        db.Jobs.Add(job);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The checks above can be overtaken by a concurrent request. A database has only one
            // unfinished job, so whatever got in first, another restore, a backup or the
            // database's deletion, made the index refuse this job. Find out which it was.
            db.ChangeTracker.Clear();

            var current = await db.Databases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == database.Id, cancellationToken);
            if (current is null)
            {
                return new CreateRestoreResult(CreateRestoreStatus.BackupNotFound);
            }

            if (current.Status != DatabaseStatus.Ready)
            {
                return new CreateRestoreResult(CreateRestoreStatus.DatabaseNotReady);
            }

            if (Rejection(await db.UnfinishedJobTypeAsync(database.Id, cancellationToken)) is { } concurrent)
            {
                return new CreateRestoreResult(concurrent);
            }

            throw;
        }

        // Queued only after the commit, so the worker never sees a job that is not in the database.
        // If the process dies between the commit and this line the job stays pending until the
        // next start, when job recovery queues it. Not cancellable: the job is already committed.
        await jobQueue.EnqueueAsync(job.Id, CancellationToken.None);
        logger.LogInformation(
            "Job {JobId} ({JobType}) restoring backup {BackupId} into database {DatabaseId} of instance {InstanceId} queued",
            job.Id, job.Type, backup.Id, database.Id, job.InstanceId);

        return new CreateRestoreResult(
            CreateRestoreStatus.Accepted,
            new RestoreResponse(DatabaseResponse.From(database), JobResponse.From(job)));
    }

    /// <summary>Why a database with that unfinished job cannot be restored into; null if it has none.</summary>
    private static CreateRestoreStatus? Rejection(JobType? unfinished) => unfinished switch
    {
        null => null,
        JobType.RestoreDatabase => CreateRestoreStatus.RestoreInProgress,
        JobType.BackupDatabase => CreateRestoreStatus.BackupInProgress,
        _ => CreateRestoreStatus.DatabaseNotReady
    };
}

/// <param name="Status">Whether the restore was accepted, and if not, why.</param>
/// <param name="Operation">The target database and the job; set only when <paramref name="Status"/> is <see cref="CreateRestoreStatus.Accepted"/>.</param>
public sealed record CreateRestoreResult(CreateRestoreStatus Status, RestoreResponse? Operation = null);

public enum CreateRestoreStatus
{
    Accepted,
    BackupNotFound,

    /// <summary>Not accepted because the backup is not <c>completed</c>.</summary>
    BackupNotCompleted,

    /// <summary>Not accepted because the server has no usable settings for the storage the backup is in.</summary>
    StorageNotConfigured,

    /// <summary>Not accepted because the backup's database is not <c>ready</c>.</summary>
    DatabaseNotReady,

    /// <summary>Not accepted because the instance is not running.</summary>
    InstanceNotReady,

    /// <summary>Not accepted because the database is already being restored.</summary>
    RestoreInProgress,

    /// <summary>Not accepted because the database is being backed up.</summary>
    BackupInProgress,
    /// <summary>Not accepted because the password of the instance's database administrator is being rotated.</summary>
    CredentialRotationInProgress
}
