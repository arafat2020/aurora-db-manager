namespace AuroraDbManager.Api.Application.BackupSchedules;

/// <summary>
/// Reads cron expressions and time zone names, and tells when a schedule is next due. Nothing
/// here looks at the clock or at the time zone of the machine: every answer follows from the
/// arguments alone.
/// </summary>
public interface IScheduleCalculator
{
    /// <summary>
    /// Whether <paramref name="cronExpression"/> is a five-field cron expression that has
    /// occurrences at all. <paramref name="normalized"/> is the expression as it is stored.
    /// </summary>
    bool TryParseCronExpression(string? cronExpression, out string normalized);

    /// <summary>Whether <paramref name="timeZoneId"/> is the IANA name of a time zone, such as <c>Asia/Dhaka</c> or <c>UTC</c>.</summary>
    bool IsTimeZone(string? timeZoneId);

    /// <summary>
    /// The first occurrence of the expression, read in the time zone, strictly after
    /// <paramref name="afterUtc"/>, as a UTC time; null if there is none.
    /// </summary>
    DateTime? NextOccurrence(string cronExpression, string timeZoneId, DateTime afterUtc);
}
