using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using AuroraDbManager.Api.Application.Monitoring;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Tests.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Monitoring;

/// <summary>
/// What the application measures at the boundaries of jobs, backups, restores and scheduler
/// passes. Each test listens to its own host's meter only (see <see cref="MetricsRecorder"/>), so
/// exact values can be asserted while other tests run in the same process.
/// </summary>
public sealed class MetricsTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 3, 10, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;
    private readonly MetricsRecorder _metrics;

    public MetricsTests()
    {
        _factory = new ApiFactory
        {
            Clock = _clock,
            ConfigureBackups = options =>
            {
                options.S3.Bucket = FakeS3ObjectStore.Bucket;
                options.S3.Region = "us-east-1";
            }
        };
        _client = _factory.CreateClient();
        _metrics = _factory.RecordMetrics();
    }

    public void Dispose()
    {
        _metrics.Dispose();
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<Guid> ReadyDatabaseAsync(string engine = "postgres", string name = "app")
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client, name: $"instance-{name}", engine: engine);
        return await _factory.CreateReadyDatabaseAsync(_client, instanceId, name);
    }

    private async Task ScheduleAsync(Guid databaseId) =>
        await (await _client.PostAsJsonAsync(
            $"{DatabasesUrl}/{databaseId}/backup-schedule", new { cronExpression = "0 2 * * *", timeZoneId = "UTC" }))
            .ReadJsonAsync(HttpStatusCode.Created);

    // The schedules here are daily at 02:00 UTC; each call moves on to the next day's occurrence.
    private void MoveClockToTheNextOccurrence() =>
        _clock.SetUtcNow(new DateTimeOffset(_clock.GetUtcNow().UtcDateTime.Date.AddDays(1).AddHours(2), TimeSpan.Zero));

    // --- Jobs ---------------------------------------------------------------------------------

    [Fact]
    public async Task CompletedJob_IsCountedAsStartedAndCompleted_ByType_WithItsDuration()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal(1, _metrics.Total("aurora_jobs_started_total", "job_type", "provision_instance"));
        Assert.Equal(1, _metrics.Total("aurora_jobs_completed_total", "job_type", "provision_instance"));
        Assert.Equal(0, _metrics.Total("aurora_jobs_failed_total"));
        Assert.Equal(0, _metrics.Total("aurora_jobs_retried_total"));
        Assert.Equal(0, _metrics.Total("aurora_jobs_cancelled_total"));

        var duration = Assert.Single(_metrics.Of("aurora_job_duration"));
        Assert.Equal("provision_instance", duration.Tag("job_type"));
        Assert.Equal("completed", duration.Tag("status"));
        Assert.True(duration.Value >= 0);
    }

    [Fact]
    public async Task FailedJob_IsCountedAsRetriedThenFailed_WithItsStableErrorCode()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();
        _factory.Provisioner.FailAllCalls();

        await _factory.ProcessJobAsync(jobId);

        // Three attempts: two of them followed by another.
        Assert.Equal(1, _metrics.Total("aurora_jobs_started_total"));
        Assert.Equal(2, _metrics.Total("aurora_jobs_retried_total", "job_type", "provision_instance"));
        Assert.Equal(0, _metrics.Total("aurora_jobs_completed_total"));

        var failed = Assert.Single(_metrics.Of("aurora_jobs_failed_total"));
        Assert.Equal("provision_instance", failed.Tag("job_type"));
        Assert.Equal("PROVISIONING_FAILED", failed.Tag("error_code"));
        Assert.Equal("failed", Assert.Single(_metrics.Of("aurora_job_duration")).Tag("status"));
    }

    [Fact]
    public async Task JobThatSucceedsOnASecondAttempt_IsRetriedOnce_AndCompleted()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();
        _factory.Provisioner.FailNextCalls(1);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal(1, _metrics.Total("aurora_jobs_retried_total"));
        Assert.Equal(1, _metrics.Total("aurora_jobs_completed_total"));
        Assert.Equal(0, _metrics.Total("aurora_jobs_failed_total"));
    }

    [Fact]
    public async Task JobInterruptedByShutdown_IsCountedAsCancelled_NotAsCompletedOrFailed()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();
        _factory.Provisioner.Block();
        using var shutdown = new CancellationTokenSource();

        var processing = _factory.ProcessJobAsync(jobId, shutdown.Token);
        await _factory.Provisioner.WaitForCallsAsync();
        await shutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);

        Assert.Equal(1, _metrics.Total("aurora_jobs_started_total"));
        Assert.Equal(1, _metrics.Total("aurora_jobs_cancelled_total", "job_type", "provision_instance"));
        Assert.Equal(0, _metrics.Total("aurora_jobs_completed_total"));
        Assert.Equal(0, _metrics.Total("aurora_jobs_failed_total"));
        Assert.Empty(_metrics.Of("aurora_job_duration"));
    }

    [Fact]
    public async Task JobDuration_IsElapsedTime_FromTheClaimToTheEnd()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();
        _factory.Provisioner.Block();

        var processing = _factory.ProcessJobAsync(jobId);
        await _factory.Provisioner.WaitForCallsAsync();
        _clock.Advance(TimeSpan.FromSeconds(42));
        _factory.Provisioner.Release();
        await processing;

        Assert.Equal(42, Assert.Single(_metrics.Of("aurora_job_duration")).Value);
    }

    [Fact]
    public async Task PendingAndRunningJobs_AreReadFromThePersistedJobs_WhenCollected()
    {
        Assert.Equal(0, _metrics.Observe("aurora_jobs_pending"));
        Assert.Equal(0, _metrics.Observe("aurora_jobs_running"));

        var (_, first) = await _client.CreateInstanceAsync(name: "first");
        await _client.CreateInstanceAsync(name: "second");
        Assert.Equal(2, _metrics.Observe("aurora_jobs_pending"));

        await _factory.SimulateAbandonedExecutionAsync(first, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(1, _metrics.Observe("aurora_jobs_pending"));
        Assert.Equal(1, _metrics.Observe("aurora_jobs_running"));
    }

    // --- Backups ------------------------------------------------------------------------------

    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    public async Task CompletedBackup_IsCountedAsStartedAndCompleted_ByEngineAndStorage_WithDurationAndSize(string engine)
    {
        var databaseId = await ReadyDatabaseAsync(engine);

        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        var started = Assert.Single(_metrics.Of("aurora_backups_started_total"));
        Assert.Equal(engine, started.Tag("engine"));
        Assert.Equal("local", started.Tag("storage_type"));

        var completed = Assert.Single(_metrics.Of("aurora_backups_completed_total"));
        Assert.Equal(engine, completed.Tag("engine"));
        Assert.Equal("local", completed.Tag("storage_type"));
        Assert.Empty(_metrics.Of("aurora_backups_failed_total"));

        var duration = Assert.Single(_metrics.Of("aurora_backup_duration"));
        Assert.Equal("completed", duration.Tag("status"));
        Assert.Equal(engine, duration.Tag("engine"));

        var size = Assert.Single(_metrics.Of("aurora_backup_size_bytes"));
        Assert.Equal((await _client.GetBackupAsync(backupId)).GetProperty("sizeBytes").GetInt64(), size.Value);
        Assert.Equal(1, _metrics.Total("aurora_jobs_completed_total", "job_type", "backup_database"));
    }

    [Fact]
    public async Task BackupToS3_IsTaggedWithThatStorage()
    {
        using var factory = new ApiFactory
        {
            ConfigureBackups = options =>
            {
                options.StorageType = BackupStorageType.S3;
                options.S3.Bucket = FakeS3ObjectStore.Bucket;
                options.S3.Region = "us-east-1";
            }
        };
        using var client = factory.CreateClient();
        using var metrics = factory.RecordMetrics();
        var instanceId = await factory.CreateRunningInstanceAsync(client);
        var databaseId = await factory.CreateReadyDatabaseAsync(client, instanceId, "app");

        await factory.CreateCompletedBackupAsync(client, databaseId);

        Assert.Equal("s3", Assert.Single(metrics.Of("aurora_backups_started_total")).Tag("storage_type"));
        Assert.Equal("s3", Assert.Single(metrics.Of("aurora_backups_completed_total")).Tag("storage_type"));
        // This host's backup was not measured on the other host's meter.
        Assert.Empty(_metrics.Of("aurora_backups_completed_total"));
    }

    [Fact]
    public async Task FailedBackup_IsCountedOnce_WithItsErrorCode_AndEveryFailedAttemptHasADuration()
    {
        var databaseId = await ReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        _factory.DumpTools.FailAllRuns();

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("failed", (await _client.GetBackupAsync(backupId)).Status());
        // Started once and failed once, however many attempts there were.
        Assert.Equal(1, _metrics.Total("aurora_backups_started_total"));
        Assert.Equal(0, _metrics.Total("aurora_backups_completed_total"));

        var failed = Assert.Single(_metrics.Of("aurora_backups_failed_total"));
        Assert.Equal("postgres", failed.Tag("engine"));
        Assert.Equal("local", failed.Tag("storage_type"));
        Assert.Equal("BACKUP_PROCESS_FAILED", failed.Tag("error_code"));

        Assert.Equal(3, _metrics.Of("aurora_backup_duration").Count);
        Assert.All(_metrics.Of("aurora_backup_duration"), duration => Assert.Equal("failed", duration.Tag("status")));
        Assert.Empty(_metrics.Of("aurora_backup_size_bytes"));
    }

    [Fact]
    public async Task BackupThatSucceedsOnARetry_IsStartedOnce_AndCompleted()
    {
        var databaseId = await ReadyDatabaseAsync();
        var (_, jobId) = await _client.CreateBackupAsync(databaseId);
        _factory.DumpTools.FailNextRuns(1);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal(1, _metrics.Total("aurora_backups_started_total"));
        Assert.Equal(1, _metrics.Total("aurora_backups_completed_total"));
        Assert.Equal(0, _metrics.Total("aurora_backups_failed_total"));
        Assert.Equal(["failed", "completed"], _metrics.Of("aurora_backup_duration").Select(duration => duration.Tag("status")));
    }

    // --- Restores -----------------------------------------------------------------------------

    [Fact]
    public async Task CompletedRestore_IsCountedAsStartedAndCompleted_ByEngineAndStorage_WithItsDuration()
    {
        var databaseId = await ReadyDatabaseAsync();
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
        var started = Assert.Single(_metrics.Of("aurora_restores_started_total"));
        Assert.Equal("postgres", started.Tag("engine"));
        Assert.Equal("local", started.Tag("storage_type"));
        Assert.Equal(1, _metrics.Total("aurora_restores_completed_total", "engine", "postgres"));
        Assert.Equal(0, _metrics.Total("aurora_restores_failed_total"));
        Assert.Equal("completed", Assert.Single(_metrics.Of("aurora_restore_duration")).Tag("status"));
    }

    [Fact]
    public async Task FailedRestore_IsCountedOnce_WithItsErrorCode()
    {
        var databaseId = await ReadyDatabaseAsync();
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        _factory.DumpTools.FailAllRuns();

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal(1, _metrics.Total("aurora_restores_started_total"));
        Assert.Equal(0, _metrics.Total("aurora_restores_completed_total"));

        var failed = Assert.Single(_metrics.Of("aurora_restores_failed_total"));
        Assert.Equal("postgres", failed.Tag("engine"));
        Assert.Equal("local", failed.Tag("storage_type"));
        Assert.Equal(job.GetProperty("error").GetProperty("code").GetString(), failed.Tag("error_code"));
        Assert.StartsWith("RESTORE_", failed.Tag("error_code"));
        Assert.Equal(3, _metrics.Of("aurora_restore_duration").Count);
    }

    // --- Scheduler ----------------------------------------------------------------------------

    [Fact]
    public async Task SchedulerPass_IsCounted_EvenWhenNothingIsDue()
    {
        await _factory.RunSchedulerAsync();
        await _factory.RunSchedulerAsync();

        Assert.Equal(2, _metrics.Total("aurora_scheduler_runs_total"));
        Assert.Equal(2, _metrics.Of("aurora_scheduler_pass_duration").Count);
        Assert.Equal(0, _metrics.Total("aurora_scheduler_failures_total"));
        Assert.Equal(0, _metrics.Total("aurora_scheduled_backups_triggered_total"));
        Assert.Equal(0, _metrics.Total("aurora_scheduled_backups_skipped_total"));
    }

    [Fact]
    public async Task DueSchedule_IsCountedAsTriggered()
    {
        await ScheduleAsync(await ReadyDatabaseAsync());
        MoveClockToTheNextOccurrence();

        Assert.Single(await _factory.RunSchedulerAsync());

        Assert.Equal(1, _metrics.Total("aurora_scheduled_backups_triggered_total"));
        Assert.Equal(0, _metrics.Total("aurora_scheduled_backups_skipped_total"));
        Assert.Equal(1, _metrics.Total("aurora_scheduler_runs_total"));
    }

    [Fact]
    public async Task ScheduleWhoseDatabaseIsAlreadyBeingBackedUp_IsCountedAsSkipped_WithTheReason()
    {
        var databaseId = await ReadyDatabaseAsync();
        await ScheduleAsync(databaseId);
        await _client.CreateBackupAsync(databaseId);
        MoveClockToTheNextOccurrence();

        Assert.Empty(await _factory.RunSchedulerAsync());

        var skipped = Assert.Single(_metrics.Of("aurora_scheduled_backups_skipped_total"));
        Assert.Equal("backup_in_progress", skipped.Tag("reason"));
        Assert.Equal(0, _metrics.Total("aurora_scheduled_backups_triggered_total"));
    }

    [Fact]
    public async Task ScheduleWhoseInstanceIsNotRunning_IsCountedAsSkipped_WithTheReason()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        await ScheduleAsync(databaseId);
        await _factory.SetInstanceStatusAsync(instanceId, Domain.Instances.InstanceStatus.Stopped);
        MoveClockToTheNextOccurrence();

        Assert.Empty(await _factory.RunSchedulerAsync());

        Assert.Equal("instance_not_ready", Assert.Single(_metrics.Of("aurora_scheduled_backups_skipped_total")).Tag("reason"));
    }

    [Fact]
    public async Task SchedulerPassThatFails_IsCountedAsAFailure_AndStillAsARun()
    {
        // The pass cannot even ask which schedules are due.
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE backup_schedules"));

        await Assert.ThrowsAnyAsync<Exception>(() => _factory.RunSchedulerAsync());

        Assert.Equal(1, _metrics.Total("aurora_scheduler_failures_total"));
        Assert.Equal(1, _metrics.Total("aurora_scheduler_runs_total"));
    }

    // --- Cardinality and failure semantics ----------------------------------------------------

    [Fact]
    public async Task NoMetric_IsEverTaggedWithAnId_AName_APath_OrAMessage()
    {
        // A bit of everything: jobs, a backup, a failed backup, a restore, a failed restore, the scheduler.
        var databaseId = await ReadyDatabaseAsync(name: "orders");
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        await _factory.ProcessJobAsync(await ApiFactory.RequestRestoreAsync(_client, backupId));
        await ScheduleAsync(databaseId);
        MoveClockToTheNextOccurrence();
        foreach (var jobId in await _factory.RunSchedulerAsync())
        {
            await _factory.ProcessJobAsync(jobId);
        }

        _factory.DumpTools.FailAllRuns("raw-tool-detail: something went wrong");
        var (_, failingBackup) = await _client.CreateBackupAsync(databaseId);
        await _factory.ProcessJobAsync(failingBackup);
        await _factory.ProcessJobAsync(await ApiFactory.RequestRestoreAsync(_client, backupId));
        var (_, failingProvision) = await _client.CreateInstanceAsync(name: "doomed");
        _factory.Provisioner.FailAllCalls();
        await _factory.ProcessJobAsync(failingProvision);
        _metrics.Observe("aurora_jobs_pending");

        string[] allowedTags = ["job_type", "engine", "storage_type", "error_code", "status", "reason"];
        var measurements = _metrics.All;
        Assert.NotEmpty(measurements);
        Assert.All(measurements, measurement => Assert.All(measurement.Tags, tag =>
        {
            Assert.Contains(tag.Key, allowedTags);
            var value = Assert.IsType<string>(tag.Value);
            Assert.False(Guid.TryParse(value, out _), $"{measurement.Instrument} is tagged with an id: {tag.Key}={value}");
            // Fixed vocabularies only: lower-case words or an upper-case error code, never free text.
            Assert.Matches("^([a-z0-9_]+|[A-Z0-9_]+)$", value);
        }));

        // Bounded: however much happened, the distinct series stay few.
        var series = measurements
            .Select(measurement => $"{measurement.Instrument}|{string.Join(',', measurement.Tags.OrderBy(tag => tag.Key).Select(tag => $"{tag.Key}={tag.Value}"))}")
            .Distinct()
            .Count();
        Assert.InRange(series, 1, 60);
    }

    [Fact]
    public async Task EveryDocumentedInstrument_ExistsOnTheAuroraMeter()
    {
        var names = new List<string>();
        using var listener = new MeterListener();
        var meters = _factory.Services.GetRequiredService<IMeterFactory>();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == AuroraMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, meters))
            {
                names.Add(instrument.Name);
            }
        };
        listener.Start();
        await Task.CompletedTask;

        Assert.Equal(
            [
                "aurora_backup_duration",
                "aurora_backup_size_bytes",
                "aurora_backups_completed_total",
                "aurora_backups_failed_total",
                "aurora_backups_started_total",
                "aurora_job_duration",
                "aurora_jobs_cancelled_total",
                "aurora_jobs_completed_total",
                "aurora_jobs_failed_total",
                "aurora_jobs_pending",
                "aurora_jobs_retried_total",
                "aurora_jobs_running",
                "aurora_jobs_started_total",
                "aurora_restore_duration",
                "aurora_restores_completed_total",
                "aurora_restores_failed_total",
                "aurora_restores_started_total",
                "aurora_scheduled_backups_skipped_total",
                "aurora_scheduled_backups_triggered_total",
                "aurora_scheduler_failures_total",
                "aurora_scheduler_pass_duration",
                "aurora_scheduler_runs_total"
            ],
            names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ListenerThatThrows_DoesNotFailTheOperationsBeingMeasured()
    {
        using var faulty = new MeterListener();
        var meters = _factory.Services.GetRequiredService<IMeterFactory>();
        faulty.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AuroraMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, meters))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        faulty.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("listener is broken"));
        faulty.SetMeasurementEventCallback<double>((_, _, _, _) => throw new InvalidOperationException("listener is broken"));
        faulty.Start();

        // Provisioning, database creation, a backup, a restore and a scheduler pass: all go through.
        var databaseId = await ReadyDatabaseAsync();
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var restoreJob = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await _factory.ProcessJobAsync(restoreJob);
        Assert.Equal("completed", (await _client.GetJobAsync(restoreJob)).Status());

        await ScheduleAsync(databaseId);
        MoveClockToTheNextOccurrence();
        var scheduled = Assert.Single(await _factory.RunSchedulerAsync());
        await _factory.ProcessJobAsync(scheduled);
        Assert.Equal("completed", (await _client.GetJobAsync(scheduled)).Status());
    }
}
