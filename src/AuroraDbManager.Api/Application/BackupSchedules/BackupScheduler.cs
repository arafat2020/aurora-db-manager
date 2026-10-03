using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Application.BackupSchedules;

/// <summary>
/// Acts on schedules that are due. For each one it does three things as one database
/// transaction: it claims the occurrence, it has <see cref="BackupService"/> add an ordinary
/// backup with an ordinary <c>backup_database</c> job, and it moves the schedule on to its next
/// occurrence in the future. It dumps nothing and stores nothing; the job does that.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly one backup per occurrence, at most.</b> The schedule's next run is a concurrency
/// token. Of two passes that find the same occurrence due, in this process or after a restart,
/// only one can change it; the other's whole transaction, backup and job included, is refused. A
/// process that dies before the commit has done nothing and finds the occurrence still due; one
/// that dies after it has the job in the database, where job recovery finds it.
/// </para>
/// <para>
/// <b>Missed occurrences.</b> The schedule always moves on to the first occurrence after now, not
/// after the one that was due. However many occurrences passed while the application was down,
/// one backup makes up for them.
/// </para>
/// <para>
/// <b>Skipped occurrences.</b> If the database cannot be backed up when its schedule is due, it
/// is not ready, its instance is not running, or it is already being backed up or restored, no
/// backup is created and the schedule moves on all the same. A schedule never waits for a
/// database and never builds up a backlog.
/// </para>
/// </remarks>
public sealed class BackupScheduler(
    AppDbContext db,
    BackupService backups,
    IScheduleCalculator calculator,
    TimeProvider timeProvider,
    ILogger<BackupScheduler> logger)
{
    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Deals with every schedule that is due now.</summary>
    /// <returns>The ids of the backup jobs that were created and queued.</returns>
    public async Task<IReadOnlyList<Guid>> RunDueAsync(CancellationToken cancellationToken)
    {
        var now = UtcNow;
        var due = await db.BackupSchedules.AsNoTracking()
            .Where(s => s.Enabled && s.NextRunAt != null && s.NextRunAt <= now)
            .OrderBy(s => s.NextRunAt)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        var jobs = new List<Guid>();
        foreach (var scheduleId in due)
        {
            try
            {
                if (await RunAsync(scheduleId, cancellationToken) is { } jobId)
                {
                    jobs.Add(jobId);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One schedule that cannot be dealt with does not hold up the others. It is
                // still due and is tried again at the next pass.
                logger.LogError(exception, "Backup schedule {BackupScheduleId} could not be run", scheduleId);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        return jobs;
    }

    private async Task<Guid?> RunAsync(Guid scheduleId, CancellationToken cancellationToken)
    {
        var now = UtcNow;

        // Read again, tracked: it may have been changed, disabled, deleted or run since it was listed.
        var schedule = await db.BackupSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, cancellationToken);
        if (schedule is not { Enabled: true, NextRunAt: { } occurrence } || occurrence > now)
        {
            return null;
        }

        // After now, not after the occurrence: whatever was missed in between is not run.
        var next = calculator.NextOccurrence(schedule.CronExpression, schedule.TimeZoneId, now);
        schedule.Advance(next, now);

        var prepared = await backups.PrepareAsync(schedule.DatabaseId, cancellationToken);

        try
        {
            // The claim, the backup with its job, and the move to the next occurrence: together or not at all.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation(
                "Backup schedule {BackupScheduleId}: the occurrence of {Occurrence} was dealt with elsewhere", scheduleId, occurrence);
            return null;
        }
        catch (DbUpdateException) when (prepared.Status == CreateBackupStatus.Accepted)
        {
            // Between the check and the save something else took the database: a backup, a
            // restore or its deletion. The occurrence is skipped like any other that finds the
            // database busy, and the schedule still moves on.
            db.ChangeTracker.Clear();

            var current = await db.BackupSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, cancellationToken);
            if (current is not { Enabled: true } || current.NextRunAt != occurrence)
            {
                return null;
            }

            current.Advance(next, now);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return null;
            }

            logger.LogWarning(
                "Backup schedule {BackupScheduleId}: the occurrence of {Occurrence} was skipped, database {DatabaseId} became busy; next run at {NextRunAt}",
                scheduleId, occurrence, current.DatabaseId, next);
            return null;
        }

        if (prepared.Status != CreateBackupStatus.Accepted)
        {
            logger.LogWarning(
                "Backup schedule {BackupScheduleId}: the occurrence of {Occurrence} was skipped, database {DatabaseId} cannot be backed up now ({Reason}); next run at {NextRunAt}",
                scheduleId, occurrence, schedule.DatabaseId, prepared.Status, next);
            return null;
        }

        await backups.EnqueueAsync(prepared.Backup!, prepared.Job!);
        logger.LogInformation(
            "Backup schedule {BackupScheduleId}: backup {BackupId} started for the occurrence of {Occurrence}; next run at {NextRunAt}",
            scheduleId, prepared.Backup!.Id, occurrence, next);
        return prepared.Job!.Id;
    }
}
