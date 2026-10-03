using System.Data.Common;
using System.Net;
using System.Text.Json;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Databases;

/// <summary>
/// The create_database and delete_database handlers, run through the real job processor with
/// <see cref="ApiFactory.ProcessJobAsync"/>. Jobs get three attempts with no delay in between.
/// </summary>
public sealed class DatabaseJobTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public DatabaseJobTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private FakeDatabaseServers Servers => _factory.DatabaseServers;

    // --- create_database ----------------------------------------------------------------------

    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    public async Task Create_Success_MarksDatabaseReadyAndCompletesJob_ThroughTheInstancesEngine(string engine)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client, engine: engine);
        var (databaseId, jobId) = await _client.CreateDatabaseAsync(instanceId, "app");

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal("create_database", job.GetProperty("type").GetString());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Equal(databaseId, job.GetProperty("databaseId").GetGuid());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("error").ValueKind);

        var database = await _client.GetDatabaseAsync(databaseId);
        Assert.Equal("ready", database.Status());
        Assert.Equal(JsonValueKind.Null, database.GetProperty("error").ValueKind);
        Assert.True(database.GetProperty("updatedAt").GetDateTimeOffset() > database.GetProperty("createdAt").GetDateTimeOffset());

        Assert.Equal([$"{engine} create app done"], Servers.Calls);
        Assert.True(Servers.Exists(instanceId, "app"));
    }

    [Fact]
    public async Task Create_AttemptFails_DatabaseStaysCreatingWhileTheJobRetries()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var (databaseId, jobId) = await _client.CreateDatabaseAsync(instanceId, "app");
        Servers.FailNextCalls(1);
        Servers.Block();

        var processing = _factory.ProcessJobAsync(jobId);
        await Servers.WaitForCallAsync();
        Servers.Release();
        // The first attempt has failed and the second is now held inside the manager.
        await Servers.WaitForCallAsync();

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("running", job.Status());
        Assert.Equal(2, job.GetProperty("attempt").GetInt32());
        Assert.Equal("DATABASE_CONNECTION_FAILED", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("creating", (await _client.GetDatabaseAsync(databaseId)).Status());

        Servers.Release();
        await processing.WaitAsync(TestTimeout);

        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(["postgres create app failed", "postgres create app done"], Servers.Calls);
    }

    [Fact]
    public async Task Create_EveryAttemptFails_FailsJobAndDatabase_WithASafeError()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var (databaseId, jobId) = await _client.CreateDatabaseAsync(instanceId, "app");
        Servers.FailAllCalls();

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        Assert.Equal("DATABASE_CONNECTION_FAILED", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(3, Servers.CallCount);

        var database = await _client.GetDatabaseAsync(databaseId);
        Assert.Equal("failed", database.Status());
        Assert.Equal("DATABASE_CONNECTION_FAILED", database.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "Could not connect to the instance's database server.",
            database.GetProperty("error").GetProperty("message").GetString());

        // Nothing of the driver's own failure, which named a password, reaches a client.
        foreach (var body in new[] { job.GetRawText(), database.GetRawText() })
        {
            Assert.DoesNotContain("raw-driver-detail", body);
            Assert.DoesNotContain("hunter2", body);
        }
    }

    [Fact]
    public async Task Create_ManagerThrowsSomethingUnexpected_IsReportedAsAGenericCreateFailure()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var (databaseId, jobId) = await _client.CreateDatabaseAsync(instanceId, "app");
        Servers.FailAllCalls(new InvalidOperationException("Host=10.0.0.5;Password=hunter2"));

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("DATABASE_CREATE_FAILED", job.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("hunter2", job.GetRawText());
        Assert.DoesNotContain("hunter2", (await _client.GetDatabaseAsync(databaseId)).GetRawText());
    }

    [Fact]
    public async Task Create_DatabaseAlreadyExistsInTheEngine_IsAdoptedAndBecomesReady()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var (databaseId, jobId) = await _client.CreateDatabaseAsync(instanceId, "app");
        Servers.Add(instanceId, "app");

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(["postgres create app already-exists"], Servers.Calls);
    }

    [Theory]
    [InlineData(InstanceStatus.Stopped)]
    [InlineData(InstanceStatus.Failed)]
    public async Task Create_InstanceNoLongerRunning_FailsWithoutTouchingTheEngineOrStartingTheInstance(InstanceStatus status)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var (databaseId, jobId) = await _client.CreateDatabaseAsync(instanceId, "app");
        await _factory.SetInstanceStatusAsync(instanceId, status);

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal("INSTANCE_NOT_READY", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("failed", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(0, Servers.CallCount);
        Assert.Empty(_factory.Provisioner.EnsureRunningInstanceIds);
    }

    [Fact]
    public async Task Create_InstanceComesBackDuringRetries_Succeeds()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var (databaseId, jobId) = await _client.CreateDatabaseAsync(instanceId, "app");
        // The instance is reachable in the metadata, but its server refuses the first two attempts.
        Servers.FailNextCalls(2, new DatabaseOperationException(
            DatabaseErrorCodes.DatabaseEngineUnavailable, "The instance's database server is not running."));

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
    }

    [Fact]
    public async Task Create_DatabaseMetadataIsGone_FailsWithoutTouchingTheEngine()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var jobId = await InsertJobAsync(JobType.CreateDatabase, instanceId, databaseId: Guid.NewGuid());

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal("DATABASE_DOES_NOT_EXIST", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, Servers.CallCount);
    }

    [Fact]
    public async Task Create_JobNamesAnotherInstanceThanTheDatabases_FailsWithoutTouchingEitherInstance()
    {
        var owner = await _factory.CreateRunningInstanceAsync(_client, "owner");
        var other = await _factory.CreateRunningInstanceAsync(_client, "other");
        var databaseId = await InsertDatabaseAsync(owner, "app");
        var jobId = await InsertJobAsync(JobType.CreateDatabase, other, databaseId);

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal("DATABASE_INVALID_STATE", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, Servers.CallCount);
        Assert.False(Servers.Exists(owner, "app"));
        Assert.False(Servers.Exists(other, "app"));
        // The database is not the job's to fail either.
        Assert.Equal("creating", (await _client.GetDatabaseAsync(databaseId)).Status());
    }

    [Fact]
    public async Task Create_FailedDatabase_IsNotMadeReadyByALaterJob()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var (databaseId, firstJobId) = await _client.CreateDatabaseAsync(instanceId, "app");
        Servers.FailAllCalls();
        await _factory.ProcessJobAsync(firstJobId);
        Servers.FailNextCalls(0);
        var callsBefore = Servers.CallCount;
        var secondJobId = await InsertJobAsync(JobType.CreateDatabase, instanceId, databaseId);

        await _factory.ProcessJobAsync(secondJobId);

        Assert.Equal("DATABASE_INVALID_STATE", (await _client.GetJobAsync(secondJobId)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("failed", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(callsBefore, Servers.CallCount);
    }

    // --- delete_database ----------------------------------------------------------------------

    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    public async Task Delete_Success_DropsTheDatabaseThenRemovesItsMetadataAndCompletesJob(string engine)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client, engine: engine);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var jobId = await _client.DeleteDatabaseAsync(databaseId);

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal("delete_database", job.GetProperty("type").GetString());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Equal(databaseId, job.GetProperty("databaseId").GetGuid());
        Assert.Equal($"{engine} delete app done", Servers.Calls[^1]);
        Assert.False(Servers.Exists(instanceId, "app"));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Databases.CountAsync()));
        await (await _client.GetAsync($"{DatabasesUrl}/{databaseId}")).AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
    }

    [Fact]
    public async Task Delete_DatabaseAlreadyAbsentFromTheEngine_StillRemovesTheMetadata()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await InsertDatabaseAsync(instanceId, "app", DatabaseStatus.Ready);
        var jobId = await _client.DeleteDatabaseAsync(databaseId);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
        Assert.Equal(["postgres delete app already-absent"], Servers.Calls);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Databases.CountAsync()));
    }

    [Fact]
    public async Task Delete_AttemptFails_MetadataIsKeptAsDeletingWhileTheJobRetries()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var jobId = await _client.DeleteDatabaseAsync(databaseId);
        Servers.FailNextCalls(1);
        Servers.Block();

        var processing = _factory.ProcessJobAsync(jobId);
        await Servers.WaitForCallAsync();
        Servers.Release();
        // The first attempt has failed and the second is now held inside the manager.
        await Servers.WaitForCallAsync();

        Assert.Equal("running", (await _client.GetJobAsync(jobId)).Status());
        Assert.Equal("deleting", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.True(Servers.Exists(instanceId, "app"));

        Servers.Release();
        await processing.WaitAsync(TestTimeout);

        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
        Assert.False(Servers.Exists(instanceId, "app"));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Databases.CountAsync()));
    }

    [Fact]
    public async Task Delete_EveryAttemptFails_KeepsTheMetadataAsFailed_AndTheEngineDatabase()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var jobId = await _client.DeleteDatabaseAsync(databaseId);
        Servers.FailAllCalls(new DatabaseOperationException(
            DatabaseErrorCodes.DatabaseDeleteFailed, "The database could not be deleted.", new IOException("raw-driver-detail")));

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        Assert.Equal("DATABASE_DELETE_FAILED", job.GetProperty("error").GetProperty("code").GetString());

        var database = await _client.GetDatabaseAsync(databaseId);
        Assert.Equal("failed", database.Status());
        Assert.Equal("DATABASE_DELETE_FAILED", database.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("raw-driver-detail", job.GetRawText() + database.GetRawText());
        Assert.True(Servers.Exists(instanceId, "app"));
    }

    [Theory]
    [InlineData(InstanceStatus.Stopped)]
    [InlineData(InstanceStatus.Failed)]
    public async Task Delete_InstanceNoLongerRunning_FailsAndKeepsTheMetadata(InstanceStatus status)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var jobId = await _client.DeleteDatabaseAsync(databaseId);
        var callsBefore = Servers.CallCount;
        await _factory.SetInstanceStatusAsync(instanceId, status);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("INSTANCE_NOT_READY", (await _client.GetJobAsync(jobId)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("failed", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(callsBefore, Servers.CallCount);
        Assert.True(Servers.Exists(instanceId, "app"));
    }

    [Fact]
    public async Task Delete_JobNamesAnotherInstanceThanTheDatabases_FailsWithoutDroppingAnything()
    {
        var owner = await _factory.CreateRunningInstanceAsync(_client, "owner");
        var other = await _factory.CreateRunningInstanceAsync(_client, "other");
        Servers.Add(owner, "app");
        Servers.Add(other, "app");
        var databaseId = await InsertDatabaseAsync(owner, "app", DatabaseStatus.Deleting);
        var jobId = await InsertJobAsync(JobType.DeleteDatabase, other, databaseId);

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal("DATABASE_INVALID_STATE", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, Servers.CallCount);
        Assert.True(Servers.Exists(owner, "app"));
        Assert.True(Servers.Exists(other, "app"));
        Assert.Equal("deleting", (await _client.GetDatabaseAsync(databaseId)).Status());
    }

    [Fact]
    public async Task Delete_DatabaseIsNotMarkedDeleting_FailsWithoutDroppingIt()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var jobId = await InsertJobAsync(JobType.DeleteDatabase, instanceId, databaseId);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("DATABASE_INVALID_STATE", (await _client.GetJobAsync(jobId)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.True(Servers.Exists(instanceId, "app"));
    }

    [Fact]
    public async Task Delete_MetadataAlreadyGone_CompletesWithNothingToDo()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var jobId = await InsertJobAsync(JobType.DeleteDatabase, instanceId, databaseId: Guid.NewGuid());

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
        Assert.Equal(0, Servers.CallCount);
    }

    // --- Job invariants -----------------------------------------------------------------------

    [Theory]
    [InlineData(JobType.CreateDatabase, JobType.CreateDatabase)]
    [InlineData(JobType.CreateDatabase, JobType.DeleteDatabase)]
    [InlineData(JobType.DeleteDatabase, JobType.DeleteDatabase)]
    public async Task UniqueIndex_RejectsASecondUnfinishedJobForTheSameDatabase(JobType first, JobType second)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await InsertDatabaseAsync(instanceId, "app");
        await InsertJobAsync(first, instanceId, databaseId);

        await Assert.ThrowsAsync<DbUpdateException>(() => InsertJobAsync(second, instanceId, databaseId));

        Assert.Equal(1, await _factory.WithDbAsync(db => db.Jobs.CountAsync(j => j.DatabaseId == databaseId)));
    }

    [Fact]
    public async Task UniqueIndex_AllowsANewJobOnceTheEarlierOneIsFinished_AndJobsForOtherDatabasesOfTheInstance()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var first = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "first");
        await InsertDatabaseAsync(instanceId, "second");

        // "first" has a completed create job; "second" gets its own unfinished create job alongside.
        await _client.DeleteDatabaseAsync(first);
        await _client.CreateDatabaseAsync(instanceId, "third");

        Assert.Equal(2, await _factory.WithDbAsync(db => db.Jobs.CountAsync(j => j.DatabaseId == first)));
        Assert.Equal(2, await _factory.WithDbAsync(db => db.Jobs.CountAsync(
            j => j.DatabaseId != null && j.Status == JobStatus.Pending)));
    }

    [Fact]
    public async Task CheckConstraint_RejectsADatabaseJobWithoutDatabase_AndAProvisioningJobWithOne()
    {
        var (instanceId, provisionJobId) = await _client.CreateInstanceAsync();
        await _factory.ProcessJobAsync(provisionJobId);
        var (_, databaseJobId) = await _client.CreateDatabaseAsync(instanceId, "app");

        await Assert.ThrowsAnyAsync<DbException>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"UPDATE jobs SET database_id = NULL WHERE id = {databaseJobId}")));
        await Assert.ThrowsAnyAsync<DbException>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"UPDATE jobs SET database_id = {Guid.NewGuid()} WHERE id = {provisionJobId}")));
    }

    [Fact]
    public async Task CheckConstraint_RejectsAnUnknownDatabaseStatus()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await InsertDatabaseAsync(instanceId, "app");

        await Assert.ThrowsAnyAsync<DbException>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"UPDATE databases SET status = 'provisioning' WHERE id = {databaseId}")));
    }

    // --- Helpers ------------------------------------------------------------------------------

    /// <summary>Stores a database row directly, in the given status and with no job of its own.</summary>
    private Task<Guid> InsertDatabaseAsync(Guid instanceId, string name, DatabaseStatus status = DatabaseStatus.Creating) =>
        _factory.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var database = Database.Create(instanceId, name, now);
            if (status is DatabaseStatus.Ready or DatabaseStatus.Deleting)
            {
                database.MarkReady(now);
            }

            if (status == DatabaseStatus.Deleting)
            {
                database.MarkDeleting(now);
            }

            db.Databases.Add(database);
            await db.SaveChangesAsync();
            return database.Id;
        });

    /// <summary>Stores a pending job directly, bypassing the service that would normally create it.</summary>
    private Task<Guid> InsertJobAsync(JobType type, Guid instanceId, Guid databaseId) =>
        _factory.WithDbAsync(async db =>
        {
            var job = Job.Create(type, instanceId, maxAttempts: 3, DateTime.UtcNow, databaseId);
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            return job.Id;
        });
}
