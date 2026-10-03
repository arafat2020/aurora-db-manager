using System.Net;
using AuroraDbManager.Api.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Databases;

/// <summary>
/// Database jobs under the existing job recovery. A "restart" disposes one factory, losing its
/// in-memory queue like a stopped process, and starts a second one on the same system database.
/// Each factory has its own <see cref="FakeDatabaseServers"/>, so the engine state the restarted
/// application finds is whatever the test puts there.
/// </summary>
public sealed class DatabaseJobRecoveryTests : IDisposable
{
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddHours(-1);

    private readonly TempDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private ApiFactory StoppedWorker() => new() { DatabasePath = _database.Path };

    private ApiFactory RestartedApplication() => new() { DatabasePath = _database.Path, RunWorker = true };

    [Fact]
    public async Task Restart_CreateInterruptedAfterTheEngineCreatedTheDatabase_AdoptsItAndMarksItReady()
    {
        Guid instanceId, databaseId, jobId;
        using (var before = StoppedWorker())
        using (var client = before.CreateClient())
        {
            instanceId = await before.CreateRunningInstanceAsync(client);
            (databaseId, jobId) = await client.CreateDatabaseAsync(instanceId, "app");
            // The execution ran CREATE DATABASE and died before it could complete the job.
            await before.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);
        }

        using var after = RestartedApplication();
        after.DatabaseServers.Add(instanceId, "app");
        using var restarted = after.CreateClient();
        var job = await restarted.WaitForFinishedJobAsync(jobId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Equal("ready", (await restarted.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(["postgres create app already-exists"], after.DatabaseServers.Calls);
    }

    [Fact]
    public async Task Restart_DeleteInterruptedAfterTheEngineDroppedTheDatabase_RemovesTheMetadata()
    {
        Guid databaseId, jobId;
        using (var before = StoppedWorker())
        using (var client = before.CreateClient())
        {
            var instanceId = await before.CreateRunningInstanceAsync(client);
            databaseId = await before.CreateReadyDatabaseAsync(client, instanceId, "app");
            jobId = await client.DeleteDatabaseAsync(databaseId);
            // The execution ran DROP DATABASE and died before it could remove the metadata.
            await before.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);
            Assert.Equal("deleting", (await client.GetDatabaseAsync(databaseId)).Status());
        }

        // The restarted application's engine has no such database any more.
        using var after = RestartedApplication();
        using var restarted = after.CreateClient();
        var job = await restarted.WaitForFinishedJobAsync(jobId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Equal(["postgres delete app already-absent"], after.DatabaseServers.Calls);
        await (await restarted.GetAsync($"{DatabasesUrl}/{databaseId}")).AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
        Assert.Equal(0, await after.WithDbAsync(db => db.Databases.CountAsync()));
    }

    [Fact]
    public async Task Restart_PendingDatabaseJobsWhoseQueueEntriesWereLost_AreExecuted()
    {
        Guid instanceId, createdId, createJobId, deletedId, deleteJobId;
        using (var before = StoppedWorker())
        using (var client = before.CreateClient())
        {
            instanceId = await before.CreateRunningInstanceAsync(client);
            deletedId = await before.CreateReadyDatabaseAsync(client, instanceId, "old");
            deleteJobId = await client.DeleteDatabaseAsync(deletedId);
            (createdId, createJobId) = await client.CreateDatabaseAsync(instanceId, "app");
        }

        using var after = RestartedApplication();
        after.DatabaseServers.Add(instanceId, "old");
        using var restarted = after.CreateClient();

        Assert.Equal("completed", (await restarted.WaitForFinishedJobAsync(createJobId)).Status());
        Assert.Equal("completed", (await restarted.WaitForFinishedJobAsync(deleteJobId)).Status());
        Assert.Equal("ready", (await restarted.GetDatabaseAsync(createdId)).Status());
        Assert.Equal(HttpStatusCode.NotFound, (await restarted.GetAsync($"{DatabasesUrl}/{deletedId}")).StatusCode);
        Assert.True(after.DatabaseServers.Exists(instanceId, "app"));
        Assert.False(after.DatabaseServers.Exists(instanceId, "old"));
    }

    [Fact]
    public async Task Recovery_DatabaseJobWithExpiredLease_IsReturnedToPendingWithItsDatabaseUntouched()
    {
        using var factory = StoppedWorker();
        using var client = factory.CreateClient();
        var instanceId = await factory.CreateRunningInstanceAsync(client);
        var (databaseId, jobId) = await client.CreateDatabaseAsync(instanceId, "app");
        await factory.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);

        var recovered = await factory.RecoverJobsAsync(includePending: false);

        Assert.Equal([jobId], recovered);
        var job = await factory.GetJobEntityAsync(jobId);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(0, job.Attempt);
        Assert.Equal(databaseId, job.DatabaseId);
        Assert.Equal("creating", (await client.GetDatabaseAsync(databaseId)).Status());
    }
}
