namespace AuroraDbManager.Api.Application.BackupSchedules;

/// <summary>
/// When the backup scheduler last made a pass that went through. Kept in memory only: it says
/// that the scheduler of this process is alive, and is unknown again after a restart until the
/// first pass. What is due, and when, is never read from here; that is each schedule's persisted
/// next run.
/// </summary>
public sealed class SchedulerHeartbeat
{
    private long _lastSuccessfulPassTicks;

    /// <summary>UTC time of the last pass that completed without a failure; null if there has been none since startup.</summary>
    public DateTime? LastSuccessfulPassAt =>
        Interlocked.Read(ref _lastSuccessfulPassTicks) is var ticks and not 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;

    public void RecordSuccessfulPass(DateTime utcNow) => Interlocked.Exchange(ref _lastSuccessfulPassTicks, utcNow.Ticks);
}
