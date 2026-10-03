using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Databases;

/// <summary>
/// Accepts database operations and hands them to the job system. Nothing here reaches a database
/// engine: creating and deleting record the intent as a status and a job, and the job's handler
/// does the engine work through <see cref="IDatabaseManager"/>.
/// </summary>
public sealed class DatabaseService(
    AppDbContext db,
    JobQueue jobQueue,
    IOptions<JobOptions> jobOptions,
    TimeProvider timeProvider,
    ILogger<DatabaseService> logger)
{
    public async Task<CreateDatabaseResult> CreateAsync(
        Guid instanceId, CreateDatabaseRequest request, CancellationToken cancellationToken)
    {
        if (DatabaseName.Validate(request.Name) is { } nameError)
        {
            return new CreateDatabaseResult(CreateDatabaseStatus.NameInvalid, NameError: nameError);
        }

        var name = request.Name!;

        var instance = await db.Instances.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null)
        {
            return new CreateDatabaseResult(CreateDatabaseStatus.InstanceNotFound);
        }

        if (instance.Status != InstanceStatus.Running)
        {
            return new CreateDatabaseResult(CreateDatabaseStatus.InstanceNotReady);
        }

        if (await NameExistsAsync(instanceId, name, cancellationToken))
        {
            return new CreateDatabaseResult(CreateDatabaseStatus.AlreadyExists);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var database = Database.Create(instanceId, name, now);
        var job = Job.Create(JobType.CreateDatabase, instanceId, jobOptions.Value.MaxAttempts, now, database.Id);

        // One SaveChanges is one transaction: the database and its job are stored together or not at all.
        db.Databases.Add(database);
        db.Jobs.Add(job);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The checks above can be overtaken by a concurrent request; the unique index and the
            // foreign key are what actually guarantee them. Find out which one refused the rows.
            db.ChangeTracker.Clear();

            if (await NameExistsAsync(instanceId, name, cancellationToken))
            {
                return new CreateDatabaseResult(CreateDatabaseStatus.AlreadyExists);
            }

            if (!await db.Instances.AnyAsync(i => i.Id == instanceId, cancellationToken))
            {
                return new CreateDatabaseResult(CreateDatabaseStatus.InstanceNotFound);
            }

            throw;
        }

        await EnqueueAsync(job);

        return new CreateDatabaseResult(CreateDatabaseStatus.Accepted, ToResponse(database, job));
    }

    public async Task<DatabaseResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var database = await db.Databases.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

        return database is null ? null : DatabaseResponse.From(database);
    }

    /// <returns>The requested page, or null if the instance does not exist.</returns>
    public async Task<DatabaseListResponse?> ListAsync(
        Guid instanceId, ListDatabasesQuery query, CancellationToken cancellationToken)
    {
        if (!await db.Instances.AnyAsync(i => i.Id == instanceId, cancellationToken))
        {
            return null;
        }

        var totalCount = await db.Databases.CountAsync(d => d.InstanceId == instanceId, cancellationToken);

        var databases = await db.Databases.AsNoTracking()
            .Where(d => d.InstanceId == instanceId)
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new DatabaseListResponse(
            databases.Select(DatabaseResponse.From).ToList(),
            query.Page,
            query.PageSize,
            totalCount);
    }

    /// <summary>
    /// Starts deleting a <c>ready</c> database: marks it <c>deleting</c> and creates the job that
    /// drops it. The row stays until the engine has dropped the database; the job removes it then,
    /// and with it the metadata of the database's backups. The backup files themselves are kept.
    /// </summary>
    public async Task<DeleteDatabaseResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var database = await db.Databases.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (database is null)
        {
            return new DeleteDatabaseResult(DeleteDatabaseStatus.NotFound);
        }

        if (RejectionFor(database.Status) is { } rejection)
        {
            return new DeleteDatabaseResult(rejection);
        }

        var instance = await db.Instances.AsNoTracking()
            .FirstAsync(i => i.Id == database.InstanceId, cancellationToken);
        if (instance.Status != InstanceStatus.Running)
        {
            return new DeleteDatabaseResult(DeleteDatabaseStatus.InstanceNotReady);
        }

        // A backup reads the database for as long as it runs and a restore writes to it; the
        // database is not dropped under either.
        if (BusyWith(await db.UnfinishedJobTypeAsync(id, cancellationToken)) is { } busy)
        {
            return new DeleteDatabaseResult(busy);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        database.MarkDeleting(now);
        var job = Job.Create(JobType.DeleteDatabase, database.InstanceId, jobOptions.Value.MaxAttempts, now, database.Id);
        db.Jobs.Add(job);

        try
        {
            // The status change and the job are one transaction.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent request got in first: the status is a concurrency token, and a
            // database can have only one unfinished job, a backup or restore included. Report
            // what it is doing now.
            db.ChangeTracker.Clear();

            if (BusyWith(await db.UnfinishedJobTypeAsync(id, cancellationToken)) is { } concurrent)
            {
                return new DeleteDatabaseResult(concurrent);
            }

            var current = await db.Databases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
            if (current is null)
            {
                return new DeleteDatabaseResult(DeleteDatabaseStatus.NotFound);
            }

            if (RejectionFor(current.Status) is { } concurrentRejection)
            {
                return new DeleteDatabaseResult(concurrentRejection);
            }

            throw;
        }

        await EnqueueAsync(job);

        return new DeleteDatabaseResult(DeleteDatabaseStatus.Accepted, ToResponse(database, job));
    }

    /// <summary>Why a database in <paramref name="status"/> cannot be deleted; null if it can.</summary>
    private static DeleteDatabaseStatus? RejectionFor(DatabaseStatus status) => status switch
    {
        DatabaseStatus.Ready => null,
        DatabaseStatus.Creating => DeleteDatabaseStatus.Creating,
        DatabaseStatus.Deleting => DeleteDatabaseStatus.Deleting,
        _ => DeleteDatabaseStatus.Failed
    };

    private async Task EnqueueAsync(Job job)
    {
        // Queued only after the commit, so the worker never sees a job that is not in the database.
        // If the process dies between the commit and this line the job stays pending until the
        // next start, when job recovery queues it. Not cancellable: the job is already committed.
        await jobQueue.EnqueueAsync(job.Id, CancellationToken.None);
        logger.LogInformation(
            "Job {JobId} ({JobType}) for database {DatabaseId} of instance {InstanceId} queued",
            job.Id, job.Type, job.DatabaseId, job.InstanceId);
    }

    private static DatabaseOperationResponse ToResponse(Database database, Job job) =>
        new(DatabaseResponse.From(database), JobResponse.From(job));

    /// <summary>The backup or restore that keeps a database from being deleted; null if neither is unfinished.</summary>
    private static DeleteDatabaseStatus? BusyWith(JobType? unfinished) => unfinished switch
    {
        JobType.BackupDatabase => DeleteDatabaseStatus.BackupInProgress,
        JobType.RestoreDatabase => DeleteDatabaseStatus.RestoreInProgress,
        _ => null
    };

    private Task<bool> NameExistsAsync(Guid instanceId, string name, CancellationToken cancellationToken) =>
        db.Databases.AnyAsync(d => d.InstanceId == instanceId && d.Name == name, cancellationToken);
}

/// <param name="Status">Whether the creation was accepted, and if not, why.</param>
/// <param name="Operation">The database and its job; set only when <paramref name="Status"/> is <see cref="CreateDatabaseStatus.Accepted"/>.</param>
/// <param name="NameError">What is wrong with the name; set only when <paramref name="Status"/> is <see cref="CreateDatabaseStatus.NameInvalid"/>.</param>
public sealed record CreateDatabaseResult(
    CreateDatabaseStatus Status,
    DatabaseOperationResponse? Operation = null,
    string? NameError = null);

public enum CreateDatabaseStatus
{
    Accepted,
    NameInvalid,
    InstanceNotFound,

    /// <summary>Not accepted because the instance is not running.</summary>
    InstanceNotReady,

    /// <summary>Not accepted because the instance already has a database with this name.</summary>
    AlreadyExists
}

/// <param name="Status">Whether the deletion was accepted, and if not, why.</param>
/// <param name="Operation">The database and its job; set only when <paramref name="Status"/> is <see cref="DeleteDatabaseStatus.Accepted"/>.</param>
public sealed record DeleteDatabaseResult(DeleteDatabaseStatus Status, DatabaseOperationResponse? Operation = null);

public enum DeleteDatabaseStatus
{
    Accepted,
    NotFound,

    /// <summary>Not accepted because the database is still being created.</summary>
    Creating,

    /// <summary>Not accepted because the database is already being deleted.</summary>
    Deleting,

    /// <summary>Not accepted because an earlier operation on the database failed for good.</summary>
    Failed,

    /// <summary>Not accepted because the instance is not running.</summary>
    InstanceNotReady,

    /// <summary>Not accepted because a backup of the database is not finished yet.</summary>
    BackupInProgress,

    /// <summary>Not accepted because the database is being restored.</summary>
    RestoreInProgress
}
