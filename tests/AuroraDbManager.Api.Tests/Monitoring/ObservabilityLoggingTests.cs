using System.Net;
using System.Net.Http.Json;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Tests.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Monitoring;

/// <summary>
/// The operational log: the scheduler's events, the failures of jobs, backups and restores with
/// their stable codes, the request id that ties a request to its job, and, above all, what must
/// never be in it.
/// </summary>
public sealed class ObservabilityLoggingTests : IDisposable
{
    private const string AccessKey = "AKIALOGGINGTEST00001";
    private const string SecretKey = "logging-test-secret-5d2b8e4f";
    private static readonly DateTimeOffset Start = new(2026, 3, 10, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }
    }

    private (ApiFactory Factory, HttpClient Client) Application(bool runWorker = false, bool s3 = false, bool testClock = true)
    {
        var factory = new ApiFactory
        {
            Clock = testClock ? _clock : null,
            RunWorker = runWorker,
            ConfigureBackups = options =>
            {
                options.StorageType = s3 ? BackupStorageType.S3 : BackupStorageType.Local;
                options.S3.Bucket = FakeS3ObjectStore.Bucket;
                options.S3.Region = "us-east-1";
                options.S3.Endpoint = "https://s3.internal.example";
                options.S3.AccessKey = AccessKey;
                options.S3.SecretKey = SecretKey;
            }
        };
        var client = factory.CreateClient();
        _disposables.Add(factory);
        _disposables.Add(client);
        return (factory, client);
    }

    private static async Task<(Guid InstanceId, Guid DatabaseId)> ReadyDatabaseAsync(ApiFactory factory, HttpClient client)
    {
        var instanceId = await factory.CreateRunningInstanceAsync(client);
        return (instanceId, await factory.CreateReadyDatabaseAsync(client, instanceId, "app"));
    }

    private static async Task<Guid> ScheduleAsync(ApiFactory factory, HttpClient client, Guid databaseId)
    {
        await (await client.PostAsJsonAsync(
            $"{DatabasesUrl}/{databaseId}/backup-schedule", new { cronExpression = "0 2 * * *", timeZoneId = "UTC" }))
            .ReadJsonAsync(HttpStatusCode.Created);
        return await factory.WithDbAsync(db => db.BackupSchedules.Where(s => s.DatabaseId == databaseId).Select(s => s.Id).SingleAsync());
    }

    private void MoveClockToTheNextOccurrence() => _clock.SetUtcNow(new DateTimeOffset(2026, 3, 11, 2, 0, 0, TimeSpan.Zero));

    private static string Entry(ApiFactory factory, string containing) =>
        Assert.Single(factory.Logs.Entries, entry => entry.Contains(containing, StringComparison.Ordinal));

    // --- Scheduler ----------------------------------------------------------------------------

    [Fact]
    public async Task SchedulerPass_LogsItsCounts_AndTheTriggeredBackupWithItsIds()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ReadyDatabaseAsync(factory, client);
        var scheduleId = await ScheduleAsync(factory, client, databaseId);
        MoveClockToTheNextOccurrence();

        var jobId = Assert.Single(await factory.RunSchedulerAsync());

        var triggered = Entry(factory, "Scheduled backup triggered");
        Assert.Contains($"schedule {scheduleId}", triggered, StringComparison.Ordinal);
        Assert.Contains($"database {databaseId}", triggered, StringComparison.Ordinal);
        Assert.Contains($"job {jobId}", triggered, StringComparison.Ordinal);

        var pass = Entry(factory, "Scheduled backup scheduler pass completed");
        Assert.Contains("Information", pass, StringComparison.Ordinal);
        Assert.Contains("1 due, 1 triggered, 0 skipped, 0 failed", pass, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchedulerPass_WithNothingDue_IsLoggedBelowTheOrdinaryLevel()
    {
        var (factory, _) = Application();

        await factory.RunSchedulerAsync();

        // Written at debug level, which the application's default level leaves out: a pass every
        // few seconds that found nothing is not worth a line.
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains("scheduler pass completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SkippedOccurrence_IsLoggedWithItsIdsAndItsReason()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ReadyDatabaseAsync(factory, client);
        var scheduleId = await ScheduleAsync(factory, client, databaseId);
        await client.CreateBackupAsync(databaseId);
        MoveClockToTheNextOccurrence();

        Assert.Empty(await factory.RunSchedulerAsync());

        var skipped = Entry(factory, "Scheduled backup skipped");
        Assert.Contains("Warning", skipped, StringComparison.Ordinal);
        Assert.Contains($"schedule {scheduleId}", skipped, StringComparison.Ordinal);
        Assert.Contains($"database {databaseId}", skipped, StringComparison.Ordinal);
        Assert.Contains("reason backup_in_progress", skipped, StringComparison.Ordinal);
        Assert.Contains("1 due, 0 triggered, 1 skipped, 0 failed", Entry(factory, "Scheduled backup scheduler pass completed"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedSchedulerPass_IsLoggedAsAnError_WithAClassification()
    {
        var (factory, _) = Application();
        await factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE backup_schedules"));

        await Assert.ThrowsAnyAsync<Exception>(() => factory.RunSchedulerAsync());

        var failed = Entry(factory, "Scheduled backup scheduler pass failed");
        Assert.Contains("Error", failed, StringComparison.Ordinal);
        Assert.Contains("SqliteException", failed, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains("scheduler pass completed", StringComparison.Ordinal));
    }

    // --- Failures -----------------------------------------------------------------------------

    [Fact]
    public async Task FailedJob_IsLoggedWithItsIdTypeAndStableErrorCode()
    {
        var (factory, client) = Application();
        var (_, jobId) = await client.CreateInstanceAsync();
        factory.Provisioner.FailAllCalls();

        await factory.ProcessJobAsync(jobId);

        var failed = Entry(factory, "failed after 3 attempts");
        Assert.Contains("Error", failed, StringComparison.Ordinal);
        Assert.Contains($"Job {jobId} (ProvisionInstance)", failed, StringComparison.Ordinal);
        Assert.Contains("PROVISIONING_FAILED", failed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedBackup_IsLoggedWithItsIdsAndStableErrorCode()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ReadyDatabaseAsync(factory, client);
        var (backupId, jobId) = await client.CreateBackupAsync(databaseId);
        factory.DumpTools.FailAllRuns();

        await factory.ProcessJobAsync(jobId);

        var failed = Entry(factory, $"Backup {backupId} of database {databaseId} failed");
        Assert.Contains("BACKUP_PROCESS_FAILED", failed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRestore_IsLoggedWithItsIdsAndStableErrorCode()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ReadyDatabaseAsync(factory, client);
        var backupId = await factory.CreateCompletedBackupAsync(client, databaseId);
        var jobId = await ApiFactory.RequestRestoreAsync(client, backupId);
        factory.DumpTools.FailAllRuns();

        await factory.ProcessJobAsync(jobId);

        var code = (await client.GetJobAsync(jobId)).GetProperty("error").GetProperty("code").GetString()!;
        var failed = Entry(factory, $"Restore of backup {backupId} into database {databaseId} failed");
        Assert.Contains(code, failed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EverythingAJobLogs_IsInTheScopeOfThatJob()
    {
        var (factory, client) = Application();
        var (_, jobId) = await client.CreateInstanceAsync();

        await factory.ProcessJobAsync(jobId);

        Assert.Contains(factory.Logs.Entries, entry =>
            entry.Contains("JobProcessor scope: ", StringComparison.Ordinal)
            && entry.Contains($"Job {jobId} (ProvisionInstance)", StringComparison.Ordinal));
    }

    // --- Request correlation ------------------------------------------------------------------

    [Fact]
    public async Task Request_WithoutAnId_GetsOne_ReturnedInTheResponseHeader()
    {
        var (_, client) = Application();

        var first = await client.GetAsync(InstancesUrl);
        var second = await client.GetAsync(InstancesUrl);

        var firstId = Assert.Single(first.Headers.GetValues("X-Request-Id"));
        var secondId = Assert.Single(second.Headers.GetValues("X-Request-Id"));
        Assert.Matches("^[0-9a-f]{32}$", firstId);
        Assert.NotEqual(firstId, secondId);
    }

    [Fact]
    public async Task Request_WithAUsableId_KeepsIt_InTheResponseAndInTheLog()
    {
        var (factory, client) = Application();
        using var request = new HttpRequestMessage(HttpMethod.Get, InstancesUrl);
        request.Headers.Add("X-Request-Id", "deploy-42.check_1");

        var response = await client.SendAsync(request);

        Assert.Equal("deploy-42.check_1", Assert.Single(response.Headers.GetValues("X-Request-Id")));
        Assert.Contains(factory.Logs.Entries, entry => entry.EndsWith("scope: Request deploy-42.check_1", StringComparison.Ordinal));
        var completed = Entry(factory, "HTTP GET /api/v1/instances responded 200");
        Assert.Contains("Information", completed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("has spaces in it")]
    [InlineData("line\\nbreak{injected}")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Request_WithAnUnusableId_GetsAGeneratedOne_AndTheClientsIsNeverLogged(string supplied)
    {
        var (factory, client) = Application();
        using var request = new HttpRequestMessage(HttpMethod.Get, InstancesUrl);
        request.Headers.TryAddWithoutValidation("X-Request-Id", supplied);

        var response = await client.SendAsync(request);

        Assert.Matches("^[0-9a-f]{32}$", Assert.Single(response.Headers.GetValues("X-Request-Id")));
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(supplied, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ErrorResponses_CarryTheRequestIdToo()
    {
        var (_, client) = Application();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{InstancesUrl}/{Guid.NewGuid()}");
        request.Headers.Add("X-Request-Id", "lookup-7");

        var notFound = await client.SendAsync(request);
        var unknownRoute = await client.GetAsync("/api/v1/nothing-here");

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("lookup-7", Assert.Single(notFound.Headers.GetValues("X-Request-Id")));
        Assert.Equal(HttpStatusCode.NotFound, unknownRoute.StatusCode);
        Assert.Single(unknownRoute.Headers.GetValues("X-Request-Id"));
    }

    [Fact]
    public async Task HealthProbes_GetAnIdToo_ButStayOutOfTheOrdinaryLog()
    {
        var (factory, client) = Application();

        var response = await client.GetAsync(HealthUrl);

        Assert.Single(response.Headers.GetValues("X-Request-Id"));
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains("HTTP GET /health", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RequestLog_NeverContainsTheQueryString()
    {
        var (factory, client) = Application();

        await client.GetAsync($"{JobsUrl}?status=failed&pageSize=5");

        Assert.Contains(factory.Logs.Entries, entry => entry.Contains("HTTP GET /api/v1/jobs responded 200", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains("pageSize=5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JobCreatedByARequest_IsProcessedInTheBackground_UnderThatRequestsId()
    {
        // The real worker and the real clock: the job goes through the queue as it does in production.
        var (factory, client) = Application(runWorker: true, testClock: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, InstancesUrl)
        {
            Content = JsonContent.Create(ValidInstanceRequest())
        };
        request.Headers.Add("X-Request-Id", "create-instance-0001");

        var body = await (await client.SendAsync(request)).ReadJsonAsync(HttpStatusCode.Accepted);
        var jobId = body.GetProperty("job").GetProperty("id").GetGuid();
        Assert.Equal("completed", (await client.WaitForFinishedJobAsync(jobId)).Status());

        // The request logged the job's creation under its id...
        var entries = factory.Logs.Entries;
        Assert.Contains(entries, entry => entry.Contains("RequestCorrelationMiddleware scope: Request create-instance-0001", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Contains($"Job {jobId} (ProvisionInstance)", StringComparison.Ordinal) && entry.Contains("queued", StringComparison.Ordinal));
        // ...and the worker took the same id up before it ran the job.
        var workerScope = entries.ToList().FindIndex(entry => entry.Contains("JobWorker scope: Request create-instance-0001", StringComparison.Ordinal));
        var jobStarted = entries.ToList().FindIndex(entry => entry.Contains($"Job {jobId} (ProvisionInstance)", StringComparison.Ordinal) && entry.Contains("started", StringComparison.Ordinal));
        Assert.True(workerScope >= 0, "The worker did not open a scope with the request id.");
        Assert.True(jobStarted > workerScope, "The job started before the worker's scope was opened.");
    }

    // --- Sensitive data -----------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NothingLogged_AndNoMonitoringResponse_EverContainsACredential(bool s3)
    {
        var (factory, client) = Application(s3: s3);
        var (instanceId, databaseId) = await ReadyDatabaseAsync(factory, client);
        var password = await factory.AdminPasswordAsync(instanceId);
        await ScheduleAsync(factory, client, databaseId);

        // Successes: a backup, a restore, a scheduled backup.
        var backupId = await factory.CreateCompletedBackupAsync(client, databaseId);
        await factory.ProcessJobAsync(await ApiFactory.RequestRestoreAsync(client, backupId));
        MoveClockToTheNextOccurrence();
        foreach (var jobId in await factory.RunSchedulerAsync())
        {
            await factory.ProcessJobAsync(jobId);
        }

        // Failures: of the dump program, of the storage, of the restore, of Docker.
        factory.DumpTools.FailAllRuns("raw-tool-detail: something went wrong");
        var (_, failingBackup) = await client.CreateBackupAsync(databaseId);
        await factory.ProcessJobAsync(failingBackup);
        await factory.ProcessJobAsync(await ApiFactory.RequestRestoreAsync(client, backupId));
        factory.DumpTools.ClearScript();
        factory.ObjectStore.Unavailable = true;
        factory.Docker.Unavailable = true;
        var (_, storageFailure) = await client.CreateBackupAsync(databaseId);
        await factory.ProcessJobAsync(storageFailure);

        // Everything monitoring exposes.
        var responses = new List<string>();
        foreach (var url in new[]
                 {
                     HealthUrl, ReadinessUrl, StorageHealthUrl, MonitoringSummaryUrl, JobsUrl, $"{JobsUrl}?status=failed",
                     InstanceHealthUrl(instanceId), DatabaseBackupsUrl(databaseId)
                 })
        {
            responses.Add(await (await client.GetAsync(url)).Content.ReadAsStringAsync());
        }

        string[] secrets = s3 ? [password, SecretKey, AccessKey] : [password];
        Assert.NotEmpty(factory.Logs.Entries);
        Assert.All(secrets, secret =>
        {
            Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(responses, response => response.Contains(secret, StringComparison.Ordinal));
        });

        // Nor what is around a credential: connection strings and the contents of credential files.
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains("Password=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains("PGPASSWORD", StringComparison.Ordinal));
        Assert.All(responses, response =>
        {
            Assert.DoesNotContain("raw-tool-detail", response, StringComparison.Ordinal);
            Assert.DoesNotContain("raw-sdk-detail", response, StringComparison.Ordinal);
            Assert.DoesNotContain("raw-daemon-detail", response, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", response, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeInstanceEndpoints.Host, response, StringComparison.Ordinal);
        });
    }
}
