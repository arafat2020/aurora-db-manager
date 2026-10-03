using AuroraDbManager.Api.Domain.BackupSchedules;
using AuroraDbManager.Api.Infrastructure.Backups;
using AuroraDbManager.Api.Infrastructure.Scheduling;

namespace AuroraDbManager.Api.Tests.Schedules;

public sealed class ScheduleCalculatorTests
{
    private readonly CronosScheduleCalculator _calculator = new();

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    // --- Cron expressions ---------------------------------------------------------------------

    [Theory]
    [InlineData("0 2 * * *")]
    [InlineData("0 */6 * * *")]
    [InlineData("0 3 * * 0")]
    [InlineData("*/15 * * * *")]
    [InlineData("30 1 1,15 * *")]
    [InlineData("0 9-17 * * MON-FRI")]
    [InlineData("0 0 1 JAN *")]
    public void ValidFiveFieldExpression_IsAccepted_AndStoredAsWritten(string expression)
    {
        Assert.True(_calculator.TryParseCronExpression(expression, out var normalized));
        Assert.Equal(expression, normalized);
    }

    [Theory]
    [InlineData("  0 2 * * *  ", "0 2 * * *")]
    [InlineData("0   2\t*  *   *", "0 2 * * *")]
    public void SurroundingAndRepeatedWhitespace_IsNormalized(string expression, string expected)
    {
        Assert.True(_calculator.TryParseCronExpression(expression, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0 2 * *")]
    [InlineData("0 0 2 * * *")]
    [InlineData("60 2 * * *")]
    [InlineData("0 24 * * *")]
    [InlineData("0 2 32 * *")]
    [InlineData("0 2 * 13 *")]
    [InlineData("0 2 * * 9")]
    [InlineData("every day at two")]
    [InlineData("@daily")]
    [InlineData("0 2 * * *; DROP TABLE backup_schedules")]
    // Well-formed, and never happens.
    [InlineData("0 0 30 2 *")]
    public void InvalidExpression_IsRejected(string? expression)
    {
        Assert.False(_calculator.TryParseCronExpression(expression, out _));
    }

    [Fact]
    public void ExpressionLongerThanTheColumn_IsRejected()
    {
        var minutes = string.Join(',', Enumerable.Range(0, 60));

        Assert.True(minutes.Length + " * * * *".Length > BackupSchedule.CronExpressionMaxLength);
        Assert.False(_calculator.TryParseCronExpression($"{minutes} * * * *", out _));
    }

    // --- Occurrences --------------------------------------------------------------------------

    [Theory]
    // Daily at 02:00, every six hours, Sundays at 03:00; 2026-03-10 is a Tuesday.
    [InlineData("0 2 * * *", "2026-03-11T02:00:00Z")]
    [InlineData("0 */6 * * *", "2026-03-10T12:00:00Z")]
    [InlineData("0 3 * * 0", "2026-03-15T03:00:00Z")]
    [InlineData("*/15 * * * *", "2026-03-10T10:15:00Z")]
    public void NextOccurrence_InUtc(string expression, string expected)
    {
        var next = _calculator.NextOccurrence(expression, "UTC", Utc(2026, 3, 10, 10));

        Assert.Equal(DateTime.Parse(expected).ToUniversalTime(), next);
        Assert.Equal(DateTimeKind.Utc, next!.Value.Kind);
    }

    [Fact]
    public void NextOccurrence_IsStrictlyAfterTheGivenInstant()
    {
        Assert.Equal(Utc(2026, 3, 10, 12), _calculator.NextOccurrence("0 */6 * * *", "UTC", Utc(2026, 3, 10, 11, 59)));
        // At the occurrence itself, the next one is the one after.
        Assert.Equal(Utc(2026, 3, 10, 18), _calculator.NextOccurrence("0 */6 * * *", "UTC", Utc(2026, 3, 10, 12)));
    }

    [Theory]
    // 02:00 in Dhaka (UTC+6, no daylight saving) is 20:00 UTC the day before.
    [InlineData("Asia/Dhaka", "2026-03-10T20:00:00Z")]
    // 02:00 in Tokyo (UTC+9) is 17:00 UTC the day before.
    [InlineData("Asia/Tokyo", "2026-03-10T17:00:00Z")]
    // 02:00 in Los Angeles on 10 March 2026 (already daylight time, UTC-7) is 09:00 UTC; that has passed, so the 11th.
    [InlineData("America/Los_Angeles", "2026-03-11T09:00:00Z")]
    [InlineData("UTC", "2026-03-11T02:00:00Z")]
    public void NextOccurrence_IsReadInTheSchedulesTimeZone_AndReturnedAsUtc(string timeZoneId, string expected)
    {
        var next = _calculator.NextOccurrence("0 2 * * *", timeZoneId, Utc(2026, 3, 10, 10));

        Assert.Equal(DateTime.Parse(expected).ToUniversalTime(), next);
        Assert.Equal(DateTimeKind.Utc, next!.Value.Kind);
    }

    [Fact]
    public void NextOccurrence_DoesNotDependOnTheMachinesTimeZone()
    {
        // The same question asked with an "unspecified" and with a UTC instant gives the same answer:
        // the argument is an instant, and no local time is ever involved.
        var asUtc = _calculator.NextOccurrence("0 2 * * *", "Asia/Dhaka", Utc(2026, 3, 10, 10));
        var unspecified = _calculator.NextOccurrence("0 2 * * *", "Asia/Dhaka", new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Unspecified));

        Assert.Equal(asUtc, unspecified);
    }

    [Fact]
    public void SameLocalTime_IsADifferentInstantInWinterAndInSummer_WhereTheZoneHasDaylightSaving()
    {
        // 09:00 in Berlin: UTC+1 in January, UTC+2 in July.
        Assert.Equal(Utc(2026, 1, 15, 8), _calculator.NextOccurrence("0 9 * * *", "Europe/Berlin", Utc(2026, 1, 15)));
        Assert.Equal(Utc(2026, 7, 15, 7), _calculator.NextOccurrence("0 9 * * *", "Europe/Berlin", Utc(2026, 7, 15)));
    }

    [Fact]
    public void TimeThatDoesNotExist_OnTheDayClocksGoForward_RunsWhenTheyDo_AndNotTwice()
    {
        // New York, 8 March 2026: 02:00 becomes 03:00, so 02:30 does not exist that day.
        var midnight = Utc(2026, 3, 8, 5);

        var first = _calculator.NextOccurrence("30 2 * * *", "America/New_York", midnight);
        var second = _calculator.NextOccurrence("30 2 * * *", "America/New_York", first!.Value);

        // 03:00 daylight time, the moment the clocks jump: 07:00 UTC.
        Assert.Equal(Utc(2026, 3, 8, 7), first);
        // And then 02:30 the next day, now UTC-4: one run per day, none lost and none doubled.
        Assert.Equal(Utc(2026, 3, 9, 6, 30), second);
    }

    [Fact]
    public void TimeThatOccursTwice_OnTheDayClocksGoBack_RunsOnce()
    {
        // New York, 1 November 2026: 02:00 becomes 01:00, so 01:30 occurs twice that day.
        var midnight = Utc(2026, 11, 1, 4);
        var nextMidnight = Utc(2026, 11, 2, 5);

        var occurrences = new List<DateTime>();
        for (var next = _calculator.NextOccurrence("30 1 * * *", "America/New_York", midnight);
             next < nextMidnight;
             next = _calculator.NextOccurrence("30 1 * * *", "America/New_York", next!.Value))
        {
            occurrences.Add(next!.Value);
        }

        var only = Assert.Single(occurrences);
        Assert.Contains(only, new[] { Utc(2026, 11, 1, 5, 30), Utc(2026, 11, 1, 6, 30) });
        // The day after, 01:30 standard time, UTC-5.
        Assert.Equal(Utc(2026, 11, 2, 6, 30), _calculator.NextOccurrence("30 1 * * *", "America/New_York", only));
    }

    // --- Time zones ---------------------------------------------------------------------------

    [Theory]
    [InlineData("UTC")]
    [InlineData("Etc/UTC")]
    [InlineData("Asia/Dhaka")]
    [InlineData("Europe/Berlin")]
    [InlineData("America/New_York")]
    [InlineData("America/Argentina/Buenos_Aires")]
    public void IanaTimeZoneName_IsAccepted(string timeZoneId)
    {
        Assert.True(_calculator.IsTimeZone(timeZoneId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+06:00")]
    [InlineData("UTC+6")]
    [InlineData("GMT+6")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("Asia/Dhaka ")]
    [InlineData(" Asia/Dhaka")]
    [InlineData("asia/dhaka; DROP TABLE x")]
    [InlineData("Local")]
    // The other platform's names are not portable and are not accepted on any platform.
    [InlineData("Bangladesh Standard Time")]
    [InlineData("W. Europe Standard Time")]
    public void AnythingThatIsNotAnIanaTimeZoneName_IsRejected_AndNeverReplacedByUtc(string? timeZoneId)
    {
        Assert.False(_calculator.IsTimeZone(timeZoneId));
        if (timeZoneId is not null)
        {
            Assert.Throws<ArgumentException>(() => _calculator.NextOccurrence("0 2 * * *", timeZoneId, Utc(2026, 3, 10)));
        }
    }

    // --- The schedule itself ------------------------------------------------------------------

    [Fact]
    public void Schedule_Create_KeepsWhatItIsGiven()
    {
        var databaseId = Guid.NewGuid();
        var now = Utc(2026, 3, 10, 10);

        var schedule = BackupSchedule.Create(databaseId, "0 2 * * *", "Asia/Dhaka", enabled: true, Utc(2026, 3, 10, 20), now);

        Assert.NotEqual(Guid.Empty, schedule.Id);
        Assert.Equal(databaseId, schedule.DatabaseId);
        Assert.Equal("0 2 * * *", schedule.CronExpression);
        Assert.Equal("Asia/Dhaka", schedule.TimeZoneId);
        Assert.True(schedule.Enabled);
        Assert.Equal(Utc(2026, 3, 10, 20), schedule.NextRunAt);
        Assert.Equal(now, schedule.CreatedAt);
        Assert.Equal(now, schedule.UpdatedAt);
    }

    [Fact]
    public void Schedule_Disabled_HasNoNextRun_AndCannotBeGivenOneOrAdvanced()
    {
        var now = Utc(2026, 3, 10, 10);
        var schedule = BackupSchedule.Create(Guid.NewGuid(), "0 2 * * *", "UTC", enabled: false, nextRunAt: null, now);

        Assert.Null(schedule.NextRunAt);
        Assert.Throws<ArgumentException>(() => schedule.Update("0 2 * * *", "UTC", enabled: false, Utc(2026, 3, 11, 2), now));
        Assert.Throws<InvalidOperationException>(() => schedule.Advance(Utc(2026, 3, 11, 2), now));
    }

    [Fact]
    public void Schedule_NextRun_MustBeAUtcInstant()
    {
        var now = Utc(2026, 3, 10, 10);
        var local = new DateTime(2026, 3, 11, 2, 0, 0, DateTimeKind.Local);
        var unspecified = new DateTime(2026, 3, 11, 2, 0, 0, DateTimeKind.Unspecified);

        Assert.Throws<ArgumentException>(() => BackupSchedule.Create(Guid.NewGuid(), "0 2 * * *", "UTC", true, local, now));
        Assert.Throws<ArgumentException>(() => BackupSchedule.Create(Guid.NewGuid(), "0 2 * * *", "UTC", true, unspecified, now));
    }

    [Fact]
    public void Schedule_Advance_MovesToTheNextRun()
    {
        var schedule = BackupSchedule.Create(Guid.NewGuid(), "0 2 * * *", "UTC", true, Utc(2026, 3, 11, 2), Utc(2026, 3, 10, 10));

        schedule.Advance(Utc(2026, 3, 12, 2), Utc(2026, 3, 11, 2, 1));

        Assert.Equal(Utc(2026, 3, 12, 2), schedule.NextRunAt);
        Assert.Equal(Utc(2026, 3, 11, 2, 1), schedule.UpdatedAt);
        Assert.Equal(Utc(2026, 3, 10, 10), schedule.CreatedAt);
    }

    // --- Settings -----------------------------------------------------------------------------

    [Theory]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(1, true)]
    [InlineData(30, true)]
    [InlineData(3600, true)]
    [InlineData(3601, false)]
    public void PollInterval_MustBePositive_AndModest(int seconds, bool valid)
    {
        var options = new BackupOptions { Local = new LocalBackupOptions { RootPath = "/backups" } };
        options.Scheduler.PollIntervalSeconds = seconds;

        Assert.Equal(valid, options.Validate() is null);
        Assert.Equal(30, new BackupOptions().Scheduler.PollIntervalSeconds);
    }
}
