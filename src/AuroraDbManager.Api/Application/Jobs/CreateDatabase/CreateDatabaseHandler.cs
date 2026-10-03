using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Application.Jobs.CreateDatabase;

/// <summary>
/// Creates the job's database in its instance through <see cref="IDatabaseManager"/> and reports
/// the result on the database: <c>ready</c> on success, <c>failed</c> once the job has failed for
/// good. While the job retries, the database stays <c>creating</c>.
/// </summary>
public sealed class CreateDatabaseHandler(
    AppDbContext db,
    IEnumerable<IDatabaseManager> managers,
    TimeProvider timeProvider,
    ILogger<CreateDatabaseHandler> logger) : IJobHandler
{
    public JobType Type => JobType.CreateDatabase;

    public async Task ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        var database = await db.Databases.FirstOrDefaultAsync(d => d.Id == job.DatabaseId, cancellationToken)
            ?? throw new JobExecutionException(DatabaseErrorCodes.DatabaseDoesNotExist, "The database no longer exists.");

        // The instance is taken from the database, never from elsewhere, and must be the job's.
        if (database.InstanceId != job.InstanceId)
        {
            throw new JobExecutionException(
                DatabaseErrorCodes.DatabaseInvalidState,
                "The database does not belong to the job's instance.");
        }

        if (database.Status == DatabaseStatus.Ready)
        {
            logger.LogInformation(
                "Database {DatabaseId} is already ready; job {JobId} has nothing to create",
                database.Id, job.Id);
            return;
        }

        if (database.Status != DatabaseStatus.Creating)
        {
            throw new JobExecutionException(
                DatabaseErrorCodes.DatabaseInvalidState,
                "The database is not waiting to be created.");
        }

        var instance = await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == database.InstanceId, cancellationToken)
            ?? throw new JobExecutionException(DatabaseErrorCodes.InstanceNotFound, "The instance no longer exists.");

        // Not started here: an instance that stopped is retried, and fails the job if it stays down.
        if (instance.Status != InstanceStatus.Running)
        {
            throw new JobExecutionException(DatabaseErrorCodes.InstanceNotReady, "The instance is not running.");
        }

        using var scope = logger.BeginScope(
            "Job {JobId} attempt {Attempt}: creating database {DatabaseId} in {Engine} instance {InstanceId}",
            job.Id, job.Attempt, database.Id, instance.Engine, instance.Id);

        try
        {
            await ManagerFor(instance).CreateDatabaseAsync(instance, database, cancellationToken);
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
                DatabaseErrorCodes.DatabaseCreateFailed, "The database could not be created.", exception);
        }

        database.MarkReady(timeProvider.GetUtcNow().UtcDateTime);
    }

    public async Task OnFailedAsync(Job job, CancellationToken cancellationToken)
    {
        var database = await db.Databases.FirstOrDefaultAsync(d => d.Id == job.DatabaseId, cancellationToken);

        if (database is { Status: DatabaseStatus.Creating } && database.InstanceId == job.InstanceId)
        {
            database.MarkFailed(
                job.ErrorCode ?? DatabaseErrorCodes.DatabaseCreateFailed,
                job.ErrorMessage ?? "The database could not be created.",
                timeProvider.GetUtcNow().UtcDateTime);
        }
    }

    private IDatabaseManager ManagerFor(Instance instance) =>
        managers.FirstOrDefault(manager => manager.Engine == instance.Engine)
        ?? throw new JobExecutionException(
            DatabaseErrorCodes.DatabaseCreateFailed,
            "Databases cannot be managed for the instance's engine.");
}
