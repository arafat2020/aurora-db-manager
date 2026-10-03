using AuroraDbManager.Api.Application.BackupSchedules;
using AuroraDbManager.Api.Domain.BackupSchedules;
using Cronos;

namespace AuroraDbManager.Api.Infrastructure.Scheduling;

/// <summary>
/// <see cref="IScheduleCalculator"/> with the Cronos library for cron expressions and the
/// platform's time zone database for time zones.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cron.</b> The standard five fields: minute, hour, day of month, month, day of week. What
/// each field may contain is what Cronos accepts for that format, which includes lists, ranges,
/// steps and names of months and days. Expressions with a seconds field and shorthands such as
/// <c>@daily</c> are not accepted.
/// </para>
/// <para>
/// <b>Time zones.</b> IANA names only, the same on every platform. A Windows name, an offset such
/// as <c>+06:00</c>, or a name the runtime does not know is not a time zone here; nothing is ever
/// replaced by UTC or by the machine's own zone.
/// </para>
/// <para>
/// <b>Daylight saving.</b> Cronos's rules: a time that does not exist on the day clocks go
/// forward runs at the moment they do; a time that occurs twice on the day they go back runs once.
/// </para>
/// </remarks>
public sealed class CronosScheduleCalculator : IScheduleCalculator
{
    public bool TryParseCronExpression(string? cronExpression, out string normalized)
    {
        // Stored with single spaces between its fields.
        var fields = (cronExpression ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        normalized = string.Join(' ', fields);

        // Exactly five fields: neither a seconds field nor a shorthand such as "@daily".
        return fields.Length == 5
            && normalized.Length <= BackupSchedule.CronExpressionMaxLength
            && CronExpression.TryParse(normalized, CronFormat.Standard, out var expression)
            // An expression that can be written but never happens, such as the 30th of February.
            && expression.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc) is not null;
    }

    public bool IsTimeZone(string? timeZoneId) => TryFind(timeZoneId, out _);

    public DateTime? NextOccurrence(string cronExpression, string timeZoneId, DateTime afterUtc)
    {
        if (!TryFind(timeZoneId, out var timeZone))
        {
            throw new ArgumentException("Not a time zone.", nameof(timeZoneId));
        }

        var after = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        return CronExpression.Parse(cronExpression, CronFormat.Standard).GetNextOccurrence(after, timeZone, inclusive: false);
    }

    private static bool TryFind(string? timeZoneId, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;

        return timeZoneId is { Length: > 0 and <= BackupSchedule.TimeZoneIdMaxLength }
            && timeZoneId == timeZoneId.Trim()
            && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out timeZone!)
            // The runtime also resolves the other platform's names; only IANA names are portable.
            && timeZone.HasIanaId;
    }
}
