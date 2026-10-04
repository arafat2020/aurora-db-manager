using System.Net;
using System.Runtime.InteropServices;
using AuroraDbManager.Api.Infrastructure.Docker;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Integration;

/// <summary>
/// Monitoring against the real thing: a real Docker daemon behind the health checks, a real
/// PostgreSQL container behind the instance health, and real backups, made by the real
/// <c>pg_dump</c>, behind the metrics and the summary. Opt-in: see <see cref="DockerFactAttribute"/>.
/// Each test uses its own network and removes everything it created.
/// </summary>
/// <remarks>
/// Set up like <see cref="BackupIntegrationTests"/>, and run the same way, with
/// <c>tests/run-docker-integration-tests.sh</c>: the dump program connects to the container over
/// the Docker network.
/// </remarks>
[Trait("Category", "DockerIntegration")]
[Collection(BackupIntegrationCollection.Name)]
public sealed class MonitoringIntegrationTests : IAsyncLifetime
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable(DockerFactAttribute.EnableVariable) == "1";
    private static readonly bool InContainer = File.Exists("/.dockerenv");

    private readonly string _network = $"aurora-db-test-{Guid.NewGuid():N}";
    private readonly List<Guid> _instanceIds = [];
    private DockerEngine _engine = null!;
    private DockerClient _docker = null!;
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private MetricsRecorder _metrics = null!;

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        if (!InContainer && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new InvalidOperationException(
                "Instance containers publish no ports, and on Docker Desktop their network is not reachable from the host. "
                + "Run these tests with tests/run-docker-integration-tests.sh, which runs them in a container.");
        }

        var options = new DockerOptions { NetworkName = _network };
        _engine = new DockerEngine(Options.Create(options));
        _docker = DockerClientFactory.Create(options);

        await _engine.CreateNetworkAsync(_network, DockerResourceNaming.NetworkLabels(), default);
        if (InContainer)
        {
            await _docker.Networks.ConnectNetworkAsync(_network, new NetworkConnectParameters { Container = Environment.MachineName });
        }

        // No worker: each test runs the jobs itself, so nothing depends on timing.
        _factory = new ApiFactory { RealDockerNetwork = _network };
        _client = _factory.CreateClient();
        _metrics = _factory.RecordMetrics();
    }

    public async Task DisposeAsync()
    {
        if (!Enabled || _engine is null)
        {
            return;
        }

        _metrics?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();

        foreach (var instanceId in _instanceIds)
        {
            await _engine.RemoveContainerAsync(DockerResourceNaming.ContainerName(instanceId), default);
            await _engine.RemoveVolumeAsync(DockerResourceNaming.VolumeName(instanceId), default);
        }

        if (InContainer)
        {
            await _docker.Networks.DisconnectNetworkAsync(
                _network, new NetworkDisconnectParameters { Container = Environment.MachineName, Force = true });
        }

        await _docker.Networks.DeleteNetworkAsync(_network);
        _docker.Dispose();
        _engine.Dispose();
    }

    [DockerFact]
    public async Task Health_AgainstTheRealDockerDaemon_IsAliveAndReady()
    {
        var live = await (await _client.GetAsync(HealthUrl)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("healthy", live.Status());

        // The real engine's ping, over the real socket.
        var ready = await (await _client.GetAsync(ReadinessUrl)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("healthy", ready.Status());
        Assert.Equal("healthy", ready.GetProperty("checks").GetProperty("docker").GetString());
        Assert.Equal("healthy", ready.GetProperty("checks").GetProperty("metadataDatabase").GetString());

        // S3 is not configured here: not applicable, and not a failure.
        var storage = await (await _client.GetAsync(StorageHealthUrl)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("not_applicable", storage.GetProperty("checks").GetProperty("s3").GetString());
    }

    [DockerFact]
    public async Task InstanceHealth_FollowsTheRealContainer_AndNeverChangesTheInstance()
    {
        var (instanceId, _) = await CreateDatabaseAsync("shop");
        var containerName = DockerResourceNaming.ContainerName(instanceId);
        var before = (await _client.GetInstanceAsync(instanceId)).GetRawText();

        // Running, and the real pg_isready inside it says the database accepts connections.
        var healthy = await (await _client.GetAsync(InstanceHealthUrl(instanceId))).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("healthy", healthy.Status());
        Assert.True(healthy.GetProperty("container").GetProperty("running").GetBoolean());
        Assert.True(healthy.GetProperty("database").GetProperty("reachable").GetBoolean());

        // Stopped behind Aurora's back.
        await _docker.Containers.StopContainerAsync(containerName, new ContainerStopParameters { WaitBeforeKillSeconds = 5 });
        var stopped = await (await _client.GetAsync(InstanceHealthUrl(instanceId))).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("unhealthy", stopped.Status());
        Assert.Equal("container_not_running", stopped.GetProperty("reason").GetString());
        Assert.True(stopped.GetProperty("container").GetProperty("exists").GetBoolean());
        Assert.Equal("running", stopped.GetProperty("instanceStatus").GetString());

        // Observed, not repaired: the container is still stopped, and the record is untouched.
        Assert.Equal(DockerContainerState.Exited, (await _engine.FindContainerAsync(containerName, default))!.State);
        Assert.Equal(before, (await _client.GetInstanceAsync(instanceId)).GetRawText());

        // Gone altogether.
        await _engine.RemoveContainerAsync(containerName, default);
        var missing = await (await _client.GetAsync(InstanceHealthUrl(instanceId))).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("unhealthy", missing.Status());
        Assert.Equal("container_missing", missing.GetProperty("reason").GetString());
        Assert.Equal(before, (await _client.GetInstanceAsync(instanceId)).GetRawText());

        // The summary counts what is on record and asked Docker nothing: still one running instance.
        var summary = await (await _client.GetAsync(MonitoringSummaryUrl)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(1, summary.GetProperty("instances").GetProperty("running").GetInt32());
    }

    [DockerFact]
    public async Task RealBackup_IsMeasured_Listed_AndSummarized()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync("shop");

        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        var pending = await (await _client.GetAsync(MonitoringSummaryUrl)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(1, pending.GetProperty("backups").GetProperty("pending").GetInt32());
        Assert.Equal(1, _metrics.Observe("aurora_jobs_pending"));

        await _factory.ProcessJobAsync(jobId);

        var backup = await _client.GetBackupAsync(backupId);
        Assert.True(backup.Status() == "completed", backup.GetRawText());
        var path = _factory.BackupFilePath(instanceId, databaseId, backupId, "dump");

        // Measured where it happened: the real dump, by engine and storage, with its real size.
        var completed = Assert.Single(_metrics.Of("aurora_backups_completed_total"));
        Assert.Equal("postgres", completed.Tag("engine"));
        Assert.Equal("local", completed.Tag("storage_type"));
        Assert.Equal(1, _metrics.Total("aurora_backups_started_total"));
        Assert.Equal(0, _metrics.Total("aurora_backups_failed_total"));
        Assert.Equal(new FileInfo(path).Length, Assert.Single(_metrics.Of("aurora_backup_size_bytes")).Value);
        var duration = Assert.Single(_metrics.Of("aurora_backup_duration"));
        Assert.Equal("completed", duration.Tag("status"));
        Assert.True(duration.Value > 0);

        // The jobs behind it: provisioning, database creation and the backup, all completed.
        Assert.Equal(3, _metrics.Total("aurora_jobs_started_total"));
        Assert.Equal(3, _metrics.Total("aurora_jobs_completed_total"));
        Assert.Equal(1, _metrics.Total("aurora_jobs_completed_total", "job_type", "backup_database"));
        Assert.Equal(0, _metrics.Total("aurora_jobs_failed_total"));
        Assert.Equal(0, _metrics.Observe("aurora_jobs_pending"));
        Assert.Equal(0, _metrics.Observe("aurora_jobs_running"));

        // The job history and the summary say the same.
        var jobs = await (await _client.GetAsync($"{JobsUrl}?type=backup_database&status=completed")).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(jobId, Assert.Single(jobs.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        var summary = await (await _client.GetAsync(MonitoringSummaryUrl)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("healthy", summary.Status());
        Assert.Equal(1, summary.GetProperty("instances").GetProperty("running").GetInt32());
        Assert.Equal(0, summary.GetProperty("backups").GetProperty("pending").GetInt32());
        Assert.Equal(0, summary.GetProperty("backups").GetProperty("running").GetInt32());
        Assert.Equal(0, summary.GetProperty("backups").GetProperty("failedRecently").GetInt32());
        Assert.Empty(summary.GetProperty("recentFailures").EnumerateArray());

        // Nothing a metric is tagged with, and nothing logged, gives away the instance's password.
        var password = await _factory.AdminPasswordAsync(instanceId);
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(password, StringComparison.Ordinal));
        Assert.All(_metrics.All, measurement => Assert.All(measurement.Tags, tag => Assert.False(Guid.TryParse(tag.Value?.ToString(), out _))));
    }

    [DockerFact]
    public async Task RealBackupThatFails_ShowsUpInTheMetricsAndTheSummary_WithItsStableCode()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync("shop");
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);

        // The server goes away before the dump: every attempt fails.
        await _docker.Containers.StopContainerAsync(
            DockerResourceNaming.ContainerName(instanceId), new ContainerStopParameters { WaitBeforeKillSeconds = 5 });
        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        var code = job.GetProperty("error").GetProperty("code").GetString()!;
        Assert.StartsWith("BACKUP_", code);

        var failed = Assert.Single(_metrics.Of("aurora_backups_failed_total"));
        Assert.Equal(code, failed.Tag("error_code"));
        Assert.Equal("postgres", failed.Tag("engine"));
        Assert.Equal(1, _metrics.Total("aurora_jobs_failed_total", "job_type", "backup_database"));
        Assert.Equal(0, _metrics.Total("aurora_backups_completed_total"));

        var summary = await (await _client.GetAsync(MonitoringSummaryUrl)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("degraded", summary.Status());
        Assert.Equal(1, summary.GetProperty("backups").GetProperty("failedRecently").GetInt32());
        var failure = Assert.Single(summary.GetProperty("recentFailures").EnumerateArray());
        Assert.Equal(jobId, failure.GetProperty("jobId").GetGuid());
        Assert.Equal(backupId, failure.GetProperty("backupId").GetGuid());
        Assert.Equal(code, failure.GetProperty("errorCode").GetString());
    }

    [DockerFact]
    public async Task ScheduledBackup_FromTheSchedulerToTheCompletedBackup_IsVisibleInMonitoring()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync("shop");
        await (await _client.PostAsync(
            $"{DatabasesUrl}/{databaseId}/backup-schedule",
            System.Net.Http.Json.JsonContent.Create(new { cronExpression = "0 2 * * *", timeZoneId = "UTC" }))).ReadJsonAsync(HttpStatusCode.Created);

        // The clock here is the real one, so the stored occurrence is moved into the past: far
        // enough that, until a pass deals with it, the summary reports it as overdue.
        var due = DateTime.UtcNow.AddMinutes(-30);
        await _factory.WithDbAsync(db => db.BackupSchedules.ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRunAt, due)));

        var before = await (await _client.GetAsync(MonitoringSummaryUrl)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("degraded", before.Status());
        Assert.Equal(1, before.GetProperty("scheduler").GetProperty("enabledSchedules").GetInt32());
        Assert.Equal(1, before.GetProperty("scheduler").GetProperty("overdueSchedules").GetInt32());

        var passStarted = DateTimeOffset.UtcNow.AddSeconds(-1);
        var jobId = Assert.Single(await _factory.RunSchedulerAsync());

        // The scheduler: one pass, one backup triggered, and its heartbeat.
        Assert.Equal(1, _metrics.Total("aurora_scheduler_runs_total"));
        Assert.Equal(1, _metrics.Total("aurora_scheduled_backups_triggered_total"));
        Assert.Equal(0, _metrics.Total("aurora_scheduled_backups_skipped_total"));
        Assert.Equal(0, _metrics.Total("aurora_scheduler_failures_total"));
        Assert.Contains(_factory.Logs.Entries, entry =>
            entry.Contains("Scheduled backup triggered", StringComparison.Ordinal) && entry.Contains($"job {jobId}", StringComparison.Ordinal));

        var triggered = await (await _client.GetAsync(MonitoringSummaryUrl)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(0, triggered.GetProperty("scheduler").GetProperty("overdueSchedules").GetInt32());
        Assert.Equal(1, triggered.GetProperty("backups").GetProperty("pending").GetInt32());
        Assert.InRange(
            triggered.GetProperty("scheduler").GetProperty("lastSuccessfulPassAt").GetDateTimeOffset(), passStarted, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.True(triggered.GetProperty("scheduler").GetProperty("nextRunAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);

        // The backup job: the ordinary one, carried out by the real pg_dump.
        await _factory.ProcessJobAsync(jobId);
        var job = await _client.GetJobAsync(jobId);
        Assert.True(job.Status() == "completed", job.GetRawText());
        var backupId = job.GetProperty("backupId").GetGuid();
        Assert.Equal("completed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.True(File.Exists(_factory.BackupFilePath(instanceId, databaseId, backupId, "dump")));

        Assert.Equal(1, _metrics.Total("aurora_backups_completed_total", "engine", "postgres"));
        Assert.Equal(1, _metrics.Total("aurora_jobs_completed_total", "job_type", "backup_database"));

        // And the state afterwards: nothing pending, nothing failed, the latest backup completed.
        var after = await (await _client.GetAsync(MonitoringSummaryUrl)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("healthy", after.Status());
        Assert.Equal(0, after.GetProperty("backups").GetProperty("pending").GetInt32());
        Assert.Equal(0, after.GetProperty("jobs").GetProperty("failedRecently").GetInt32());
        var latest = await (await _client.GetAsync($"{DatabaseBackupsUrl(databaseId)}?status=completed&pageSize=1")).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(backupId, Assert.Single(latest.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        // A second pass finds nothing due.
        Assert.Empty(await _factory.RunSchedulerAsync());
        Assert.Equal(2, _metrics.Total("aurora_scheduler_runs_total"));
        Assert.Equal(1, _metrics.Total("aurora_scheduled_backups_triggered_total"));
    }

    private async Task<(Guid InstanceId, Guid DatabaseId)> CreateDatabaseAsync(string name)
    {
        var response = await _client.PostAsync(
            InstancesUrl,
            System.Net.Http.Json.JsonContent.Create(ValidInstanceRequest(engine: "postgres", version: "16", memoryMb: 512, storageGb: 1)));
        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        var instanceId = body.GetProperty("instance").GetProperty("id").GetGuid();
        _instanceIds.Add(instanceId);

        await _factory.ProcessJobAsync(body.GetProperty("job").GetProperty("id").GetGuid());
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
        return (instanceId, await _factory.CreateReadyDatabaseAsync(_client, instanceId, name));
    }
}
