using AuroraDbManager.Api.Domain.BackupSchedules;

namespace AuroraDbManager.Api.Application.BackupSchedules;

public sealed class BackupScheduleRequest
{
    /// <summary>
    /// When to back up, as a five-field cron expression: minute, hour, day of month, month, day
    /// of week. For example <c>0 2 * * *</c> for every day at 02:00. Required.
    /// </summary>
    // Validated by the schedule calculator rather than by attributes, so every problem with it
    // is reported as INVALID_CRON_EXPRESSION.
    public string? CronExpression { get; init; }

    /// <summary>
    /// The IANA name of the time zone the expression is read in, e.g. <c>Asia/Dhaka</c>,
    /// <c>Europe/Berlin</c> or <c>UTC</c>. Required; the server's own time zone is never assumed.
    /// </summary>
    public string? TimeZoneId { get; init; }

    /// <summary>Whether the schedule is active. Defaults to true.</summary>
    public bool? Enabled { get; init; }
}

/// <param name="Id">Unique identifier of the schedule.</param>
/// <param name="DatabaseId">The database that is backed up.</param>
/// <param name="CronExpression">When, as a five-field cron expression.</param>
/// <param name="TimeZoneId">The time zone the expression is read in.</param>
/// <param name="Enabled">Whether the schedule is active.</param>
/// <param name="NextRunAt">UTC time of the next backup; null while the schedule is disabled.</param>
/// <param name="CreatedAt">UTC time the schedule was created.</param>
/// <param name="UpdatedAt">UTC time the schedule last changed, including when it last moved on to its next run.</param>
public sealed record BackupScheduleResponse(
    Guid Id,
    Guid DatabaseId,
    string CronExpression,
    string TimeZoneId,
    bool Enabled,
    DateTime? NextRunAt,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static BackupScheduleResponse From(BackupSchedule schedule) => new(
        schedule.Id,
        schedule.DatabaseId,
        schedule.CronExpression,
        schedule.TimeZoneId,
        schedule.Enabled,
        schedule.NextRunAt is { } nextRunAt ? Utc(nextRunAt) : null,
        Utc(schedule.CreatedAt),
        Utc(schedule.UpdatedAt));

    // The stored instants are UTC whatever kind the database provider hands back, and are
    // always written as such: a client must never have to guess the offset of a next run.
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <param name="Status">Whether the operation succeeded, and if not, why.</param>
/// <param name="Schedule">The schedule as it now is; set only when <paramref name="Status"/> is <see cref="BackupScheduleStatus.Ok"/>.</param>
public sealed record BackupScheduleResult(BackupScheduleStatus Status, BackupScheduleResponse? Schedule = null);

public enum BackupScheduleStatus
{
    Ok,
    DatabaseNotFound,
    ScheduleNotFound,

    /// <summary>Not created because the database already has a schedule.</summary>
    AlreadyExists,

    /// <summary>Not created because the database is not <c>ready</c>.</summary>
    DatabaseNotReady,

    /// <summary>Not created because the instance is not running.</summary>
    InstanceNotReady,
    InvalidCronExpression,
    InvalidTimeZone
}
