using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.Extensions.Time.Testing;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Monitoring;

/// <summary>
/// <c>GET /api/v1/monitoring/summary</c>: a snapshot counted from the system database. The clock
/// is a test clock, so "recently" and "overdue" are decided by the tests.
/// </summary>
public sealed class MonitoringSummaryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 3, 10, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public MonitoringSummaryTests()
    {
        _factory = new ApiFactory { Clock = _clock };
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<JsonElement> SummaryAsync() =>
        await (await _client.GetAsync(MonitoringSummaryUrl)).ReadJsonAsync(HttpStatusCode.OK);

    private static int Count(JsonElement summary, string section, string name) =>
        summary.GetProperty(section).GetProperty(name).GetInt32();

    private async Task<Guid> ReadyDatabaseAsync(string name = "app")
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client, name: $"instance-{name}");
        return await _factory.CreateReadyDatabaseAsync(_client, instanceId, name);
    }

    private async Task ScheduleAsync(Guid databaseId, string cron = "0 2 * * *") =>
        await (await _client.PostAsJsonAsync(
            $"{DatabasesUrl}/{databaseId}/backup-schedule", new { cronExpression = cron, timeZoneId = "UTC" }))
            .ReadJsonAsync(HttpStatusCode.Created);

    private async Task<Guid> FailedProvisioningAsync(string name)
    {
        var (_, jobId) = await _client.CreateInstanceAsync(name: name);
        _factory.Provisioner.FailAllCalls();
        await _factory.ProcessJobAsync(jobId);
        _factory.Provisioner.FailNextCalls(0);
        return jobId;
    }

    [Fact]
    public async Task EmptySystem_IsHealthy_WithZeroesEverywhere()
    {
        var summary = await SummaryAsync();

        Assert.Equal("healthy", summary.Status());
        Assert.Equal(
            ["backups", "generatedAt", "instances", "jobs", "recentFailures", "recentWindowHours", "restores", "scheduler", "status"],
            summary.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(Start, summary.GetProperty("generatedAt").GetDateTimeOffset());
        Assert.Equal(24, summary.GetProperty("recentWindowHours").GetInt32());
        Assert.Equal(0, Count(summary, "instances", "total"));
        Assert.Equal(0, Count(summary, "jobs", "pending"));
        Assert.Equal(0, Count(summary, "jobs", "running"));
        Assert.Equal(0, Count(summary, "jobs", "failedRecently"));
        Assert.Equal(0, Count(summary, "scheduler", "enabledSchedules"));
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("scheduler").GetProperty("nextRunAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("scheduler").GetProperty("lastSuccessfulPassAt").ValueKind);
        Assert.Empty(summary.GetProperty("recentFailures").EnumerateArray());
    }

    [Fact]
    public async Task RepresentativeSystem_IsCounted_ByInstancesJobsBackupsRestoresAndSchedules()
    {
        // Two running instances with a database each, one instance still provisioning, one failed.
        var databaseA = await ReadyDatabaseAsync("a");
        var databaseB = await ReadyDatabaseAsync("b");
        await _client.CreateInstanceAsync(name: "still-provisioning");
        var failedProvisioning = await FailedProvisioningAsync("broken");

        // A completed backup, a failed one, one waiting, and a restore waiting.
        var completedBackup = await _factory.CreateCompletedBackupAsync(_client, databaseA);
        var (_, failedBackupJob) = await _client.CreateBackupAsync(databaseA);
        _factory.DumpTools.FailAllRuns();
        await _factory.ProcessJobAsync(failedBackupJob);
        _factory.DumpTools.ClearScript();
        await _client.CreateBackupAsync(databaseB);
        var restoreJob = await ApiFactory.RequestRestoreAsync(_client, completedBackup);

        // Two schedules, one of them disabled.
        await ScheduleAsync(databaseA, "0 2 * * *");
        await ScheduleAsync(databaseB, "0 4 * * *");
        await (await _client.PutAsJsonAsync(
            $"{DatabasesUrl}/{databaseB}/backup-schedule", new { cronExpression = "0 4 * * *", timeZoneId = "UTC", enabled = false }))
            .ReadJsonAsync(HttpStatusCode.OK);

        var summary = await SummaryAsync();

        Assert.Equal("degraded", summary.Status());

        Assert.Equal(4, Count(summary, "instances", "total"));
        Assert.Equal(2, Count(summary, "instances", "running"));
        Assert.Equal(1, Count(summary, "instances", "provisioning"));
        Assert.Equal(1, Count(summary, "instances", "failed"));
        Assert.Equal(0, Count(summary, "instances", "stopped"));

        // Pending: the provisioning, the waiting backup and the restore.
        Assert.Equal(3, Count(summary, "jobs", "pending"));
        Assert.Equal(0, Count(summary, "jobs", "running"));
        Assert.Equal(2, Count(summary, "jobs", "failedRecently"));

        Assert.Equal(1, Count(summary, "backups", "pending"));
        Assert.Equal(0, Count(summary, "backups", "running"));
        Assert.Equal(1, Count(summary, "backups", "failedRecently"));

        Assert.Equal(1, Count(summary, "restores", "pending"));
        Assert.Equal(0, Count(summary, "restores", "failedRecently"));

        var scheduler = summary.GetProperty("scheduler");
        Assert.Equal(1, scheduler.GetProperty("enabledSchedules").GetInt32());
        Assert.Equal(0, scheduler.GetProperty("overdueSchedules").GetInt32());
        Assert.Equal(new DateTimeOffset(2026, 3, 11, 2, 0, 0, TimeSpan.Zero), scheduler.GetProperty("nextRunAt").GetDateTimeOffset());

        // Newest first, each with its stable code and nothing more than ids, type and time.
        var failures = summary.GetProperty("recentFailures").EnumerateArray().ToList();
        Assert.Equal([failedBackupJob, failedProvisioning], failures.Select(failure => failure.GetProperty("jobId").GetGuid()));
        Assert.Equal("backup_database", failures[0].GetProperty("type").GetString());
        Assert.Equal("BACKUP_PROCESS_FAILED", failures[0].GetProperty("errorCode").GetString());
        Assert.Equal(databaseA, failures[0].GetProperty("databaseId").GetGuid());
        Assert.Equal("PROVISIONING_FAILED", failures[1].GetProperty("errorCode").GetString());
        Assert.False(failures[1].TryGetProperty("databaseId", out _));
        Assert.Equal(Start, failures[0].GetProperty("failedAt").GetDateTimeOffset());
        Assert.Equal("pending", (await _client.GetJobAsync(restoreJob)).Status());
    }

    [Fact]
    public async Task RunningJobs_AreCountedAsRunning_ByTheirType()
    {
        var databaseId = await ReadyDatabaseAsync();
        var (_, backupJob) = await _client.CreateBackupAsync(databaseId);
        await _factory.SimulateAbandonedExecutionAsync(backupJob, DateTime.UtcNow.AddMinutes(5));

        var summary = await SummaryAsync();

        Assert.Equal(1, Count(summary, "jobs", "running"));
        Assert.Equal(1, Count(summary, "backups", "running"));
        Assert.Equal(0, Count(summary, "backups", "pending"));
        Assert.Equal(0, Count(summary, "restores", "running"));
        // Work in progress is not a problem.
        Assert.Equal("healthy", summary.Status());
    }

    [Fact]
    public async Task RecentFailures_AreBounded_HoweverManyJobsFailed()
    {
        var failed = new List<Guid>();
        for (var i = 0; i < 13; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            failed.Add(await FailedProvisioningAsync($"broken-{i}"));
        }

        var summary = await SummaryAsync();

        Assert.Equal(13, Count(summary, "jobs", "failedRecently"));
        var listed = summary.GetProperty("recentFailures").EnumerateArray().Select(failure => failure.GetProperty("jobId").GetGuid()).ToList();
        Assert.Equal(10, listed.Count);
        Assert.Equal(Enumerable.Reverse(failed).Take(10), listed);
    }

    [Fact]
    public async Task FailuresOlderThanTheRecentWindow_AreNoLongerCounted_OrListed()
    {
        var databaseId = await ReadyDatabaseAsync();
        var (_, jobId) = await _client.CreateBackupAsync(databaseId);
        _factory.DumpTools.FailAllRuns();
        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("degraded", (await SummaryAsync()).Status());

        _clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(1, Count(await SummaryAsync(), "jobs", "failedRecently"));

        _clock.Advance(TimeSpan.FromHours(2));
        var later = await SummaryAsync();
        Assert.Equal(0, Count(later, "jobs", "failedRecently"));
        Assert.Equal(0, Count(later, "backups", "failedRecently"));
        Assert.Empty(later.GetProperty("recentFailures").EnumerateArray());
        Assert.Equal("healthy", later.Status());
        // The job itself is still there to be looked up.
        Assert.Equal("failed", (await _client.GetJobAsync(jobId)).Status());
    }

    [Fact]
    public async Task Scheduler_ReportsItsLastSuccessfulPass_AndSchedulesItHasNotPickedUp()
    {
        await ScheduleAsync(await ReadyDatabaseAsync());

        // Due, but only just: the next pass is expected to pick it up.
        _clock.SetUtcNow(new DateTimeOffset(2026, 3, 11, 2, 0, 30, TimeSpan.Zero));
        var justDue = await SummaryAsync();
        Assert.Equal(0, Count(justDue, "scheduler", "overdueSchedules"));
        Assert.Equal("healthy", justDue.Status());

        // Minutes later no pass has dealt with it: the scheduler is not doing its work.
        _clock.Advance(TimeSpan.FromMinutes(10));
        var overdue = await SummaryAsync();
        Assert.Equal(1, Count(overdue, "scheduler", "overdueSchedules"));
        Assert.Equal("degraded", overdue.Status());
        Assert.Equal(JsonValueKind.Null, overdue.GetProperty("scheduler").GetProperty("lastSuccessfulPassAt").ValueKind);

        // A pass deals with it and leaves its mark.
        Assert.Single(await _factory.RunSchedulerAsync());
        var after = await SummaryAsync();
        Assert.Equal(0, Count(after, "scheduler", "overdueSchedules"));
        Assert.Equal(_clock.GetUtcNow(), after.GetProperty("scheduler").GetProperty("lastSuccessfulPassAt").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2026, 3, 12, 2, 0, 0, TimeSpan.Zero), after.GetProperty("scheduler").GetProperty("nextRunAt").GetDateTimeOffset());
        Assert.Equal("healthy", after.Status());
    }

    [Fact]
    public async Task SchedulerHeartbeat_IsNotMovedByAPassThatFails()
    {
        await _factory.RunSchedulerAsync();
        var before = (await SummaryAsync()).GetProperty("scheduler").GetProperty("lastSuccessfulPassAt").GetDateTimeOffset();
        Assert.Equal(Start, before);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await _factory.RunSchedulerAsync();
        Assert.Equal(Start.AddMinutes(1), (await SummaryAsync()).GetProperty("scheduler").GetProperty("lastSuccessfulPassAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Summary_ContainsNoSecrets_NoErrorMessages_AndNoInternals()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var password = await _factory.AdminPasswordAsync(instanceId);
        var (_, jobId) = await _client.CreateBackupAsync(databaseId);
        _factory.DumpTools.FailAllRuns("pg_dump: error: raw-tool-detail at /var/lib/secret/path");
        await _factory.ProcessJobAsync(jobId);
        var message = (await _client.GetJobAsync(jobId)).GetProperty("error").GetProperty("message").GetString()!;

        var response = await _client.GetAsync(MonitoringSummaryUrl);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("BACKUP_PROCESS_FAILED", text, StringComparison.Ordinal);
        Assert.DoesNotContain(message, text, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-tool-detail", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/var/lib/secret/path", text, StringComparison.Ordinal);
        Assert.DoesNotContain(password, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
        Assert.DoesNotContain(_factory.BackupRoot, text, StringComparison.Ordinal);
        Assert.DoesNotContain("message", text, StringComparison.OrdinalIgnoreCase);

        var failure = Assert.Single((await SummaryAsync()).GetProperty("recentFailures").EnumerateArray());
        Assert.Equal(
            ["backupId", "databaseId", "errorCode", "failedAt", "instanceId", "jobId", "type"],
            failure.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task Summary_InspectsNoInstance_AndAsksNeitherDockerNorTheBackupStorage()
    {
        for (var i = 0; i < 3; i++)
        {
            var databaseId = await ReadyDatabaseAsync($"db{i}");
            await _factory.CreateCompletedBackupAsync(_client, databaseId);
        }

        _factory.Docker.Calls.Clear();
        var dumpRuns = _factory.DumpTools.RunCount;

        var summary = await SummaryAsync();

        Assert.Equal(3, Count(summary, "instances", "running"));
        Assert.Empty(_factory.Docker.Calls);
        Assert.Equal(dumpRuns, _factory.DumpTools.RunCount);
        Assert.Equal(0, _factory.ObjectStore.UploadCount);
        Assert.Equal(0, _factory.ObjectStore.Reads);
    }

    [Fact]
    public async Task Summary_StoppedInstance_IsCounted_AndIsNotAProblemByItself()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        await _factory.SetInstanceStatusAsync(instanceId, InstanceStatus.Stopped);

        var summary = await SummaryAsync();

        Assert.Equal(1, Count(summary, "instances", "stopped"));
        Assert.Equal(0, Count(summary, "instances", "running"));
        Assert.Equal("healthy", summary.Status());
    }
}
