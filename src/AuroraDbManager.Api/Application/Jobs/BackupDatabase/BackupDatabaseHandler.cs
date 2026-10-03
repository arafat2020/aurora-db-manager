using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Application.Jobs.BackupDatabase;

/// <summary>
/// Produces the job's backup through <see cref="IBackupManager"/> and reports the result on the
/// backup: <c>completed</c> with the artifact's location, size and verified checksum, or <c>failed</c> once the job
/// has failed for good. While the job retries, the backup stays <c>running</c>. The database's
/// own status is never touched: a backup that fails says nothing about the database.
/// </summary>
/// <remarks>
/// Unlike other handlers this one saves once itself: <c>pending → running</c> is committed before
/// the dump starts, in a transaction of its own, so the dump, which can take minutes, runs with
/// no transaction open and the backup is visibly running meanwhile. The artifact's metadata and
/// <c>running → completed</c> are committed by the processor together with the job's completion.
/// </remarks>
public sealed class BackupDatabaseHandler(
    AppDbContext db,
    IEnumerable<IBackupManager> managers,
    TimeProvider timeProvider,
    ILogger<BackupDatabaseHandler> logger) : IJobHandler
{
    public JobType Type => JobType.BackupDatabase;

    public async Task ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        var backup = await db.Backups.FirstOrDefaultAsync(b => b.Id == job.BackupId, cancellationToken)
            ?? throw new JobExecutionException(BackupErrorCodes.BackupInvalidState, "The backup no longer exists.");

        // The database is taken from the backup and the instance from the database, never from
        // elsewhere, and both must be the job's.
        var database = await db.Databases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == backup.DatabaseId, cancellationToken);
        if (database is null || database.Id != job.DatabaseId || database.InstanceId != job.InstanceId)
        {
            throw new JobExecutionException(
                BackupErrorCodes.BackupInvalidState,
                "The backup does not belong to the job's database and instance.");
        }

        if (backup.Status == BackupStatus.Completed)
        {
            logger.LogInformation("Backup {BackupId} is already completed; job {JobId} has nothing to do", backup.Id, job.Id);
            return;
        }

        if (backup.Status == BackupStatus.Failed)
        {
            throw new JobExecutionException(BackupErrorCodes.BackupInvalidState, "The backup has already failed.");
        }

        // A backup that is already running was started by an earlier attempt, or by an execution
        // that was interrupted; this attempt carries on with it.
        if (backup.Status == BackupStatus.Pending)
        {
            backup.MarkRunning();
            await db.SaveChangesAsync(cancellationToken);
        }

        if (database.Status != DatabaseStatus.Ready)
        {
            throw new JobExecutionException(BackupErrorCodes.BackupDatabaseUnavailable, "The database is not ready.");
        }

        var instance = await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == database.InstanceId, cancellationToken)
            ?? throw new JobExecutionException(BackupErrorCodes.BackupDatabaseUnavailable, "The instance no longer exists.");

        // Not started here: an instance that stopped is retried, and fails the job if it stays down.
        if (instance.Status != InstanceStatus.Running)
        {
            throw new JobExecutionException(BackupErrorCodes.BackupDatabaseUnavailable, "The instance is not running.");
        }

        using var scope = logger.BeginScope(
            "Job {JobId} attempt {Attempt}: backup {BackupId} of database {DatabaseId} in {Engine} instance {InstanceId}",
            job.Id, job.Attempt, backup.Id, database.Id, instance.Engine, instance.Id);

        BackupArtifact artifact;
        try
        {
            artifact = await ManagerFor(instance).BackupAsync(instance, database, backup, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BackupOperationException exception)
        {
            // Its code and message are written for clients.
            throw new JobExecutionException(exception.Code, exception.Message, exception);
        }
        catch (Exception exception)
        {
            throw new JobExecutionException(BackupErrorCodes.BackupProcessFailed, "The backup failed.", exception);
        }

        if (artifact.SizeBytes <= 0)
        {
            throw new JobExecutionException(BackupErrorCodes.BackupArtifactInvalid, "The backup is empty.");
        }

        // The storage vouches for the artifact with the checksum it verified; without one the
        // backup is not completed.
        if (!Backup.IsChecksum(artifact.Checksum))
        {
            throw new JobExecutionException(BackupErrorCodes.BackupArtifactInvalid, "The backup has no verified checksum.");
        }

        backup.MarkCompleted(
            artifact.Path, artifact.SizeBytes, BackupChecksumAlgorithm.Sha256, artifact.Checksum!, timeProvider.GetUtcNow().UtcDateTime);
    }

    public async Task OnFailedAsync(Job job, CancellationToken cancellationToken)
    {
        var backup = await db.Backups.FirstOrDefaultAsync(b => b.Id == job.BackupId, cancellationToken);

        // Only a backup this job took up is this job's to fail.
        if (backup is { Status: BackupStatus.Running } && backup.DatabaseId == job.DatabaseId)
        {
            backup.MarkFailed(
                job.ErrorCode ?? BackupErrorCodes.BackupProcessFailed,
                job.ErrorMessage ?? "The backup failed.",
                timeProvider.GetUtcNow().UtcDateTime);
        }
    }

    private IBackupManager ManagerFor(Instance instance) =>
        managers.FirstOrDefault(manager => manager.Engine == instance.Engine)
        ?? throw new JobExecutionException(
            BackupErrorCodes.BackupToolUnavailable,
            "Backups are not supported for the instance's engine.");
}
