namespace AuroraDbManager.Api.Domain.BackupSchedules;

/// <summary>
/// When a database is backed up by itself: a cron expression, read in a named time zone. The
/// schedule only says when; each backup it causes is an ordinary backup with an ordinary job, and
/// how that went is on those, not here. <see cref="NextRunAt"/> is the one occurrence that is
/// still owed, as an absolute instant; a disabled schedule owes none.
/// </summary>
public sealed class BackupSchedule
{
    public const int CronExpressionMaxLength = 100;
    public const int TimeZoneIdMaxLength = 100;

    private BackupSchedule()
    {
    }

    public Guid Id { get; private set; }
    public Guid DatabaseId { get; private set; }

    /// <summary>Five fields: minute, hour, day of month, month, day of week.</summary>
    public string CronExpression { get; private set; } = null!;

    /// <summary>The IANA name of the time zone the expression is read in, e.g. <c>Asia/Dhaka</c> or <c>UTC</c>.</summary>
    public string TimeZoneId { get; private set; } = null!;

    public bool Enabled { get; private set; }

    /// <summary>The next occurrence, in UTC; null exactly when the schedule owes none, which a disabled one never does.</summary>
    public DateTime? NextRunAt { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public static BackupSchedule Create(
        Guid databaseId, string cronExpression, string timeZoneId, bool enabled, DateTime? nextRunAt, DateTime utcNow)
    {
        if (databaseId == Guid.Empty)
        {
            throw new ArgumentException("databaseId is required.", nameof(databaseId));
        }

        var schedule = new BackupSchedule { Id = Guid.CreateVersion7(), DatabaseId = databaseId, CreatedAt = utcNow };
        schedule.Update(cronExpression, timeZoneId, enabled, nextRunAt, utcNow);
        return schedule;
    }

    /// <summary>Replaces what the schedule says. The caller has calculated <paramref name="nextRunAt"/> from the new values.</summary>
    public void Update(string cronExpression, string timeZoneId, bool enabled, DateTime? nextRunAt, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cronExpression.Length, CronExpressionMaxLength, nameof(cronExpression));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeZoneId.Length, TimeZoneIdMaxLength, nameof(timeZoneId));
        EnsureConsistent(enabled, nextRunAt);

        CronExpression = cronExpression;
        TimeZoneId = timeZoneId;
        Enabled = enabled;
        NextRunAt = nextRunAt;
        UpdatedAt = utcNow;
    }

    /// <summary>Moves on to the occurrence after the one that has just been dealt with.</summary>
    public void Advance(DateTime? nextRunAt, DateTime utcNow)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException($"Cannot advance backup schedule {Id}: it is disabled.");
        }

        EnsureConsistent(Enabled, nextRunAt);

        NextRunAt = nextRunAt;
        UpdatedAt = utcNow;
    }

    private static void EnsureConsistent(bool enabled, DateTime? nextRunAt)
    {
        if (!enabled && nextRunAt is not null)
        {
            throw new ArgumentException("A disabled schedule has no next run.", nameof(nextRunAt));
        }

        // An instant, never a wall-clock time of some zone.
        if (nextRunAt is { Kind: not DateTimeKind.Utc })
        {
            throw new ArgumentException("The next run must be a UTC time.", nameof(nextRunAt));
        }
    }
}
