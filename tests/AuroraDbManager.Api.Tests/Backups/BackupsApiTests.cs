using System.Data.Common;
using System.Net;
using System.Text.Json;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Jobs;
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

namespace AuroraDbManager.Api.Tests.Backups;

/// <summary>
/// The backups API. The worker is off: a requested backup stays pending until a test runs its
/// job, so every status can be observed.
/// </summary>
public sealed class BackupsApiTests : IDisposable
{
    private static readonly string[] BackupFields =
        ["completedAt", "createdAt", "databaseId", "error", "id", "sizeBytes", "status", "storageType"];

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public BackupsApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // --- Create -------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ReadyDatabase_ReturnsAcceptedWithPendingBackupAndPendingJob()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var before = DateTimeOffset.UtcNow;

        var response = await PostBackupAsync(databaseId);

        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        Assert.Equal(["backup", "job"], body.EnumerateObject().Select(property => property.Name).Order());

        var backup = body.GetProperty("backup");
        var id = backup.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(databaseId, backup.GetProperty("databaseId").GetGuid());
        Assert.Equal("pending", backup.Status());
        Assert.Equal("local", backup.GetProperty("storageType").GetString());
        Assert.InRange(backup.GetProperty("createdAt").GetDateTimeOffset(), before, DateTimeOffset.UtcNow);
        Assert.Equal(JsonValueKind.Null, backup.GetProperty("completedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, backup.GetProperty("sizeBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, backup.GetProperty("error").ValueKind);
        Assert.Equal(BackupFields, backup.EnumerateObject().Select(property => property.Name).Order());

        var job = body.GetProperty("job");
        Assert.Equal("backup_database", job.GetProperty("type").GetString());
        Assert.Equal("pending", job.Status());
        Assert.Equal(instanceId, job.GetProperty("instanceId").GetGuid());
        Assert.Equal(databaseId, job.GetProperty("databaseId").GetGuid());
        Assert.Equal(id, job.GetProperty("backupId").GetGuid());

        Assert.NotNull(response.Headers.Location);
        Assert.Equal($"{BackupsUrl}/{id}", response.Headers.Location.AbsolutePath);
    }

    [Fact]
    public async Task Create_StoresTheBackupWithExactlyOnePendingJob_AndRunsNothing()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();

        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);

        var stored = Assert.Single(await _factory.WithDbAsync(db => db.Backups.AsNoTracking().ToListAsync()));
        Assert.Equal(backupId, stored.Id);
        Assert.Equal(databaseId, stored.DatabaseId);
        Assert.Equal(BackupStatus.Pending, stored.Status);
        Assert.Equal(BackupStorageType.Local, stored.StorageType);
        Assert.Null(stored.Path);

        var job = Assert.Single(await _factory.WithDbAsync(db => db.Jobs.AsNoTracking().Where(j => j.BackupId != null).ToListAsync()));
        Assert.Equal(jobId, job.Id);
        Assert.Equal(JobType.BackupDatabase, job.Type);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(instanceId, job.InstanceId);
        Assert.Equal(databaseId, job.DatabaseId);

        // The request itself does no backup work and writes nothing; that is the job's.
        Assert.Equal(0, _factory.DumpTools.RunCount);
        Assert.Empty(_factory.BackupFiles());
    }

    [Fact]
    public async Task Create_IgnoresAnythingTheClientSends_AboutWhereOrHowToBackUp()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();

        var response = await _client.PostAsync(
            DatabaseBackupsUrl(databaseId),
            System.Net.Http.Json.JsonContent.Create(new
            {
                path = "/etc/cron.d/owned",
                storageType = "s3",
                executable = "/bin/sh",
                arguments = new[] { "-c", "id" }
            }));

        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        var backupId = body.GetProperty("backup").GetProperty("id").GetGuid();
        Assert.Equal("local", body.GetProperty("backup").GetProperty("storageType").GetString());
        await _factory.ProcessJobAsync(body.GetProperty("job").GetProperty("id").GetGuid());

        var run = Assert.Single(_factory.DumpTools.Runs);
        Assert.Equal("pg_dump", run.Executable);
        Assert.Equal(_factory.BackupFilePath(instanceId, databaseId, backupId, "dump") + ".partial", run.OutputPath);
        Assert.DoesNotContain(run.Arguments, argument => argument.Contains("cron", StringComparison.Ordinal) || argument == "id");
    }

    [Fact]
    public async Task Create_UnknownDatabase_ReturnsDatabaseNotFound()
    {
        var response = await PostBackupAsync(Guid.NewGuid());

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Create_MalformedDatabaseId_ReturnsNotFoundInErrorFormat()
    {
        var response = await _client.PostAsync($"{DatabasesUrl}/not-a-guid/backups", content: null);

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Create_CreatingDatabase_IsRejected()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var (databaseId, _) = await _client.CreateDatabaseAsync(instanceId, "app");

        var response = await PostBackupAsync(databaseId);

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_NOT_READY");
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Create_DeletingDatabase_IsRejected()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        await _client.DeleteDatabaseAsync(databaseId);

        var response = await PostBackupAsync(databaseId);

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_NOT_READY");
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Create_FailedDatabase_IsRejected()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var (databaseId, jobId) = await _client.CreateDatabaseAsync(instanceId, "app");
        _factory.DatabaseServers.FailAllCalls();
        await _factory.ProcessJobAsync(jobId);

        var response = await PostBackupAsync(databaseId);

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_NOT_READY");
        await AssertNothingStoredAsync();
    }

    [Theory]
    [InlineData(InstanceStatus.Stopped)]
    [InlineData(InstanceStatus.Failed)]
    public async Task Create_InstanceNotRunning_IsRejectedAndNotStarted(InstanceStatus status)
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        await _factory.SetInstanceStatusAsync(instanceId, status);

        var response = await PostBackupAsync(databaseId);

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "INSTANCE_NOT_READY");
        await AssertNothingStoredAsync();
        Assert.Empty(_factory.Provisioner.EnsureRunningInstanceIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_WhileABackupOfTheDatabaseIsUnfinished_IsRejected(bool firstIsRunning)
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (_, firstJobId) = await _client.CreateBackupAsync(databaseId);
        if (firstIsRunning)
        {
            await _factory.SimulateAbandonedExecutionAsync(firstJobId, DateTime.UtcNow.AddHours(1));
        }

        var response = await PostBackupAsync(databaseId);

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "BACKUP_OPERATION_IN_PROGRESS");
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
        Assert.Equal(1, await BackupJobCountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_AfterTheEarlierBackupFinished_IsAccepted_WhetherItCompletedOrFailed(bool earlierFailed)
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (firstId, firstJobId) = await _client.CreateBackupAsync(databaseId);
        if (earlierFailed)
        {
            _factory.DumpTools.FailNextRuns(3);
        }

        await _factory.ProcessJobAsync(firstJobId);
        Assert.Equal(earlierFailed ? "failed" : "completed", (await _client.GetBackupAsync(firstId)).Status());

        var (secondId, secondJobId) = await _client.CreateBackupAsync(databaseId);
        await _factory.ProcessJobAsync(secondJobId);

        Assert.NotEqual(firstId, secondId);
        Assert.Equal("completed", (await _client.GetBackupAsync(secondId)).Status());
        Assert.Equal(2, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
    }

    [Fact]
    public async Task Create_ForDifferentDatabasesOfOneInstance_CanBeUnfinishedAtTheSameTime()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var first = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "first");
        var second = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "second");

        await _client.CreateBackupAsync(first);
        await _client.CreateBackupAsync(second);

        Assert.Equal(2, await BackupJobCountAsync());
    }

    [Fact]
    public async Task Create_RequestedConcurrently_ExactlyOneIsAccepted()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostBackupAsync(databaseId)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        foreach (var rejected in responses.Where(response => response.StatusCode != HttpStatusCode.Accepted))
        {
            await rejected.AssertErrorAsync(HttpStatusCode.Conflict, "BACKUP_OPERATION_IN_PROGRESS");
        }

        Assert.Equal(1, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
        Assert.Equal(1, await BackupJobCountAsync());
    }

    [Fact]
    public async Task Create_SecondRequestSlipsPastTheServiceCheck_IsStoppedByTheIndex_AndLeavesNoBackupBehind()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();

        // A concurrent request stores its backup and job after this one's check, just before its save.
        var result = await WithServiceAsync(
            beforeSave: () => _client.CreateBackupAsync(databaseId),
            service => service.CreateAsync(databaseId, default));

        Assert.Equal(CreateBackupStatus.BackupInProgress, result.Status);
        Assert.Null(result.Operation);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
        Assert.Equal(1, await BackupJobCountAsync());
    }

    [Fact]
    public async Task Create_DatabaseDeletionSlipsInAfterTheServiceCheck_IsStoppedByTheIndex()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();

        var result = await WithServiceAsync(
            beforeSave: () => _client.DeleteDatabaseAsync(databaseId),
            service => service.CreateAsync(databaseId, default));

        Assert.Equal(CreateBackupStatus.DatabaseNotReady, result.Status);
        await AssertNothingStoredAsync();
    }

    [Theory]
    [InlineData(JobType.BackupDatabase, JobType.DeleteDatabase)]
    [InlineData(JobType.DeleteDatabase, JobType.BackupDatabase)]
    [InlineData(JobType.BackupDatabase, JobType.BackupDatabase)]
    public async Task UniqueIndex_RejectsASecondUnfinishedJobForTheDatabase_WithoutTheServices(JobType first, JobType second)
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        await InsertJobAsync(first, instanceId, databaseId);

        await Assert.ThrowsAsync<DbUpdateException>(() => InsertJobAsync(second, instanceId, databaseId));
    }

    [Fact]
    public async Task CheckConstraints_RejectABackupJobWithoutBackup_AnotherJobWithOne_AndAnArtifactOnAnUnfinishedBackup()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, backupJobId) = await _client.CreateBackupAsync(databaseId);
        var otherJobId = await _factory.WithDbAsync(db => db.Jobs.Where(j => j.Type == JobType.CreateDatabase).Select(j => j.Id).SingleAsync());

        await Assert.ThrowsAnyAsync<DbException>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"UPDATE jobs SET backup_id = NULL WHERE id = {backupJobId}")));
        await Assert.ThrowsAnyAsync<DbException>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"UPDATE jobs SET backup_id = {backupId} WHERE id = {otherJobId}")));
        await Assert.ThrowsAnyAsync<DbException>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"UPDATE backups SET path = '/x', size_bytes = 1 WHERE id = {backupId}")));
        await Assert.ThrowsAnyAsync<DbException>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"UPDATE backups SET status = 'completed' WHERE id = {backupId}")));
        await Assert.ThrowsAnyAsync<DbException>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"UPDATE backups SET status = 'uploading' WHERE id = {backupId}")));
        await Assert.ThrowsAnyAsync<DbException>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"UPDATE backups SET storage_type = 'tape' WHERE id = {backupId}")));
    }

    // --- Get and list -------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReportsTheBackupThroughItsLifecycle_AndNeverItsPath()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Assert.Equal("pending", (await _client.GetBackupAsync(backupId)).Status());

        await _factory.ProcessJobAsync(jobId);

        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("completed", backup.Status());
        Assert.Equal(databaseId, backup.GetProperty("databaseId").GetGuid());
        Assert.Equal("local", backup.GetProperty("storageType").GetString());
        Assert.Equal(FakeDumpTools.PostgresDump.Length, backup.GetProperty("sizeBytes").GetInt64());
        Assert.True(backup.GetProperty("completedAt").GetDateTimeOffset() >= backup.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, backup.GetProperty("error").ValueKind);
        Assert.Equal(BackupFields, backup.EnumerateObject().Select(property => property.Name).Order());

        // The path is stored, and stays on the server.
        var path = _factory.BackupFilePath(instanceId, databaseId, backupId, "dump");
        Assert.Equal(path, (await _factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync())).Path);
        foreach (var body in new[] { backup.GetRawText(), (await _client.GetJobAsync(jobId)).GetRawText(), await _client.GetStringAsync(DatabaseBackupsUrl(databaseId)) })
        {
            Assert.DoesNotContain(_factory.BackupRoot, body);
            Assert.DoesNotContain(".dump", body);
        }
    }

    [Fact]
    public async Task Get_FailedBackup_ReportsASafeError()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        _factory.DumpTools.FailAllRuns();
        await _factory.ProcessJobAsync(jobId);

        var backup = await _client.GetBackupAsync(backupId);

        Assert.Equal("failed", backup.Status());
        Assert.Equal("BACKUP_PROCESS_FAILED", backup.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, backup.GetProperty("sizeBytes").ValueKind);
        Assert.Equal(JsonValueKind.String, backup.GetProperty("completedAt").ValueKind);
        Assert.DoesNotContain("raw-tool-detail", backup.GetRawText());
    }

    [Fact]
    public async Task Get_MissingBackup_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"{BackupsUrl}/{Guid.NewGuid()}");

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "BACKUP_NOT_FOUND");
    }

    [Fact]
    public async Task Get_MalformedId_ReturnsNotFoundInErrorFormat()
    {
        var response = await _client.GetAsync($"{BackupsUrl}/not-a-guid");

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task List_NoBackups_ReturnsEmptyItems()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();

        var body = await (await _client.GetAsync(DatabaseBackupsUrl(databaseId))).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task List_ReturnsTheDatabasesBackupsNewestFirst_AndNoOtherDatabases()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var otherDatabaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "other");
        var first = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var second = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var (third, _) = await _client.CreateBackupAsync(databaseId);
        await _factory.CreateCompletedBackupAsync(_client, otherDatabaseId);

        var body = await (await _client.GetAsync(DatabaseBackupsUrl(databaseId))).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal([third, second, first], Ids(body));
        Assert.Equal(["pending", "completed", "completed"], body.GetProperty("items").EnumerateArray().Select(item => item.Status()));
        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
        Assert.All(body.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal(databaseId, item.GetProperty("databaseId").GetGuid());
            Assert.Equal(BackupFields, item.EnumerateObject().Select(property => property.Name).Order());
        });
    }

    [Fact]
    public async Task List_Paginates_WithStableOrderAcrossPages()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            created.Add(await _factory.CreateCompletedBackupAsync(_client, databaseId));
        }

        var listed = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var body = await (await _client.GetAsync($"{DatabaseBackupsUrl(databaseId)}?page={page}&pageSize=2")).ReadJsonAsync(HttpStatusCode.OK);
            Assert.Equal(page, body.GetProperty("page").GetInt32());
            Assert.Equal(2, body.GetProperty("pageSize").GetInt32());
            Assert.Equal(5, body.GetProperty("totalCount").GetInt32());
            listed.AddRange(Ids(body));
        }

        Assert.Equal(created.AsEnumerable().Reverse(), listed);
    }

    [Fact]
    public async Task List_UnknownDatabase_ReturnsDatabaseNotFound()
    {
        var response = await _client.GetAsync(DatabaseBackupsUrl(Guid.NewGuid()));

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
    }

    [Theory]
    [InlineData("page=0", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    public async Task List_InvalidPaging_ReturnsValidationError(string query, string invalidField)
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();

        var response = await _client.GetAsync($"{DatabaseBackupsUrl(databaseId)}?{query}");

        var error = await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal([invalidField], error.GetProperty("details").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task Backups_CannotBeDeletedOrChangedThroughTheApi()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _client.DeleteAsync($"{BackupsUrl}/{backupId}")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _client.PutAsync($"{BackupsUrl}/{backupId}", content: null)).StatusCode);
        Assert.Single(_factory.BackupFiles());
    }

    // --- Database and instance deletion -------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteDatabase_WhileItsBackupIsUnfinished_IsRejected_AndTheDatabaseStaysReady(bool backupIsRunning)
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (_, backupJobId) = await _client.CreateBackupAsync(databaseId);
        if (backupIsRunning)
        {
            await _factory.SimulateAbandonedExecutionAsync(backupJobId, DateTime.UtcNow.AddHours(1));
        }

        var response = await _client.DeleteAsync($"{DatabasesUrl}/{databaseId}");

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "BACKUP_OPERATION_IN_PROGRESS");
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Jobs.CountAsync(j => j.Type == JobType.DeleteDatabase)));
    }

    [Fact]
    public async Task DeleteDatabase_BackupSlipsInAfterTheServiceCheck_IsStoppedByTheIndex()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();

        var result = await WithDatabaseServiceAsync(
            beforeSave: () => _client.CreateBackupAsync(databaseId),
            service => service.DeleteAsync(databaseId, default));

        Assert.Equal(Application.Databases.DeleteDatabaseStatus.BackupInProgress, result.Status);
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Jobs.CountAsync(j => j.Type == JobType.DeleteDatabase)));
    }

    [Fact]
    public async Task DeleteDatabase_WithFinishedBackups_IsAccepted_RemovesTheirMetadata_AndKeepsTheirFiles()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var completedId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var (failedId, failedJobId) = await _client.CreateBackupAsync(databaseId);
        _factory.DumpTools.FailNextRuns(3);
        await _factory.ProcessJobAsync(failedJobId);
        var artifact = _factory.BackupFilePath(instanceId, databaseId, completedId, "dump");

        var deleteJobId = await _client.DeleteDatabaseAsync(databaseId);
        // While the database is being deleted its backups are still there.
        Assert.Equal("completed", (await _client.GetBackupAsync(completedId)).Status());
        await _factory.ProcessJobAsync(deleteJobId);

        Assert.Equal("completed", (await _client.GetJobAsync(deleteJobId)).Status());
        foreach (var backupId in new[] { completedId, failedId })
        {
            await (await _client.GetAsync($"{BackupsUrl}/{backupId}")).AssertErrorAsync(HttpStatusCode.NotFound, "BACKUP_NOT_FOUND");
        }

        Assert.Equal(0, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
        Assert.Equal([artifact], _factory.BackupFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteInstance_WhileABackupIsUnfinished_IsRejected_AndNothingIsRemoved(bool backupIsRunning)
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, backupJobId) = await _client.CreateBackupAsync(databaseId);
        if (backupIsRunning)
        {
            await _factory.SimulateAbandonedExecutionAsync(backupJobId, DateTime.UtcNow.AddHours(1));
        }

        var response = await _client.DeleteAsync($"{InstancesUrl}/{instanceId}");

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "BACKUP_OPERATION_IN_PROGRESS");
        Assert.Empty(_factory.Provisioner.DeprovisionedInstanceIds);
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
        Assert.Equal("pending", (await _client.GetBackupAsync(backupId)).Status());
    }

    [Fact]
    public async Task DeleteInstance_WithFinishedBackups_IsAccepted_RemovesTheirMetadata_AndKeepsTheirFiles()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var artifact = _factory.BackupFilePath(instanceId, databaseId, backupId, "dump");

        var response = await _client.DeleteAsync($"{InstancesUrl}/{instanceId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Jobs.CountAsync()));
        Assert.Equal([artifact], _factory.BackupFiles());
    }

    // --- Helpers ------------------------------------------------------------------------------

    private async Task<(Guid InstanceId, Guid DatabaseId)> CreateReadyDatabaseAsync()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        return (instanceId, await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app"));
    }

    private Task<HttpResponseMessage> PostBackupAsync(Guid databaseId) =>
        _client.PostAsync(DatabaseBackupsUrl(databaseId), content: null);

    private Task<int> BackupJobCountAsync() =>
        _factory.WithDbAsync(db => db.Jobs.CountAsync(j => j.Type == JobType.BackupDatabase));

    private async Task AssertNothingStoredAsync()
    {
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
        Assert.Equal(0, await BackupJobCountAsync());
    }

    /// <summary>Stores a pending job directly, bypassing the service that would normally create it.</summary>
    private Task InsertJobAsync(JobType type, Guid instanceId, Guid databaseId) =>
        _factory.WithDbAsync(async db =>
        {
            Guid? backupId = null;
            if (type == JobType.BackupDatabase)
            {
                var backup = Backup.Create(databaseId, BackupStorageType.Local, DateTime.UtcNow);
                db.Backups.Add(backup);
                backupId = backup.Id;
            }

            db.Jobs.Add(Job.Create(type, instanceId, maxAttempts: 3, DateTime.UtcNow, databaseId, backupId));
            return await db.SaveChangesAsync();
        });

    /// <summary>
    /// Calls the backup service on a context that runs <paramref name="beforeSave"/> right before
    /// its first save, which is where a concurrent request can get in between the checks and the write.
    /// </summary>
    private async Task<T> WithServiceAsync<T>(Func<Task> beforeSave, Func<BackupService, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await using var db = InterceptedContext(services, beforeSave);
        var service = new BackupService(
            db,
            services.GetRequiredService<IBackupStorage>(),
            services.GetRequiredService<JobQueue>(),
            services.GetRequiredService<IOptions<JobOptions>>(),
            TimeProvider.System,
            NullLogger<BackupService>.Instance);

        return await action(service);
    }

    private async Task<T> WithDatabaseServiceAsync<T>(Func<Task> beforeSave, Func<Application.Databases.DatabaseService, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await using var db = InterceptedContext(services, beforeSave);
        var service = new Application.Databases.DatabaseService(
            db,
            services.GetRequiredService<JobQueue>(),
            services.GetRequiredService<IOptions<JobOptions>>(),
            TimeProvider.System,
            NullLogger<Application.Databases.DatabaseService>.Instance);

        return await action(service);
    }

    private static AppDbContext InterceptedContext(IServiceProvider services, Func<Task> beforeSave) =>
        new(new DbContextOptionsBuilder<AppDbContext>(services.GetRequiredService<DbContextOptions<AppDbContext>>())
            .AddInterceptors(new BeforeFirstSaveInterceptor(beforeSave))
            .Options);

    private static IEnumerable<Guid> Ids(JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid());

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
