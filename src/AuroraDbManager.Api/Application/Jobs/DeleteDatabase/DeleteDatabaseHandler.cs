using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Application.Jobs.DeleteDatabase;

/// <summary>
/// Drops the job's database from its instance through <see cref="IDatabaseManager"/> and, only
/// once that has succeeded, removes the database's metadata. The removal is committed together
/// with the job's completion. While the job retries, the database stays <c>deleting</c>; once the
/// job has failed for good it becomes <c>failed</c> and its metadata is kept.
/// </summary>
public sealed class DeleteDatabaseHandler(
    AppDbContext db,
    IEnumerable<IDatabaseManager> managers,
    TimeProvider timeProvider,
    ILogger<DeleteDatabaseHandler> logger) : IJobHandler
{
    public JobType Type => JobType.DeleteDatabase;

    public async Task ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        var database = await db.Databases.FirstOrDefaultAsync(d => d.Id == job.DatabaseId, cancellationToken);
        if (database is null)
        {
            logger.LogInformation(
                "Database {DatabaseId} no longer has metadata; job {JobId} has nothing to delete",
                job.DatabaseId, job.Id);
            return;
        }

        // The instance is taken from the database, never from elsewhere, and must be the job's.
        if (database.InstanceId != job.InstanceId)
        {
            throw new JobExecutionException(
                DatabaseErrorCodes.DatabaseInvalidState,
                "The database does not belong to the job's instance.");
        }

        if (database.Status != DatabaseStatus.Deleting)
        {
            throw new JobExecutionException(
                DatabaseErrorCodes.DatabaseInvalidState,
                "The database is not waiting to be deleted.");
        }

        var instance = await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == database.InstanceId, cancellationToken)
            ?? throw new JobExecutionException(DatabaseErrorCodes.InstanceNotFound, "The instance no longer exists.");

        // Not started here: an instance that stopped is retried, and fails the job if it stays down.
        if (instance.Status != InstanceStatus.Running)
        {
            throw new JobExecutionException(DatabaseErrorCodes.InstanceNotReady, "The instance is not running.");
        }

        using var scope = logger.BeginScope(
            "Job {JobId} attempt {Attempt}: deleting database {DatabaseId} from {Engine} instance {InstanceId}",
            job.Id, job.Attempt, database.Id, instance.Engine, instance.Id);

        try
        {
            await ManagerFor(instance).DeleteDatabaseAsync(instance, database, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DatabaseOperationException exception)
        {
            // Its code and message are written for clients.
            throw new JobExecutionException(exception.Code, exception.Message, exception);
        }
        catch (Exception exception)
        {
            throw new JobExecutionException(
                DatabaseErrorCodes.DatabaseDeleteFailed, "The database could not be deleted.", exception);
        }

        // Only now, with the database gone from the engine. A failed drop never gets here, so the
        // metadata of a database that still exists is never lost.
        db.Databases.Remove(database);
    }

    public async Task OnFailedAsync(Job job, CancellationToken cancellationToken)
    {
        var database = await db.Databases.FirstOrDefaultAsync(d => d.Id == job.DatabaseId, cancellationToken);

        if (database is { Status: DatabaseStatus.Deleting } && database.InstanceId == job.InstanceId)
        {
            database.MarkFailed(
                job.ErrorCode ?? DatabaseErrorCodes.DatabaseDeleteFailed,
                job.ErrorMessage ?? "The database could not be deleted.",
                timeProvider.GetUtcNow().UtcDateTime);
        }
    }

    private IDatabaseManager ManagerFor(Instance instance) =>
        managers.FirstOrDefault(manager => manager.Engine == instance.Engine)
        ?? throw new JobExecutionException(
            DatabaseErrorCodes.DatabaseDeleteFailed,
            "Databases cannot be managed for the instance's engine.");
}
