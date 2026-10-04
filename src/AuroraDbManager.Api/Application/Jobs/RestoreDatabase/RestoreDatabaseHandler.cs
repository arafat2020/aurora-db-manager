using AuroraDbManager.Api.Application.Monitoring;
using AuroraDbManager.Api.Application.Restores;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Application.Jobs.RestoreDatabase;

/// <summary>
/// Restores the job's backup into the backup's own database through <see cref="IRestoreManager"/>.
/// The job is the whole record of the restore: nothing is changed on the backup, which is only
/// read, or on the database's status, which stays <c>ready</c> throughout. A restore that failed
/// for good therefore leaves a failed job and a database that may be empty or partly restored.
/// </summary>
public sealed class RestoreDatabaseHandler(
    AppDbContext db,
    IEnumerable<IRestoreManager> managers,
    TimeProvider timeProvider,
    AuroraMetrics metrics,
    ILogger<RestoreDatabaseHandler> logger) : IJobHandler
{
    public JobType Type => JobType.RestoreDatabase;

    public async Task ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        var backup = await db.Backups.AsNoTracking().FirstOrDefaultAsync(b => b.Id == job.BackupId, cancellationToken)
            ?? throw new JobExecutionException(RestoreErrorCodes.RestoreInvalidState, "The backup no longer exists.");

        // The database is taken from the backup and the instance from the database, never from
        // elsewhere, and both must be the job's: a backup is only ever restored into its own database.
        var database = await db.Databases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == backup.DatabaseId, cancellationToken);
        if (database is null || database.Id != job.DatabaseId || database.InstanceId != job.InstanceId)
        {
            throw new JobExecutionException(
                RestoreErrorCodes.RestoreInvalidState,
                "The backup does not belong to the job's database and instance.");
        }

        if (backup.Status != BackupStatus.Completed)
        {
            throw new JobExecutionException(RestoreErrorCodes.RestoreInvalidState, "The backup is not completed.");
        }

        if (database.Status != DatabaseStatus.Ready)
        {
            throw new JobExecutionException(RestoreErrorCodes.RestoreDatabaseUnavailable, "The database is not ready.");
        }

        var instance = await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == database.InstanceId, cancellationToken)
            ?? throw new JobExecutionException(RestoreErrorCodes.RestoreDatabaseUnavailable, "The instance no longer exists.");

        // Not started here: an instance that stopped is retried, and fails the job if it stays down.
        if (instance.Status != InstanceStatus.Running)
        {
            throw new JobExecutionException(RestoreErrorCodes.RestoreDatabaseUnavailable, "The instance is not running.");
        }

        using var scope = logger.BeginScope(
            "Job {JobId} attempt {Attempt}: restoring backup {BackupId} into database {DatabaseId} in {Engine} instance {InstanceId}",
            job.Id, job.Attempt, backup.Id, database.Id, instance.Engine, instance.Id);

        // The job is the restore, so its first attempt is the restore's start. An attempt that
        // was interrupted runs again under the same number and is counted again.
        if (job.Attempt == 1)
        {
            metrics.RestoreStarted(instance.Engine, backup.StorageType);
        }

        var attemptStarted = timeProvider.GetTimestamp();
        try
        {
            await ManagerFor(instance).RestoreAsync(instance, database, backup, job.Id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RestoreOperationException exception)
        {
            metrics.RestoreAttemptFailed(instance.Engine, backup.StorageType, timeProvider.GetElapsedTime(attemptStarted));
            // Its code and message are written for clients.
            throw new JobExecutionException(exception.Code, exception.Message, exception);
        }
        catch (Exception exception)
        {
            metrics.RestoreAttemptFailed(instance.Engine, backup.StorageType, timeProvider.GetElapsedTime(attemptStarted));
            throw new JobExecutionException(RestoreErrorCodes.RestoreProcessFailed, "The restore failed.", exception);
        }

        var attemptDuration = timeProvider.GetElapsedTime(attemptStarted);
        metrics.RestoreCompleted(instance.Engine, backup.StorageType, attemptDuration);
        logger.LogInformation(
            "Restore of backup {BackupId} into database {DatabaseId} completed, attempt took {DurationMs} ms",
            backup.Id, database.Id, (long)attemptDuration.TotalMilliseconds);
    }

    // Nothing to record anywhere but on the job itself; what follows only observes.
    public async Task OnFailedAsync(Job job, CancellationToken cancellationToken)
    {
        var errorCode = job.ErrorCode ?? RestoreErrorCodes.RestoreProcessFailed;
        metrics.RestoreFailed(
            await db.EngineOfAsync(job.InstanceId, cancellationToken),
            await db.StorageTypeOfAsync(job.BackupId, cancellationToken),
            errorCode);
        logger.LogWarning(
            "Restore of backup {BackupId} into database {DatabaseId} failed: {ErrorCode}", job.BackupId, job.DatabaseId, errorCode);
    }

    private IRestoreManager ManagerFor(Instance instance) =>
        managers.FirstOrDefault(manager => manager.Engine == instance.Engine)
        ?? throw new JobExecutionException(
            RestoreErrorCodes.RestoreToolUnavailable,
            "Restores are not supported for the instance's engine.");
}
