using System.Diagnostics.Metrics;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;

namespace AuroraDbManager.Api.Application.Monitoring;

/// <summary>
/// The application's metrics: one <see cref="Meter"/>, named <see cref="MeterName"/>, and the
/// instruments on it. These are standard .NET metrics; whatever listens to the meter, <c>dotnet-counters</c>
/// or an exporter, sees them, and nothing is stored here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Observing only.</b> The services report what they did at the boundaries of an operation;
/// nothing is decided from a metric, and a recording that fails, because of a faulty listener for
/// instance, is swallowed here and never reaches the operation that reported it.
/// </para>
/// <para>
/// <b>Tags.</b> Only values from small fixed sets: the job type, the engine, the storage type, a
/// stable error code, a skip reason. Never an id, a name, a path, an object key or a message:
/// those grow without bound and a series per value would make the metrics useless.
/// </para>
/// </remarks>
public sealed class AuroraMetrics
{
    public const string MeterName = "AuroraDbManager";

    public const string JobTypeTag = "job_type";
    public const string EngineTag = "engine";
    public const string StorageTypeTag = "storage_type";
    public const string ErrorCodeTag = "error_code";
    public const string StatusTag = "status";
    public const string ReasonTag = "reason";

    public const string Completed = "completed";
    public const string Failed = "failed";

    /// <summary>The tag value for something that could not be determined, such as the engine of an instance that is gone.</summary>
    public const string Unknown = "unknown";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuroraMetrics> _logger;

    private readonly Counter<long> _jobsStarted;
    private readonly Counter<long> _jobsCompleted;
    private readonly Counter<long> _jobsFailed;
    private readonly Counter<long> _jobsRetried;
    private readonly Counter<long> _jobsCancelled;
    private readonly Histogram<double> _jobDuration;

    private readonly Counter<long> _backupsStarted;
    private readonly Counter<long> _backupsCompleted;
    private readonly Counter<long> _backupsFailed;
    private readonly Histogram<double> _backupDuration;
    private readonly Histogram<long> _backupSize;

    private readonly Counter<long> _restoresStarted;
    private readonly Counter<long> _restoresCompleted;
    private readonly Counter<long> _restoresFailed;
    private readonly Histogram<double> _restoreDuration;

    private readonly Counter<long> _schedulerRuns;
    private readonly Counter<long> _schedulerFailures;
    private readonly Histogram<double> _schedulerPassDuration;
    private readonly Counter<long> _scheduledBackupsTriggered;
    private readonly Counter<long> _scheduledBackupsSkipped;

    public AuroraMetrics(IMeterFactory meterFactory, IServiceScopeFactory scopeFactory, ILogger<AuroraMetrics> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var meter = meterFactory.Create(MeterName);

        _jobsStarted = meter.CreateCounter<long>("aurora_jobs_started_total", description: "Jobs an execution claimed and began to work on.");
        _jobsCompleted = meter.CreateCounter<long>("aurora_jobs_completed_total", description: "Jobs that completed.");
        _jobsFailed = meter.CreateCounter<long>("aurora_jobs_failed_total", description: "Jobs that failed for good, all attempts used.");
        _jobsRetried = meter.CreateCounter<long>("aurora_jobs_retried_total", description: "Failed attempts that are followed by another attempt.");
        _jobsCancelled = meter.CreateCounter<long>("aurora_jobs_cancelled_total", description: "Executions given up before the job finished: a shutdown, or a lost lease.");
        _jobDuration = meter.CreateHistogram<double>("aurora_job_duration", unit: "s", description: "Time from claiming a job to its completion or final failure, retries included.");

        // The persisted jobs are the queue: the in-memory channel only holds ids, is lost with the
        // process, and may hold an id twice. Read when a listener collects, not on a timer.
        meter.CreateObservableGauge("aurora_jobs_pending", () => ObserveJobs(JobStatus.Pending), description: "Jobs waiting to be picked up, as persisted.");
        meter.CreateObservableGauge("aurora_jobs_running", () => ObserveJobs(JobStatus.Running), description: "Jobs being worked on, as persisted.");

        _backupsStarted = meter.CreateCounter<long>("aurora_backups_started_total", description: "Backups their job began to work on.");
        _backupsCompleted = meter.CreateCounter<long>("aurora_backups_completed_total", description: "Backups stored and verified.");
        _backupsFailed = meter.CreateCounter<long>("aurora_backups_failed_total", description: "Backups that failed for good.");
        _backupDuration = meter.CreateHistogram<double>("aurora_backup_duration", unit: "s", description: "Time one attempt took to dump, store and verify a backup.");
        _backupSize = meter.CreateHistogram<long>("aurora_backup_size_bytes", unit: "By", description: "Size of completed backups.");

        _restoresStarted = meter.CreateCounter<long>("aurora_restores_started_total", description: "Restores their job began to work on.");
        _restoresCompleted = meter.CreateCounter<long>("aurora_restores_completed_total", description: "Restores that completed.");
        _restoresFailed = meter.CreateCounter<long>("aurora_restores_failed_total", description: "Restores that failed for good.");
        _restoreDuration = meter.CreateHistogram<double>("aurora_restore_duration", unit: "s", description: "Time one attempt took to fetch, verify and load a backup.");

        _schedulerRuns = meter.CreateCounter<long>("aurora_scheduler_runs_total", description: "Passes of the backup scheduler.");
        _schedulerFailures = meter.CreateCounter<long>("aurora_scheduler_failures_total", description: "Scheduler passes that failed, and schedules a pass could not deal with.");
        _schedulerPassDuration = meter.CreateHistogram<double>("aurora_scheduler_pass_duration", unit: "s", description: "Time one scheduler pass took.");
        _scheduledBackupsTriggered = meter.CreateCounter<long>("aurora_scheduled_backups_triggered_total", description: "Backups created by a schedule.");
        _scheduledBackupsSkipped = meter.CreateCounter<long>("aurora_scheduled_backups_skipped_total", description: "Scheduled occurrences for which no backup was created.");
    }

    // --- Jobs ---------------------------------------------------------------------------------

    public void JobStarted(JobType type) => Record(() => _jobsStarted.Add(1, Tag(JobTypeTag, type)));

    public void JobCompleted(JobType type, TimeSpan duration) => Record(() =>
    {
        _jobsCompleted.Add(1, Tag(JobTypeTag, type));
        _jobDuration.Record(duration.TotalSeconds, Tag(JobTypeTag, type), Tag(StatusTag, Completed));
    });

    public void JobFailed(JobType type, string errorCode, TimeSpan duration) => Record(() =>
    {
        _jobsFailed.Add(1, Tag(JobTypeTag, type), Tag(ErrorCodeTag, errorCode));
        _jobDuration.Record(duration.TotalSeconds, Tag(JobTypeTag, type), Tag(StatusTag, Failed));
    });

    public void JobRetried(JobType type) => Record(() => _jobsRetried.Add(1, Tag(JobTypeTag, type)));

    public void JobCancelled(JobType type) => Record(() => _jobsCancelled.Add(1, Tag(JobTypeTag, type)));

    // --- Backups ------------------------------------------------------------------------------

    public void BackupStarted(InstanceEngine? engine, BackupStorageType storageType) =>
        Record(() => _backupsStarted.Add(1, Tag(EngineTag, engine), Tag(StorageTypeTag, storageType)));

    public void BackupCompleted(InstanceEngine engine, BackupStorageType storageType, TimeSpan attemptDuration, long sizeBytes) => Record(() =>
    {
        _backupsCompleted.Add(1, Tag(EngineTag, engine), Tag(StorageTypeTag, storageType));
        _backupDuration.Record(attemptDuration.TotalSeconds, Tag(EngineTag, engine), Tag(StorageTypeTag, storageType), Tag(StatusTag, Completed));
        _backupSize.Record(sizeBytes, Tag(EngineTag, engine), Tag(StorageTypeTag, storageType));
    });

    /// <summary>One attempt failed; the backup itself has not: its job may retry.</summary>
    public void BackupAttemptFailed(InstanceEngine engine, BackupStorageType storageType, TimeSpan attemptDuration) => Record(() =>
        _backupDuration.Record(attemptDuration.TotalSeconds, Tag(EngineTag, engine), Tag(StorageTypeTag, storageType), Tag(StatusTag, Failed)));

    public void BackupFailed(InstanceEngine? engine, BackupStorageType storageType, string errorCode) => Record(() =>
        _backupsFailed.Add(1, Tag(EngineTag, engine), Tag(StorageTypeTag, storageType), Tag(ErrorCodeTag, errorCode)));

    // --- Restores -----------------------------------------------------------------------------

    public void RestoreStarted(InstanceEngine engine, BackupStorageType storageType) =>
        Record(() => _restoresStarted.Add(1, Tag(EngineTag, engine), Tag(StorageTypeTag, storageType)));

    public void RestoreCompleted(InstanceEngine engine, BackupStorageType storageType, TimeSpan attemptDuration) => Record(() =>
    {
        _restoresCompleted.Add(1, Tag(EngineTag, engine), Tag(StorageTypeTag, storageType));
        _restoreDuration.Record(attemptDuration.TotalSeconds, Tag(EngineTag, engine), Tag(StorageTypeTag, storageType), Tag(StatusTag, Completed));
    });

    /// <summary>One attempt failed; the restore itself has not: its job may retry.</summary>
    public void RestoreAttemptFailed(InstanceEngine engine, BackupStorageType storageType, TimeSpan attemptDuration) => Record(() =>
        _restoreDuration.Record(attemptDuration.TotalSeconds, Tag(EngineTag, engine), Tag(StorageTypeTag, storageType), Tag(StatusTag, Failed)));

    public void RestoreFailed(InstanceEngine? engine, BackupStorageType? storageType, string errorCode) => Record(() =>
        _restoresFailed.Add(1, Tag(EngineTag, engine), Tag(StorageTypeTag, storageType), Tag(ErrorCodeTag, errorCode)));

    // --- Scheduler ----------------------------------------------------------------------------

    public void SchedulerPassCompleted(TimeSpan duration) => Record(() =>
    {
        _schedulerRuns.Add(1);
        _schedulerPassDuration.Record(duration.TotalSeconds);
    });

    public void SchedulerFailed() => Record(() => _schedulerFailures.Add(1));

    public void ScheduledBackupTriggered() => Record(() => _scheduledBackupsTriggered.Add(1));

    public void ScheduledBackupSkipped(string reason) => Record(() => _scheduledBackupsSkipped.Add(1, Tag(ReasonTag, reason)));

    // ------------------------------------------------------------------------------------------

    /// <summary>The value an enum has as a tag: the same snake_case text the API and the database use.</summary>
    public static string TagValue<TEnum>(TEnum value) where TEnum : struct, Enum => EnumStorage.ToDbValue(value);

    private static KeyValuePair<string, object?> Tag(string name, string value) => new(name, value);

    private static KeyValuePair<string, object?> Tag<TEnum>(string name, TEnum value) where TEnum : struct, Enum =>
        new(name, TagValue(value));

    private static KeyValuePair<string, object?> Tag<TEnum>(string name, TEnum? value) where TEnum : struct, Enum =>
        new(name, value is { } known ? TagValue(known) : Unknown);

    private void Record(Action record)
    {
        try
        {
            record();
        }
        catch (Exception exception)
        {
            // A listener that throws must not fail the job, backup or scheduler pass being measured.
            _logger.LogDebug(exception, "A metric could not be recorded");
        }
    }

    private IEnumerable<Measurement<long>> ObserveJobs(JobStatus status)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return [new Measurement<long>(db.Jobs.Count(job => job.Status == status))];
        }
        catch (Exception exception)
        {
            // No measurement rather than a wrong one, for example while the system database is down.
            _logger.LogDebug(exception, "The number of {JobStatus} jobs could not be read for the metrics", status);
            return [];
        }
    }
}
