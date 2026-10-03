using System.Data.Common;
using System.Net;
using System.Text.Json;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Application.Restores;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Restores;

/// <summary>
/// The restore endpoint. The worker is off: a requested restore stays pending until a test runs
/// its job, so what a request alone does, and does not do, can be observed.
/// </summary>
public sealed class RestoreApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public RestoreApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static string RestoreUrl(Guid backupId) => $"{BackupsUrl}/{backupId}/restore";

    private Task<HttpResponseMessage> PostRestoreAsync(Guid backupId) => _client.PostAsync(RestoreUrl(backupId), content: null);

    private async Task<(Guid InstanceId, Guid DatabaseId, Guid BackupId)> CreateBackedUpDatabaseAsync(string name = "app")
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client, name: $"instance-{name}");
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, name);
        return (instanceId, databaseId, await _factory.CreateCompletedBackupAsync(_client, databaseId));
    }

    private Task<int> RestoreJobCountAsync() =>
        _factory.WithDbAsync(db => db.Jobs.CountAsync(j => j.Type == JobType.RestoreDatabase));

    // --- Accepting ----------------------------------------------------------------------------

    [Fact]
    public async Task Restore_CompletedBackup_ReturnsAcceptedWithItsOwnDatabaseAndAPendingJob()
    {
        var (instanceId, databaseId, backupId) = await CreateBackedUpDatabaseAsync();

        var response = await PostRestoreAsync(backupId);

        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        Assert.Equal(["database", "job"], body.EnumerateObject().Select(property => property.Name).Order());

        // The database that will be overwritten: the one the backup was made of.
        var database = body.GetProperty("database");
        Assert.Equal(databaseId, database.GetProperty("id").GetGuid());
        Assert.Equal("app", database.GetProperty("name").GetString());
        Assert.Equal("ready", database.Status());

        var job = body.GetProperty("job");
        var jobId = job.GetProperty("id").GetGuid();
        Assert.Equal("restore_database", job.GetProperty("type").GetString());
        Assert.Equal("pending", job.Status());
        Assert.Equal(instanceId, job.GetProperty("instanceId").GetGuid());
        Assert.Equal(databaseId, job.GetProperty("databaseId").GetGuid());
        Assert.Equal(backupId, job.GetProperty("backupId").GetGuid());
        Assert.Equal(0, job.GetProperty("attempt").GetInt32());

        // The job is what a client polls.
        Assert.NotNull(response.Headers.Location);
        Assert.Equal($"{JobsUrl}/{jobId}", response.Headers.Location.AbsolutePath);
        Assert.Equal("pending", (await _client.GetJobAsync(jobId)).Status());
    }

    [Fact]
    public async Task Restore_Request_OnlyStoresTheJob_NothingIsFetchedEmptiedOrRun()
    {
        var (_, databaseId, backupId) = await CreateBackedUpDatabaseAsync();
        var runsBefore = _factory.DumpTools.RunCount;

        await ApiFactory.RequestRestoreAsync(_client, backupId);

        var job = Assert.Single(await _factory.WithDbAsync(db => db.Jobs.AsNoTracking().Where(j => j.Type == JobType.RestoreDatabase).ToListAsync()));
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(databaseId, job.DatabaseId);
        Assert.Equal(backupId, job.BackupId);

        Assert.Equal(runsBefore, _factory.DumpTools.RunCount);
        Assert.Empty(_factory.RestoreSql.Statements);
        Assert.Empty(_factory.RestoreStagingEntries());
        // No second status system: the database stays ready and the backup stays as it was.
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal("completed", (await _client.GetBackupAsync(backupId)).Status());
    }

    [Fact]
    public async Task Restore_TargetIsAlwaysTheBackupsOwnDatabase_WhateverTheClientSends()
    {
        var (_, databaseId, backupId) = await CreateBackedUpDatabaseAsync();
        var (_, otherDatabaseId, _) = await CreateBackedUpDatabaseAsync("other");

        var response = await _client.PostAsync(
            RestoreUrl(backupId),
            System.Net.Http.Json.JsonContent.Create(new
            {
                databaseId = otherDatabaseId,
                targetDatabaseId = otherDatabaseId,
                database = "postgres",
                instanceId = Guid.NewGuid(),
                path = "/etc/passwd",
                key = "backups/someone-elses.dump"
            }));

        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        Assert.Equal(databaseId, body.GetProperty("database").GetProperty("id").GetGuid());
        Assert.Equal(databaseId, body.GetProperty("job").GetProperty("databaseId").GetGuid());
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Jobs.CountAsync(j => j.Type == JobType.RestoreDatabase && j.DatabaseId == otherDatabaseId)));
    }

    [Fact]
    public async Task Restore_DatabasesOfDifferentBackups_CanBeRestoredAtTheSameTime()
    {
        var (_, _, first) = await CreateBackedUpDatabaseAsync("first");
        var (_, _, second) = await CreateBackedUpDatabaseAsync("second");

        await ApiFactory.RequestRestoreAsync(_client, first);
        await ApiFactory.RequestRestoreAsync(_client, second);

        Assert.Equal(2, await RestoreJobCountAsync());
    }

    // --- Rejecting ----------------------------------------------------------------------------

    [Fact]
    public async Task Restore_UnknownBackup_ReturnsBackupNotFound()
    {
        var response = await PostRestoreAsync(Guid.NewGuid());

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "BACKUP_NOT_FOUND");
        Assert.Equal(0, await RestoreJobCountAsync());
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    public async Task Restore_MalformedBackupId_ReturnsNotFoundInErrorFormat(string backupId)
    {
        var response = await _client.PostAsync($"{BackupsUrl}/{backupId}/restore", content: null);

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Theory]
    [InlineData(BackupStatus.Pending)]
    [InlineData(BackupStatus.Running)]
    [InlineData(BackupStatus.Failed)]
    public async Task Restore_BackupThatIsNotCompleted_IsRejected(BackupStatus status)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var backupId = await _factory.WithDbAsync(async db =>
        {
            var backup = Backup.Create(databaseId, BackupStorageType.Local, DateTime.UtcNow);
            if (status != BackupStatus.Pending)
            {
                backup.MarkRunning();
            }

            if (status == BackupStatus.Failed)
            {
                backup.MarkFailed("BACKUP_PROCESS_FAILED", "The backup failed.", DateTime.UtcNow);
            }

            db.Backups.Add(backup);
            await db.SaveChangesAsync();
            return backup.Id;
        });

        var response = await PostRestoreAsync(backupId);

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "BACKUP_NOT_COMPLETED");
        Assert.Equal(0, await RestoreJobCountAsync());
    }

    [Fact]
    public async Task Restore_BackupInAStorageTheServerIsNotConfiguredWith_IsRejected()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        // Stored in S3 when the server used S3; the server now stores backups locally.
        var backupId = await _factory.WithDbAsync(async db =>
        {
            var backup = Backup.Create(databaseId, BackupStorageType.S3, DateTime.UtcNow);
            backup.MarkRunning();
            backup.MarkCompleted("backups/instances/a/databases/b/c.dump", 100, DateTime.UtcNow);
            db.Backups.Add(backup);
            await db.SaveChangesAsync();
            return backup.Id;
        });

        var response = await PostRestoreAsync(backupId);

        var error = await response.AssertErrorAsync(HttpStatusCode.Conflict, "BACKUP_STORAGE_NOT_CONFIGURED");
        Assert.DoesNotContain("backups/instances", error.GetRawText());
        Assert.Equal(0, await RestoreJobCountAsync());
    }

    [Fact]
    public async Task Restore_DatabaseBeingDeleted_IsRejected()
    {
        var (_, databaseId, backupId) = await CreateBackedUpDatabaseAsync();
        await _client.DeleteDatabaseAsync(databaseId);

        var response = await PostRestoreAsync(backupId);

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_NOT_READY");
        Assert.Equal(0, await RestoreJobCountAsync());
    }

    [Fact]
    public async Task Restore_FailedDatabase_IsRejected()
    {
        var (_, databaseId, backupId) = await CreateBackedUpDatabaseAsync();
        var deleteJobId = await _client.DeleteDatabaseAsync(databaseId);
        _factory.DatabaseServers.FailAllCalls();
        await _factory.ProcessJobAsync(deleteJobId);
        Assert.Equal("failed", (await _client.GetDatabaseAsync(databaseId)).Status());

        var response = await PostRestoreAsync(backupId);

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_NOT_READY");
    }

    [Theory]
    [InlineData(InstanceStatus.Stopped)]
    [InlineData(InstanceStatus.Failed)]
    public async Task Restore_InstanceNotRunning_IsRejectedAndNotStarted(InstanceStatus status)
    {
        var (instanceId, _, backupId) = await CreateBackedUpDatabaseAsync();
        await _factory.SetInstanceStatusAsync(instanceId, status);

        var response = await PostRestoreAsync(backupId);

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "INSTANCE_NOT_READY");
        Assert.Equal(0, await RestoreJobCountAsync());
        Assert.Empty(_factory.Provisioner.EnsureRunningInstanceIds);
    }

    // --- One operation at a time --------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_WhileTheDatabaseIsAlreadyBeingRestored_IsRejected_EvenFromAnotherBackup(bool firstIsRunning)
    {
        var (_, databaseId, backupId) = await CreateBackedUpDatabaseAsync();
        var otherBackupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var firstJobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        if (firstIsRunning)
        {
            await _factory.SimulateAbandonedExecutionAsync(firstJobId, DateTime.UtcNow.AddHours(1));
        }

        foreach (var id in new[] { backupId, otherBackupId })
        {
            await (await PostRestoreAsync(id)).AssertErrorAsync(HttpStatusCode.Conflict, "RESTORE_OPERATION_IN_PROGRESS");
        }

        Assert.Equal(1, await RestoreJobCountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_AfterTheEarlierRestoreFinished_IsAccepted_WhetherItCompletedOrFailed(bool earlierFailed)
    {
        var (_, _, backupId) = await CreateBackedUpDatabaseAsync();
        var firstJobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        if (earlierFailed)
        {
            _factory.DumpTools.FailNext(FakeDumpToolsKinds.PgRestore, 3);
        }

        await _factory.ProcessJobAsync(firstJobId);
        Assert.Equal(earlierFailed ? "failed" : "completed", (await _client.GetJobAsync(firstJobId)).Status());

        var secondJobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await _factory.ProcessJobAsync(secondJobId);

        Assert.Equal("completed", (await _client.GetJobAsync(secondJobId)).Status());
        Assert.Equal(2, await RestoreJobCountAsync());
    }

    [Fact]
    public async Task Restore_WhileTheDatabaseIsBeingBackedUp_IsRejected()
    {
        var (_, databaseId, backupId) = await CreateBackedUpDatabaseAsync();
        await _client.CreateBackupAsync(databaseId);

        var response = await PostRestoreAsync(backupId);

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "BACKUP_OPERATION_IN_PROGRESS");
        Assert.Equal(0, await RestoreJobCountAsync());
    }

    [Fact]
    public async Task WhileBeingRestored_TheDatabaseCannotBeBackedUpOrDeleted_NorItsInstance_UntilTheRestoreHasFinished()
    {
        var (instanceId, databaseId, backupId) = await CreateBackedUpDatabaseAsync();
        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);

        await (await _client.PostAsync(DatabaseBackupsUrl(databaseId), content: null)).AssertErrorAsync(HttpStatusCode.Conflict, "RESTORE_OPERATION_IN_PROGRESS");
        await (await _client.DeleteAsync($"{DatabasesUrl}/{databaseId}")).AssertErrorAsync(HttpStatusCode.Conflict, "RESTORE_OPERATION_IN_PROGRESS");
        await (await _client.DeleteAsync($"{InstancesUrl}/{instanceId}")).AssertErrorAsync(HttpStatusCode.Conflict, "RESTORE_OPERATION_IN_PROGRESS");
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
        Assert.Empty(_factory.Provisioner.DeprovisionedInstanceIds);

        await _factory.ProcessJobAsync(jobId);

        // Finished: the guards are lifted.
        await _client.CreateBackupAsync(databaseId);
    }

    [Fact]
    public async Task Restore_RequestedConcurrently_ExactlyOneIsAccepted()
    {
        var (_, _, backupId) = await CreateBackedUpDatabaseAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostRestoreAsync(backupId)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        foreach (var rejected in responses.Where(response => response.StatusCode != HttpStatusCode.Accepted))
        {
            await rejected.AssertErrorAsync(HttpStatusCode.Conflict, "RESTORE_OPERATION_IN_PROGRESS");
        }

        Assert.Equal(1, await RestoreJobCountAsync());
    }

    [Fact]
    public async Task Restore_SecondRequestSlipsPastTheServiceCheck_IsStoppedByTheIndex()
    {
        var (_, _, backupId) = await CreateBackedUpDatabaseAsync();

        var result = await WithServiceAsync(
            beforeSave: () => ApiFactory.RequestRestoreAsync(_client, backupId),
            service => service.CreateAsync(backupId, default));

        Assert.Equal(CreateRestoreStatus.RestoreInProgress, result.Status);
        Assert.Null(result.Operation);
        Assert.Equal(1, await RestoreJobCountAsync());
    }

    [Fact]
    public async Task Restore_BackupSlipsInAfterTheServiceCheck_IsStoppedByTheIndex()
    {
        var (_, databaseId, backupId) = await CreateBackedUpDatabaseAsync();

        var result = await WithServiceAsync(
            beforeSave: () => _client.CreateBackupAsync(databaseId),
            service => service.CreateAsync(backupId, default));

        Assert.Equal(CreateRestoreStatus.BackupInProgress, result.Status);
        Assert.Equal(0, await RestoreJobCountAsync());
    }

    [Fact]
    public async Task Restore_DatabaseDeletionSlipsInAfterTheServiceCheck_IsStoppedByTheIndex()
    {
        var (_, databaseId, backupId) = await CreateBackedUpDatabaseAsync();

        var result = await WithServiceAsync(
            beforeSave: () => _client.DeleteDatabaseAsync(databaseId),
            service => service.CreateAsync(backupId, default));

        Assert.Equal(CreateRestoreStatus.DatabaseNotReady, result.Status);
        Assert.Equal(0, await RestoreJobCountAsync());
    }

    [Theory]
    [InlineData(JobType.RestoreDatabase, JobType.RestoreDatabase)]
    [InlineData(JobType.RestoreDatabase, JobType.DeleteDatabase)]
    [InlineData(JobType.DeleteDatabase, JobType.RestoreDatabase)]
    [InlineData(JobType.RestoreDatabase, JobType.BackupDatabase)]
    [InlineData(JobType.BackupDatabase, JobType.RestoreDatabase)]
    public async Task UniqueIndex_RejectsASecondUnfinishedJobForTheDatabase_WithoutTheServices(JobType first, JobType second)
    {
        var (instanceId, databaseId, backupId) = await CreateBackedUpDatabaseAsync();
        await InsertJobAsync(first, instanceId, databaseId, backupId);

        await Assert.ThrowsAsync<DbUpdateException>(() => InsertJobAsync(second, instanceId, databaseId, backupId));
    }

    [Fact]
    public async Task CheckConstraints_RejectARestoreJobWithoutBackupOrWithoutDatabase()
    {
        var (_, _, backupId) = await CreateBackedUpDatabaseAsync();
        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);

        await Assert.ThrowsAnyAsync<DbException>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"UPDATE jobs SET backup_id = NULL WHERE id = {jobId}")));
        await Assert.ThrowsAnyAsync<DbException>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"UPDATE jobs SET database_id = NULL WHERE id = {jobId}")));
    }

    [Fact]
    public void Job_RestoreJob_NeedsItsDatabaseAndItsBackup()
    {
        var now = DateTime.UtcNow;
        var databaseId = Guid.NewGuid();
        var backupId = Guid.NewGuid();

        var job = Job.Create(JobType.RestoreDatabase, Guid.NewGuid(), 3, now, databaseId, backupId);

        Assert.Equal(databaseId, job.DatabaseId);
        Assert.Equal(backupId, job.BackupId);
        Assert.Throws<ArgumentException>(() => Job.Create(JobType.RestoreDatabase, Guid.NewGuid(), 3, now, databaseId));
        Assert.Throws<ArgumentException>(() => Job.Create(JobType.RestoreDatabase, Guid.NewGuid(), 3, now, backupId: backupId));
    }

    [Fact]
    public async Task Restore_IsOnlyEverRequestedWithPost()
    {
        var (_, _, backupId) = await CreateBackedUpDatabaseAsync();

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _client.GetAsync(RestoreUrl(backupId))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _client.DeleteAsync(RestoreUrl(backupId))).StatusCode);
        Assert.Equal(0, await RestoreJobCountAsync());
    }

    // --- Helpers ------------------------------------------------------------------------------

    /// <summary>Stores a pending job directly, bypassing the service that would normally create it.</summary>
    private Task InsertJobAsync(JobType type, Guid instanceId, Guid databaseId, Guid backupId) =>
        _factory.WithDbAsync(async db =>
        {
            Guid? jobBackupId = type switch
            {
                JobType.RestoreDatabase => backupId,
                JobType.BackupDatabase => Guid.NewGuid(),
                _ => null
            };
            db.Jobs.Add(Job.Create(type, instanceId, maxAttempts: 3, DateTime.UtcNow, databaseId, jobBackupId));
            return await db.SaveChangesAsync();
        });

    /// <summary>
    /// Calls the restore service on a context that runs <paramref name="beforeSave"/> right before
    /// its first save, which is where a concurrent request can get in between the checks and the write.
    /// </summary>
    private async Task<T> WithServiceAsync<T>(Func<Task> beforeSave, Func<RestoreService, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>(services.GetRequiredService<DbContextOptions<AppDbContext>>())
                .AddInterceptors(new BeforeFirstSaveInterceptor(beforeSave))
                .Options);
        var service = new RestoreService(
            db,
            services.GetRequiredService<IBackupStorage>(),
            services.GetRequiredService<JobQueue>(),
            services.GetRequiredService<IOptions<JobOptions>>(),
            TimeProvider.System,
            NullLogger<RestoreService>.Instance);

        return await action(service);
    }

    private sealed class BeforeFirstSaveInterceptor(Func<Task> action) : SaveChangesInterceptor
    {
        private bool _ran;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_ran)
            {
                _ran = true;
                await action();
            }

            return result;
        }
    }
}

/// <summary>Short names for the kinds of run <see cref="Backups.FakeDumpTools"/> records.</summary>
internal static class FakeDumpToolsKinds
{
    public const string PgDump = Backups.FakeDumpTools.PgDump;
    public const string MySqlDump = Backups.FakeDumpTools.MySqlDump;
    public const string PgRestoreList = Backups.FakeDumpTools.PgRestoreList;
    public const string PgRestore = Backups.FakeDumpTools.PgRestore;
    public const string MySqlVersion = Backups.FakeDumpTools.MySqlVersion;
    public const string MySql = Backups.FakeDumpTools.MySql;
}
