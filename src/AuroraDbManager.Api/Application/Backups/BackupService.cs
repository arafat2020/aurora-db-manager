using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Backups;

/// <summary>
/// Accepts backup requests and hands them to the job system. Nothing here touches a database
/// engine or the backup storage; the job's handler does that through <see cref="IBackupManager"/>.
/// A new backup is for the server's default storage, <c>Backups:StorageType</c>, which is what
/// <see cref="IBackupStorage"/> is here; a client cannot choose one. From then on the backup's
/// own record says where it is.
/// </summary>
public sealed class BackupService(
    AppDbContext db,
    IBackupStorage storage,
    JobQueue jobQueue,
    IOptions<JobOptions> jobOptions,
    TimeProvider timeProvider,
    ILogger<BackupService> logger)
{
    public async Task<CreateBackupResult> CreateAsync(Guid databaseId, CancellationToken cancellationToken)
    {
        var database = await db.Databases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == databaseId, cancellationToken);
        if (database is null)
        {
            return new CreateBackupResult(CreateBackupStatus.DatabaseNotFound);
        }

        if (database.Status != DatabaseStatus.Ready)
        {
            return new CreateBackupResult(CreateBackupStatus.DatabaseNotReady);
        }

        var instance = await db.Instances.AsNoTracking().FirstAsync(i => i.Id == database.InstanceId, cancellationToken);
        if (instance.Status != InstanceStatus.Running)
        {
            return new CreateBackupResult(CreateBackupStatus.InstanceNotReady);
        }

        if (Rejection(await db.UnfinishedJobTypeAsync(databaseId, cancellationToken)) is { } busy)
        {
            return new CreateBackupResult(busy);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var backup = Backup.Create(databaseId, storage.Type, now);
        var job = Job.Create(
            JobType.BackupDatabase, database.InstanceId, jobOptions.Value.MaxAttempts, now, databaseId, backup.Id);

        // One SaveChanges is one transaction: the backup and its job are stored together or not at all.
        db.Backups.Add(backup);
        db.Jobs.Add(job);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The checks above can be overtaken by a concurrent request. A database has only one
            // unfinished job, so whatever got in first, another backup, a restore or the
            // database's deletion, made the index refuse this job. Find out which it was.
            db.ChangeTracker.Clear();

            if (Rejection(await db.UnfinishedJobTypeAsync(databaseId, cancellationToken)) is { } concurrent)
            {
                return new CreateBackupResult(concurrent);
            }

            var current = await db.Databases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == databaseId, cancellationToken);
            if (current is null)
            {
                return new CreateBackupResult(CreateBackupStatus.DatabaseNotFound);
            }

            if (current.Status != DatabaseStatus.Ready)
            {
                return new CreateBackupResult(CreateBackupStatus.DatabaseNotReady);
            }

            throw;
        }

        // Queued only after the commit, so the worker never sees a job that is not in the database.
        // If the process dies between the commit and this line the job stays pending until the
        // next start, when job recovery queues it. Not cancellable: the job is already committed.
        await jobQueue.EnqueueAsync(job.Id, CancellationToken.None);
        logger.LogInformation(
            "Job {JobId} ({JobType}) for backup {BackupId} of database {DatabaseId} of instance {InstanceId} queued",
            job.Id, job.Type, backup.Id, databaseId, job.InstanceId);

        return new CreateBackupResult(
            CreateBackupStatus.Accepted,
            new CreateBackupResponse(BackupResponse.From(backup), JobResponse.From(job)));
    }

    public async Task<BackupResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var backup = await db.Backups.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

        return backup is null ? null : BackupResponse.From(backup);
    }

    /// <returns>The requested page, or null if the database does not exist.</returns>
    public async Task<BackupListResponse?> ListAsync(Guid databaseId, ListBackupsQuery query, CancellationToken cancellationToken)
    {
        if (!await db.Databases.AnyAsync(d => d.Id == databaseId, cancellationToken))
        {
            return null;
        }

        var totalCount = await db.Backups.CountAsync(b => b.DatabaseId == databaseId, cancellationToken);

        var backups = await db.Backups.AsNoTracking()
            .Where(b => b.DatabaseId == databaseId)
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new BackupListResponse(
            backups.Select(BackupResponse.From).ToList(),
            query.Page,
            query.PageSize,
            totalCount);
    }

    /// <summary>Why a database with that unfinished job cannot be backed up; null if it can.</summary>
    private static CreateBackupStatus? Rejection(JobType? unfinished) => unfinished switch
    {
        JobType.BackupDatabase => CreateBackupStatus.BackupInProgress,
        // A backup taken in the middle of a restore would be a backup of half a database.
        JobType.RestoreDatabase => CreateBackupStatus.RestoreInProgress,
        // A database that is being created or deleted is reported by its status.
        _ => null
    };
}

/// <param name="Status">Whether the backup was accepted, and if not, why.</param>
/// <param name="Operation">The backup and its job; set only when <paramref name="Status"/> is <see cref="CreateBackupStatus.Accepted"/>.</param>
public sealed record CreateBackupResult(CreateBackupStatus Status, CreateBackupResponse? Operation = null);

public enum CreateBackupStatus
{
    Accepted,
    DatabaseNotFound,

    /// <summary>Not accepted because the database is not <c>ready</c>.</summary>
    DatabaseNotReady,

    /// <summary>Not accepted because the instance is not running.</summary>
    InstanceNotReady,

    /// <summary>Not accepted because the database already has a backup that is not finished.</summary>
    BackupInProgress,

    /// <summary>Not accepted because the database is being restored.</summary>
    RestoreInProgress
}
