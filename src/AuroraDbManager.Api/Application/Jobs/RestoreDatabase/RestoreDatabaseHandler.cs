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
            // Its code and message are written for clients.
            throw new JobExecutionException(exception.Code, exception.Message, exception);
        }
        catch (Exception exception)
        {
            throw new JobExecutionException(RestoreErrorCodes.RestoreProcessFailed, "The restore failed.", exception);
        }
    }

    // Nothing to record anywhere but on the job itself.
    public Task OnFailedAsync(Job job, CancellationToken cancellationToken) => Task.CompletedTask;

    private IRestoreManager ManagerFor(Instance instance) =>
        managers.FirstOrDefault(manager => manager.Engine == instance.Engine)
        ?? throw new JobExecutionException(
            RestoreErrorCodes.RestoreToolUnavailable,
            "Restores are not supported for the instance's engine.");
}
