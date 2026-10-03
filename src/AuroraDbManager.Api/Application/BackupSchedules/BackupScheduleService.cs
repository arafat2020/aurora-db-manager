using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Domain.BackupSchedules;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Application.BackupSchedules;

/// <summary>
/// Creates, changes and removes the backup schedule of a database. A database has one at most.
/// Nothing here starts a backup: the first run of a new or changed schedule is its next
/// occurrence after now, and it is the scheduler that acts on it.
/// </summary>
public sealed class BackupScheduleService(
    AppDbContext db,
    BackupService backups,
    IScheduleCalculator calculator,
    TimeProvider timeProvider,
    ILogger<BackupScheduleService> logger)
{
    public async Task<BackupScheduleResult> CreateAsync(Guid databaseId, BackupScheduleRequest request, CancellationToken cancellationToken)
    {
        // Only for a database that can be backed up right now, by the same rule a backup request is held to.
        var ineligible = await backups.EligibilityAsync(databaseId, cancellationToken) switch
        {
            null => (BackupScheduleStatus?)null,
            CreateBackupStatus.DatabaseNotFound => BackupScheduleStatus.DatabaseNotFound,
            CreateBackupStatus.InstanceNotReady => BackupScheduleStatus.InstanceNotReady,
            _ => BackupScheduleStatus.DatabaseNotReady
        };
        if (ineligible is not null)
        {
            return new BackupScheduleResult(ineligible.Value);
        }

        if (Validate(request, out var cronExpression, out var timeZoneId) is { } invalid)
        {
            return new BackupScheduleResult(invalid);
        }

        if (await db.BackupSchedules.AnyAsync(s => s.DatabaseId == databaseId, cancellationToken))
        {
            return new BackupScheduleResult(BackupScheduleStatus.AlreadyExists);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var enabled = request.Enabled ?? true;
        var schedule = BackupSchedule.Create(
            databaseId, cronExpression, timeZoneId, enabled, NextRun(enabled, cronExpression, timeZoneId, now), now);
        db.BackupSchedules.Add(schedule);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The check above can be overtaken by a concurrent request; the unique index and the
            // foreign key are what actually guarantee it. Find out which one refused the row.
            db.ChangeTracker.Clear();

            if (await db.BackupSchedules.AnyAsync(s => s.DatabaseId == databaseId, cancellationToken))
            {
                return new BackupScheduleResult(BackupScheduleStatus.AlreadyExists);
            }

            if (!await db.Databases.AnyAsync(d => d.Id == databaseId, cancellationToken))
            {
                return new BackupScheduleResult(BackupScheduleStatus.DatabaseNotFound);
            }

            throw;
        }

        logger.LogInformation(
            "Backup schedule {BackupScheduleId} created for database {DatabaseId}; next run at {NextRunAt}",
            schedule.Id, databaseId, schedule.NextRunAt);
        return new BackupScheduleResult(BackupScheduleStatus.Ok, BackupScheduleResponse.From(schedule));
    }

    public async Task<BackupScheduleResult> GetAsync(Guid databaseId, CancellationToken cancellationToken)
    {
        var schedule = await db.BackupSchedules.AsNoTracking().FirstOrDefaultAsync(s => s.DatabaseId == databaseId, cancellationToken);

        return schedule is null
            ? new BackupScheduleResult(await MissingAsync(databaseId, cancellationToken))
            : new BackupScheduleResult(BackupScheduleStatus.Ok, BackupScheduleResponse.From(schedule));
    }

    public async Task<BackupScheduleResult> UpdateAsync(Guid databaseId, BackupScheduleRequest request, CancellationToken cancellationToken)
    {
        var schedule = await db.BackupSchedules.FirstOrDefaultAsync(s => s.DatabaseId == databaseId, cancellationToken);
        if (schedule is null)
        {
            return new BackupScheduleResult(await MissingAsync(databaseId, cancellationToken));
        }

        if (Validate(request, out var cronExpression, out var timeZoneId) is { } invalid)
        {
            return new BackupScheduleResult(invalid);
        }

        var enabled = request.Enabled ?? true;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Unchanged, the schedule keeps the run it owes. Changed, re-enabled or disabled, what it
        // owes is worked out anew from now: an occurrence of the old schedule is not carried over.
        var unchanged = schedule.Enabled == enabled && schedule.CronExpression == cronExpression && schedule.TimeZoneId == timeZoneId;
        if (!unchanged)
        {
            schedule.Update(cronExpression, timeZoneId, enabled, NextRun(enabled, cronExpression, timeZoneId, now), now);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The scheduler moved the schedule on, or it was deleted, in this very moment.
                // What the client asked for is applied to the schedule as it now is.
                db.ChangeTracker.Clear();
                return await UpdateAsync(databaseId, request, cancellationToken);
            }

            logger.LogInformation(
                "Backup schedule {BackupScheduleId} of database {DatabaseId} changed; next run at {NextRunAt}",
                schedule.Id, databaseId, schedule.NextRunAt);
        }

        return new BackupScheduleResult(BackupScheduleStatus.Ok, BackupScheduleResponse.From(schedule));
    }

    /// <summary>
    /// Removes the schedule. Backups that exist stay, and a backup that is running because of the
    /// schedule runs on: it is a job of its own.
    /// </summary>
    public async Task<BackupScheduleStatus> DeleteAsync(Guid databaseId, CancellationToken cancellationToken)
    {
        var deleted = await db.BackupSchedules.Where(s => s.DatabaseId == databaseId).ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            return await MissingAsync(databaseId, cancellationToken);
        }

        logger.LogInformation("Backup schedule of database {DatabaseId} deleted", databaseId);
        return BackupScheduleStatus.Ok;
    }

    private BackupScheduleStatus? Validate(BackupScheduleRequest request, out string cronExpression, out string timeZoneId)
    {
        timeZoneId = request.TimeZoneId ?? string.Empty;

        if (!calculator.TryParseCronExpression(request.CronExpression, out cronExpression))
        {
            return BackupScheduleStatus.InvalidCronExpression;
        }

        return calculator.IsTimeZone(request.TimeZoneId) ? null : BackupScheduleStatus.InvalidTimeZone;
    }

    // Strictly after now: creating or changing a schedule never makes a backup due at once.
    private DateTime? NextRun(bool enabled, string cronExpression, string timeZoneId, DateTime now) =>
        enabled ? calculator.NextOccurrence(cronExpression, timeZoneId, now) : null;

    private async Task<BackupScheduleStatus> MissingAsync(Guid databaseId, CancellationToken cancellationToken) =>
        await db.Databases.AnyAsync(d => d.Id == databaseId, cancellationToken)
            ? BackupScheduleStatus.ScheduleNotFound
            : BackupScheduleStatus.DatabaseNotFound;
}
