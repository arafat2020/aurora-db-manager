using System.Text.Json.Serialization;
using AuroraDbManager.Api.Application.BackupSchedules;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Backups;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Monitoring;

/// <summary>
/// A snapshot of what the system database says about the state of things: instances, jobs,
/// backups, restores and schedules, counted, plus the most recent failures. It reads and nothing
/// else. No instance is inspected, Docker and the backup storage are not asked, and nothing is
/// cached: a handful of small, bounded queries each time.
/// </summary>
/// <remarks>
/// Backups and restores are counted through their jobs. Every backup and every restore is one
/// job, the job's outcome is theirs, and the jobs table has the index for it.
/// </remarks>
public sealed class MonitoringSummaryService(
    AppDbContext db,
    SchedulerHeartbeat heartbeat,
    IOptions<BackupOptions> backupOptions,
    TimeProvider timeProvider)
{
    /// <summary>How far back "recently" reaches.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);

    /// <summary>How many recent failures are listed at most.</summary>
    public const int MaxRecentFailures = 10;

    public async Task<MonitoringSummaryResponse> GetAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var recentSince = now - RecentWindow;

        var instances = await db.Instances.AsNoTracking()
            .GroupBy(instance => instance.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var unfinished = await db.Jobs.AsNoTracking()
            .Where(job => job.Status == JobStatus.Pending || job.Status == JobStatus.Running)
            .GroupBy(job => new { job.Type, job.Status })
            .Select(group => new { group.Key.Type, group.Key.Status, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var failedRecently = await db.Jobs.AsNoTracking()
            .Where(job => job.Status == JobStatus.Failed && job.CompletedAt >= recentSince)
            .GroupBy(job => job.Type)
            .Select(group => new { Type = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var recentFailures = await db.Jobs.AsNoTracking()
            .Where(job => job.Status == JobStatus.Failed && job.CompletedAt >= recentSince)
            .OrderByDescending(job => job.CompletedAt)
            .ThenByDescending(job => job.Id)
            .Take(MaxRecentFailures)
            .Select(job => new { job.Id, job.Type, job.InstanceId, job.DatabaseId, job.BackupId, job.ErrorCode, job.CompletedAt })
            .ToListAsync(cancellationToken);

        // A schedule whose run is this far behind was not picked up by the last two passes: the
        // scheduler is not running, or cannot deal with it. Told from the database, so it holds
        // across restarts, which the in-memory heartbeat does not.
        var overdueBefore = now - TimeSpan.FromSeconds(2 * backupOptions.Value.Scheduler.PollIntervalSeconds);
        var enabledSchedules = await db.BackupSchedules.CountAsync(schedule => schedule.Enabled, cancellationToken);
        var overdueSchedules = await db.BackupSchedules
            .CountAsync(schedule => schedule.Enabled && schedule.NextRunAt != null && schedule.NextRunAt < overdueBefore, cancellationToken);
        var nextRunAt = await db.BackupSchedules
            .Where(schedule => schedule.Enabled && schedule.NextRunAt != null)
            .MinAsync(schedule => schedule.NextRunAt, cancellationToken);

        int Unfinished(JobStatus status, JobType? type = null) =>
            unfinished.Where(row => row.Status == status && (type is null || row.Type == type)).Sum(row => row.Count);
        int FailedRecently(JobType? type = null) =>
            failedRecently.Where(row => type is null || row.Type == type).Sum(row => row.Count);
        int Instances(InstanceStatus status) => instances.Where(row => row.Status == status).Sum(row => row.Count);

        var instanceSummary = new InstanceSummary(
            instances.Sum(row => row.Count),
            Instances(InstanceStatus.Provisioning),
            Instances(InstanceStatus.Running),
            Instances(InstanceStatus.Stopped),
            Instances(InstanceStatus.Failed));
        var jobSummary = new OperationSummary(Unfinished(JobStatus.Pending), Unfinished(JobStatus.Running), FailedRecently());

        // Something an operator should look at, or nothing. Whether Aurora itself can work is
        // what /health/ready answers; this never says "unhealthy".
        var status = instanceSummary.Failed > 0 || jobSummary.FailedRecently > 0 || overdueSchedules > 0
            ? HealthStatus.Degraded
            : HealthStatus.Healthy;

        return new MonitoringSummaryResponse(
            status,
            now,
            (int)RecentWindow.TotalHours,
            instanceSummary,
            jobSummary,
            new OperationSummary(
                Unfinished(JobStatus.Pending, JobType.BackupDatabase),
                Unfinished(JobStatus.Running, JobType.BackupDatabase),
                FailedRecently(JobType.BackupDatabase)),
            new OperationSummary(
                Unfinished(JobStatus.Pending, JobType.RestoreDatabase),
                Unfinished(JobStatus.Running, JobType.RestoreDatabase),
                FailedRecently(JobType.RestoreDatabase)),
            new SchedulerSummary(
                enabledSchedules,
                overdueSchedules,
                nextRunAt is { } next ? Utc(next) : null,
                heartbeat.LastSuccessfulPassAt),
            recentFailures
                .Select(job => new RecentFailure(
                    job.Id,
                    job.Type,
                    job.InstanceId,
                    job.DatabaseId,
                    job.BackupId,
                    // The stable code only. The message stays on the job, where it is asked for by id.
                    job.ErrorCode ?? string.Empty,
                    Utc(job.CompletedAt ?? now)))
                .ToList());
    }

    // The stored instants are UTC whatever kind the database provider hands back.
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <param name="Status">
/// <c>healthy</c>, or <c>degraded</c> when there is something to look at: a failed instance, a
/// job that failed recently, or a schedule that is overdue.
/// </param>
/// <param name="GeneratedAt">UTC time of the snapshot.</param>
/// <param name="RecentWindowHours">How many hours back "recently" reaches.</param>
/// <param name="Instances">Instances by their status on record. No instance is inspected for this.</param>
/// <param name="Jobs">All background jobs.</param>
/// <param name="Backups">The backup jobs among them.</param>
/// <param name="Restores">The restore jobs among them.</param>
/// <param name="Scheduler">The backup scheduler and its schedules.</param>
/// <param name="RecentFailures">The jobs that failed most recently, newest first; ten at most.</param>
public sealed record MonitoringSummaryResponse(
    HealthStatus Status,
    DateTime GeneratedAt,
    int RecentWindowHours,
    InstanceSummary Instances,
    OperationSummary Jobs,
    OperationSummary Backups,
    OperationSummary Restores,
    SchedulerSummary Scheduler,
    IReadOnlyList<RecentFailure> RecentFailures);

public sealed record InstanceSummary(int Total, int Provisioning, int Running, int Stopped, int Failed);

/// <param name="Pending">Waiting to be picked up.</param>
/// <param name="Running">Being worked on, retries included.</param>
/// <param name="FailedRecently">Failed for good within the recent window.</param>
public sealed record OperationSummary(int Pending, int Running, int FailedRecently);

/// <param name="EnabledSchedules">Schedules that are active.</param>
/// <param name="OverdueSchedules">
/// Active schedules whose next run is more than two scheduler intervals in the past: a sign that
/// the scheduler is not running or cannot deal with them.
/// </param>
/// <param name="NextRunAt">UTC time of the earliest next run among the active schedules; null if there is none.</param>
/// <param name="LastSuccessfulPassAt">
/// UTC time of this process's last scheduler pass that went through; null if there has been none
/// since it started. Kept in memory only.
/// </param>
public sealed record SchedulerSummary(int EnabledSchedules, int OverdueSchedules, DateTime? NextRunAt, DateTime? LastSuccessfulPassAt);

/// <param name="JobId">The failed job; <c>GET /api/v1/jobs/{id}</c> has the rest.</param>
/// <param name="Type">What kind of work it was.</param>
/// <param name="InstanceId">The instance it worked on.</param>
/// <param name="DatabaseId">The database it worked on, if any.</param>
/// <param name="BackupId">The backup it produced or restored, if any.</param>
/// <param name="ErrorCode">Stable, machine-readable error code.</param>
/// <param name="FailedAt">UTC time the job failed.</param>
public sealed record RecentFailure(
    Guid JobId,
    JobType Type,
    Guid InstanceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? DatabaseId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? BackupId,
    string ErrorCode,
    DateTime FailedAt);
