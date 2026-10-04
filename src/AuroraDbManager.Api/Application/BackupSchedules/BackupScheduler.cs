using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Monitoring;
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
/// <para>
/// <b>Observability.</b> Every pass, triggered backup, skipped occurrence and failure is counted
/// and logged, and a pass that went through is noted on the <see cref="SchedulerHeartbeat"/>.
/// None of that is read back here: what is due is decided from the database alone.
/// </para>
/// </remarks>
public sealed class BackupScheduler(
    AppDbContext db,
    BackupService backups,
    IScheduleCalculator calculator,
    TimeProvider timeProvider,
    AuroraMetrics metrics,
    SchedulerHeartbeat heartbeat,
    ILogger<BackupScheduler> logger)
{
    /// <summary>The reason given when the database was taken by another operation between the check and the save.</summary>
    public const string DatabaseBusy = "database_busy";

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Deals with every schedule that is due now.</summary>
    /// <returns>The ids of the backup jobs that were created and queued.</returns>
    public async Task<IReadOnlyList<Guid>> RunDueAsync(CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();

        List<Guid> due;
        try
        {
            var now = UtcNow;
            due = await db.BackupSchedules.AsNoTracking()
                .Where(s => s.Enabled && s.NextRunAt != null && s.NextRunAt <= now)
                .OrderBy(s => s.NextRunAt)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // For example the system database being unreachable. The caller tries again at the next pass.
            metrics.SchedulerFailed();
            metrics.SchedulerPassCompleted(timeProvider.GetElapsedTime(started));
            logger.LogError(
                exception,
                "Scheduled backup scheduler pass failed: {ErrorType} after {DurationMs} ms",
                exception.GetType().Name, (long)timeProvider.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }

        var jobs = new List<Guid>();
        var skipped = 0;
        var failed = 0;
        foreach (var scheduleId in due)
        {
            try
            {
                var (outcome, jobId) = await RunAsync(scheduleId, cancellationToken);
                if (jobId is { } triggered)
                {
                    jobs.Add(triggered);
                }
                else if (outcome == Outcome.Skipped)
                {
                    skipped++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One schedule that cannot be dealt with does not hold up the others. It is
                // still due and is tried again at the next pass.
                failed++;
                metrics.SchedulerFailed();
                logger.LogError(
                    exception,
                    "Scheduled backup scheduler pass failed for schedule {ScheduleId}: {ErrorType}",
                    scheduleId, exception.GetType().Name);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        var duration = timeProvider.GetElapsedTime(started);
        metrics.SchedulerPassCompleted(duration);
        if (failed == 0)
        {
            heartbeat.RecordSuccessfulPass(UtcNow);
        }

        // Most passes find nothing due; those are not worth a line in the ordinary log.
        logger.Log(
            due.Count == 0 ? LogLevel.Debug : LogLevel.Information,
            "Scheduled backup scheduler pass completed: {DueCount} due, {TriggeredCount} triggered, {SkippedCount} skipped, {FailedCount} failed in {DurationMs} ms",
            due.Count, jobs.Count, skipped, failed, (long)duration.TotalMilliseconds);

        return jobs;
    }

    private async Task<(Outcome Outcome, Guid? JobId)> RunAsync(Guid scheduleId, CancellationToken cancellationToken)
    {
        var now = UtcNow;

        // Read again, tracked: it may have been changed, disabled, deleted or run since it was listed.
        var schedule = await db.BackupSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, cancellationToken);
        if (schedule is not { Enabled: true, NextRunAt: { } occurrence } || occurrence > now)
        {
            return (Outcome.NotDue, null);
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
                "Backup schedule {ScheduleId}: the occurrence of {Occurrence} was dealt with elsewhere", scheduleId, occurrence);
            return (Outcome.NotDue, null);
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
                return (Outcome.NotDue, null);
            }

            current.Advance(next, now);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return (Outcome.NotDue, null);
            }

            Skipped(scheduleId, current.DatabaseId, DatabaseBusy, occurrence, next);
            return (Outcome.Skipped, null);
        }

        if (prepared.Status != CreateBackupStatus.Accepted)
        {
            Skipped(scheduleId, schedule.DatabaseId, AuroraMetrics.TagValue(prepared.Status), occurrence, next);
            return (Outcome.Skipped, null);
        }

        await backups.EnqueueAsync(prepared.Backup!, prepared.Job!);
        metrics.ScheduledBackupTriggered();
        logger.LogInformation(
            "Scheduled backup triggered: schedule {ScheduleId}, database {DatabaseId}, job {JobId}, backup {BackupId}, for the occurrence of {Occurrence}; next run at {NextRunAt}",
            scheduleId, schedule.DatabaseId, prepared.Job!.Id, prepared.Backup!.Id, occurrence, next);
        return (Outcome.Triggered, prepared.Job!.Id);
    }

    // The reason is one of a few fixed values: why a backup request is refused, or DatabaseBusy.
    private void Skipped(Guid scheduleId, Guid databaseId, string reason, DateTime occurrence, DateTime? next)
    {
        metrics.ScheduledBackupSkipped(reason);
        logger.LogWarning(
            "Scheduled backup skipped: schedule {ScheduleId}, database {DatabaseId}, reason {Reason}, for the occurrence of {Occurrence}; next run at {NextRunAt}",
            scheduleId, databaseId, reason, occurrence, next);
    }

    private enum Outcome
    {
        /// <summary>Nothing to do: no longer due, or dealt with by another pass.</summary>
        NotDue,
        Triggered,
        Skipped
    }
}
