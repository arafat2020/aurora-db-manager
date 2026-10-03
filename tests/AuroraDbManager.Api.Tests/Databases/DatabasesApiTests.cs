using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Databases;

/// <summary>
/// The databases API. The worker is off: a requested operation stays pending until a test runs
/// its job, so every lifecycle status can be observed.
/// </summary>
public sealed class DatabasesApiTests : IDisposable
{
    private static readonly string[] DatabaseFields = ["createdAt", "error", "id", "instanceId", "name", "status", "updatedAt"];

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public DatabasesApiTests()
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
    public async Task Create_ValidRequest_ReturnsAcceptedWithCreatingDatabaseAndPendingJob()
    {
        var instanceId = await CreateRunningInstanceAsync();
        var before = DateTimeOffset.UtcNow;

        var response = await PostDatabaseAsync(instanceId, "application");

        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        Assert.Equal(["database", "job"], body.EnumerateObject().Select(property => property.Name).Order());

        var database = body.GetProperty("database");
        var id = database.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(instanceId, database.GetProperty("instanceId").GetGuid());
        Assert.Equal("application", database.GetProperty("name").GetString());
        Assert.Equal("creating", database.Status());
        Assert.Equal(JsonValueKind.Null, database.GetProperty("error").ValueKind);
        var createdAt = database.GetProperty("createdAt").GetDateTimeOffset();
        Assert.InRange(createdAt, before, DateTimeOffset.UtcNow);
        Assert.Equal(createdAt, database.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(DatabaseFields, database.EnumerateObject().Select(property => property.Name).Order());

        var job = body.GetProperty("job");
        Assert.NotEqual(Guid.Empty, job.GetProperty("id").GetGuid());
        Assert.Equal("create_database", job.GetProperty("type").GetString());
        Assert.Equal("pending", job.Status());
        Assert.Equal(instanceId, job.GetProperty("instanceId").GetGuid());
        Assert.Equal(id, job.GetProperty("databaseId").GetGuid());
        Assert.Equal(0, job.GetProperty("attempt").GetInt32());

        Assert.NotNull(response.Headers.Location);
        Assert.Equal($"{DatabasesUrl}/{id}", response.Headers.Location.AbsolutePath);
    }

    [Fact]
    public async Task Create_ValidRequest_StoresTheDatabaseWithExactlyOnePendingJob_AndTouchesNoEngine()
    {
        var instanceId = await CreateRunningInstanceAsync();

        var (databaseId, jobId) = await _client.CreateDatabaseAsync(instanceId, "application");

        var stored = Assert.Single(await _factory.WithDbAsync(db => db.Databases.AsNoTracking().ToListAsync()));
        Assert.Equal(databaseId, stored.Id);
        Assert.Equal(instanceId, stored.InstanceId);
        Assert.Equal("application", stored.Name);
        Assert.Equal(DatabaseStatus.Creating, stored.Status);

        var job = Assert.Single(await _factory.WithDbAsync(db => db.Jobs.AsNoTracking().Where(j => j.DatabaseId != null).ToListAsync()));
        Assert.Equal(jobId, job.Id);
        Assert.Equal(JobType.CreateDatabase, job.Type);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(instanceId, job.InstanceId);
        Assert.Equal(databaseId, job.DatabaseId);

        // The request itself does no engine work; that is the job's.
        Assert.Equal(0, _factory.DatabaseServers.CallCount);
        Assert.False(_factory.DatabaseServers.Exists(instanceId, "application"));
    }

    [Fact]
    public async Task Create_ThenJobRuns_DatabaseBecomesReadyAndExistsInTheEngine()
    {
        var instanceId = await CreateRunningInstanceAsync();
        var (databaseId, jobId) = await _client.CreateDatabaseAsync(instanceId, "application");

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
        Assert.True(_factory.DatabaseServers.Exists(instanceId, "application"));
    }

    [Fact]
    public async Task Create_UnknownInstance_ReturnsInstanceNotFoundAndStoresNothing()
    {
        var response = await PostDatabaseAsync(Guid.NewGuid(), "application");

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "INSTANCE_NOT_FOUND");
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Create_MalformedInstanceId_ReturnsNotFoundInErrorFormat()
    {
        var response = await _client.PostAsJsonAsync($"{InstancesUrl}/not-a-guid/databases", new { name = "application" });

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Create_ProvisioningInstance_IsRejected()
    {
        var (instanceId, _) = await _client.CreateInstanceAsync();

        var response = await PostDatabaseAsync(instanceId, "application");

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "INSTANCE_NOT_READY");
        await AssertNothingStoredAsync();
        Assert.Equal("provisioning", (await _client.GetInstanceAsync(instanceId)).Status());
    }

    [Fact]
    public async Task Create_FailedInstance_IsRejected()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        _factory.Provisioner.FailAllCalls();
        await _factory.ProcessJobAsync(jobId);
        Assert.Equal("failed", (await _client.GetInstanceAsync(instanceId)).Status());

        var response = await PostDatabaseAsync(instanceId, "application");

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "INSTANCE_NOT_READY");
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Create_StoppedInstance_IsRejectedAndNotStarted()
    {
        var instanceId = await CreateRunningInstanceAsync();
        await _factory.SetInstanceStatusAsync(instanceId, InstanceStatus.Stopped);

        var response = await PostDatabaseAsync(instanceId, "application");

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "INSTANCE_NOT_READY");
        await AssertNothingStoredAsync();
        Assert.Equal("stopped", (await _client.GetInstanceAsync(instanceId)).Status());
        Assert.Empty(_factory.Provisioner.EnsureRunningInstanceIds);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("app_1")]
    [InlineData("analytics_2026_archive")]
    public async Task Create_ValidName_IsStoredUnchanged(string name)
    {
        var instanceId = await CreateRunningInstanceAsync();

        var body = await (await PostDatabaseAsync(instanceId, name)).ReadJsonAsync(HttpStatusCode.Accepted);

        Assert.Equal(name, body.GetProperty("database").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Create_NameOfMaximumLength_IsAccepted()
    {
        var instanceId = await CreateRunningInstanceAsync();

        var response = await PostDatabaseAsync(instanceId, new string('a', 63));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("my-db")]
    [InlineData("my db")]
    [InlineData(" app ")]
    [InlineData("App")]
    [InlineData("1app")]
    [InlineData("_app")]
    [InlineData("app;drop")]
    [InlineData("app\"; DROP DATABASE postgres; --")]
    [InlineData("naïve")]
    [InlineData("postgres")]
    [InlineData("mysql")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Create_InvalidName_ReturnsNameInvalidAndStoresNothing(string name)
    {
        var instanceId = await CreateRunningInstanceAsync();

        var response = await PostDatabaseAsync(instanceId, name);

        await response.AssertErrorAsync(HttpStatusCode.BadRequest, "DATABASE_NAME_INVALID");
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Create_MissingName_ReturnsNameInvalid()
    {
        var instanceId = await CreateRunningInstanceAsync();

        var response = await _client.PostAsJsonAsync(InstanceDatabasesUrl(instanceId), new { });

        await response.AssertErrorAsync(HttpStatusCode.BadRequest, "DATABASE_NAME_INVALID");
    }

    [Fact]
    public async Task Create_MalformedJson_ReturnsValidationErrorWithoutInternals()
    {
        var instanceId = await CreateRunningInstanceAsync();

        var response = await _client.PostAsync(
            InstanceDatabasesUrl(instanceId),
            new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));

        await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.DoesNotContain("System.", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_DuplicateNameOnSameInstance_ReturnsAlreadyExists_WhateverTheFirstOnesStatus(bool firstIsReady)
    {
        var instanceId = await CreateRunningInstanceAsync();
        var (_, jobId) = await _client.CreateDatabaseAsync(instanceId, "app");
        if (firstIsReady)
        {
            await _factory.ProcessJobAsync(jobId);
        }

        var response = await PostDatabaseAsync(instanceId, "app");

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_ALREADY_EXISTS");
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Databases.CountAsync()));
        Assert.Equal(1, await DatabaseJobCountAsync());
    }

    [Fact]
    public async Task Create_SameNameOnDifferentInstances_Succeeds()
    {
        var first = await CreateRunningInstanceAsync("first");
        var second = await CreateRunningInstanceAsync("second");

        foreach (var instanceId in new[] { first, second })
        {
            await _client.CreateDatabaseAsync(instanceId, "app");
            await _client.CreateDatabaseAsync(instanceId, "analytics");
        }

        // Four databases being created at once, two per instance, each with its own job.
        Assert.Equal(4, await _factory.WithDbAsync(db => db.Databases.CountAsync()));
        Assert.Equal(4, await DatabaseJobCountAsync());
    }

    [Fact]
    public async Task UniqueIndex_RejectsDuplicateNameOnSameInstance_WithoutTheService()
    {
        var instanceId = await CreateRunningInstanceAsync();
        await InsertDatabaseAsync(instanceId, "app");

        await Assert.ThrowsAsync<DbUpdateException>(() => InsertDatabaseAsync(instanceId, "app"));

        Assert.Equal(1, await _factory.WithDbAsync(db => db.Databases.CountAsync()));
    }

    [Fact]
    public async Task ForeignKey_RejectsDatabaseOfUnknownInstance_WithoutTheService()
    {
        await Assert.ThrowsAsync<DbUpdateException>(() => InsertDatabaseAsync(Guid.NewGuid(), "app"));

        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Create_DuplicateSlipsPastTheServiceCheck_IsStoppedByTheIndex_AndLeavesNoJobBehind()
    {
        var instanceId = await CreateRunningInstanceAsync();

        // A concurrent request stores the same name after the service's own check, just before its save.
        var result = await WithServiceAsync(
            beforeSave: () => InsertDatabaseAsync(instanceId, "app"),
            service => service.CreateAsync(instanceId, new CreateDatabaseRequest { Name = "app" }, default));

        Assert.Equal(CreateDatabaseStatus.AlreadyExists, result.Status);
        Assert.Null(result.Operation);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Databases.CountAsync()));
        // The losing request's job was in the same transaction as its database and went with it.
        Assert.Equal(0, await DatabaseJobCountAsync());
    }

    [Fact]
    public async Task Create_InstanceDeletedAfterTheServiceCheck_IsReportedAsInstanceNotFound()
    {
        var instanceId = await CreateRunningInstanceAsync();

        var result = await WithServiceAsync(
            beforeSave: () => _client.DeleteAsync($"{InstancesUrl}/{instanceId}"),
            service => service.CreateAsync(instanceId, new CreateDatabaseRequest { Name = "app" }, default));

        Assert.Equal(CreateDatabaseStatus.InstanceNotFound, result.Status);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Create_SameNameRequestedConcurrently_ExactlyOneIsAccepted()
    {
        var instanceId = await CreateRunningInstanceAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostDatabaseAsync(instanceId, "app")));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        foreach (var rejected in responses.Where(response => response.StatusCode != HttpStatusCode.Accepted))
        {
            await rejected.AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_ALREADY_EXISTS");
        }

        Assert.Equal(1, await _factory.WithDbAsync(db => db.Databases.CountAsync()));
        Assert.Equal(1, await DatabaseJobCountAsync());
    }

    // --- Get and list -------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReportsEveryLifecycleStatus()
    {
        var instanceId = await CreateRunningInstanceAsync();
        var (databaseId, createJobId) = await _client.CreateDatabaseAsync(instanceId, "orders");
        Assert.Equal("creating", (await _client.GetDatabaseAsync(databaseId)).Status());

        await _factory.ProcessJobAsync(createJobId);
        var ready = await _client.GetDatabaseAsync(databaseId);
        Assert.Equal("ready", ready.Status());
        Assert.Equal(instanceId, ready.GetProperty("instanceId").GetGuid());
        Assert.Equal("orders", ready.GetProperty("name").GetString());
        Assert.Equal(DatabaseFields, ready.EnumerateObject().Select(property => property.Name).Order());

        var deleteJobId = await _client.DeleteDatabaseAsync(databaseId);
        Assert.Equal("deleting", (await _client.GetDatabaseAsync(databaseId)).Status());

        _factory.DatabaseServers.FailAllCalls();
        await _factory.ProcessJobAsync(deleteJobId);
        var failed = await _client.GetDatabaseAsync(databaseId);
        Assert.Equal("failed", failed.Status());
        Assert.Equal("DATABASE_CONNECTION_FAILED", failed.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_MissingDatabase_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"{DatabasesUrl}/{Guid.NewGuid()}");

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
    }

    [Fact]
    public async Task Get_MalformedId_ReturnsNotFoundInErrorFormat()
    {
        var response = await _client.GetAsync($"{DatabasesUrl}/not-a-guid");

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task List_NoDatabases_ReturnsEmptyItems()
    {
        var instanceId = await CreateRunningInstanceAsync();

        var body = await (await _client.GetAsync(InstanceDatabasesUrl(instanceId))).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task List_ReturnsDatabasesNewestFirst_WithTheirStatus()
    {
        var instanceId = await CreateRunningInstanceAsync();
        await _factory.CreateReadyDatabaseAsync(_client, instanceId, "first");
        var second = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "second");
        await _client.CreateDatabaseAsync(instanceId, "third");
        await _client.DeleteDatabaseAsync(second);

        var body = await (await _client.GetAsync(InstanceDatabasesUrl(instanceId))).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal(["third", "second", "first"], Names(body));
        Assert.Equal(["creating", "deleting", "ready"], body.GetProperty("items").EnumerateArray().Select(item => item.Status()));
        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task List_SameCreationTime_IsOrderedByIdAndStableAcrossPages()
    {
        var instanceId = await CreateRunningInstanceAsync();
        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var databases = Enumerable.Range(0, 5)
            .Select(index => Database.Create(instanceId, $"db_{index}", createdAt))
            .ToList();
        await _factory.WithDbAsync(async db =>
        {
            db.Databases.AddRange(databases);
            return await db.SaveChangesAsync();
        });

        var listed = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var body = await (await _client.GetAsync($"{InstanceDatabasesUrl(instanceId)}?page={page}&pageSize=2")).ReadJsonAsync();
            listed.AddRange(body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        }

        Assert.Equal(5, listed.Distinct().Count());
        Assert.Equal(databases.Select(database => database.Id).Order(), listed.Order());
        var again = await (await _client.GetAsync($"{InstanceDatabasesUrl(instanceId)}?pageSize=5")).ReadJsonAsync();
        Assert.Equal(listed, again.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task List_Paginates()
    {
        var instanceId = await CreateRunningInstanceAsync();
        await _client.CreateDatabaseAsync(instanceId, "first");
        await _client.CreateDatabaseAsync(instanceId, "second");
        await _client.CreateDatabaseAsync(instanceId, "third");

        var body = await (await _client.GetAsync($"{InstanceDatabasesUrl(instanceId)}?page=2&pageSize=2")).ReadJsonAsync();

        Assert.Equal(["first"], Names(body));
        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, body.GetProperty("page").GetInt32());
        Assert.Equal(2, body.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task List_ReturnsOnlyDatabasesOfTheRequestedInstance()
    {
        var first = await CreateRunningInstanceAsync("first");
        var second = await CreateRunningInstanceAsync("second");
        await _client.CreateDatabaseAsync(first, "app");
        await _client.CreateDatabaseAsync(first, "analytics");
        await _client.CreateDatabaseAsync(second, "app");
        await _client.CreateDatabaseAsync(second, "billing");

        var body = await (await _client.GetAsync(InstanceDatabasesUrl(first))).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal(["analytics", "app"], Names(body));
        Assert.Equal(2, body.GetProperty("totalCount").GetInt32());
        Assert.All(
            body.GetProperty("items").EnumerateArray(),
            item => Assert.Equal(first, item.GetProperty("instanceId").GetGuid()));
    }

    [Fact]
    public async Task List_UnknownInstance_ReturnsInstanceNotFound()
    {
        var response = await _client.GetAsync(InstanceDatabasesUrl(Guid.NewGuid()));

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "INSTANCE_NOT_FOUND");
    }

    [Theory]
    [InlineData("page=0", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    public async Task List_InvalidPaging_ReturnsValidationError(string query, string invalidField)
    {
        var instanceId = await CreateRunningInstanceAsync();

        var response = await _client.GetAsync($"{InstanceDatabasesUrl(instanceId)}?{query}");

        var error = await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal([invalidField], error.GetProperty("details").EnumerateObject().Select(property => property.Name));
    }

    // --- Delete -------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ReadyDatabase_ReturnsAcceptedWithDeletingDatabaseAndPendingJob_AndKeepsTheMetadata()
    {
        var instanceId = await CreateRunningInstanceAsync();
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var callsBefore = _factory.DatabaseServers.CallCount;

        var response = await _client.DeleteAsync($"{DatabasesUrl}/{databaseId}");

        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        var database = body.GetProperty("database");
        Assert.Equal(databaseId, database.GetProperty("id").GetGuid());
        Assert.Equal("app", database.GetProperty("name").GetString());
        Assert.Equal("deleting", database.Status());

        var job = body.GetProperty("job");
        Assert.Equal("delete_database", job.GetProperty("type").GetString());
        Assert.Equal("pending", job.Status());
        Assert.Equal(instanceId, job.GetProperty("instanceId").GetGuid());
        Assert.Equal(databaseId, job.GetProperty("databaseId").GetGuid());

        // Nothing is removed by the request: the metadata and the engine's database are both still there.
        Assert.Equal("deleting", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.True(_factory.DatabaseServers.Exists(instanceId, "app"));
        Assert.Equal(callsBefore, _factory.DatabaseServers.CallCount);
    }

    [Fact]
    public async Task Delete_ThenJobRuns_DatabaseIsGoneFromTheEngineAndFromTheApi()
    {
        var instanceId = await CreateRunningInstanceAsync();
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var jobId = await _client.DeleteDatabaseAsync(databaseId);

        await _factory.ProcessJobAsync(jobId);

        await (await _client.GetAsync($"{DatabasesUrl}/{databaseId}")).AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
        Assert.False(_factory.DatabaseServers.Exists(instanceId, "app"));
        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        // The job outlives its database and still says which one it deleted.
        Assert.Equal(databaseId, job.GetProperty("databaseId").GetGuid());
    }

    [Fact]
    public async Task Delete_CreatingDatabase_IsRejectedWithoutACompetingJob()
    {
        var instanceId = await CreateRunningInstanceAsync();
        var (databaseId, createJobId) = await _client.CreateDatabaseAsync(instanceId, "app");

        var response = await _client.DeleteAsync($"{DatabasesUrl}/{databaseId}");

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_CREATING");
        Assert.Equal("creating", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(1, await DatabaseJobCountAsync());

        // The creation is unaffected and still completes.
        await _factory.ProcessJobAsync(createJobId);
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
    }

    [Fact]
    public async Task Delete_DeletingDatabase_IsRejectedWithoutASecondJob()
    {
        var instanceId = await CreateRunningInstanceAsync();
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        await _client.DeleteDatabaseAsync(databaseId);

        var response = await _client.DeleteAsync($"{DatabasesUrl}/{databaseId}");

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_DELETING");
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Jobs.CountAsync(j => j.Type == JobType.DeleteDatabase)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_FailedDatabase_IsRejected_AndNothingIsRetried(bool failedWhileDeleting)
    {
        var instanceId = await CreateRunningInstanceAsync();
        Guid databaseId;
        if (failedWhileDeleting)
        {
            databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
            var jobId = await _client.DeleteDatabaseAsync(databaseId);
            _factory.DatabaseServers.FailAllCalls();
            await _factory.ProcessJobAsync(jobId);
        }
        else
        {
            (databaseId, var jobId) = await _client.CreateDatabaseAsync(instanceId, "app");
            _factory.DatabaseServers.FailAllCalls();
            await _factory.ProcessJobAsync(jobId);
        }

        var jobsBefore = await DatabaseJobCountAsync();
        var callsBefore = _factory.DatabaseServers.CallCount;

        var response = await _client.DeleteAsync($"{DatabasesUrl}/{databaseId}");

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_FAILED");
        Assert.Equal("failed", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(jobsBefore, await DatabaseJobCountAsync());
        Assert.Equal(callsBefore, _factory.DatabaseServers.CallCount);
    }

    [Fact]
    public async Task Delete_InstanceNotRunning_IsRejectedAndTheDatabaseStaysReady()
    {
        var instanceId = await CreateRunningInstanceAsync();
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        await _factory.SetInstanceStatusAsync(instanceId, InstanceStatus.Stopped);

        var response = await _client.DeleteAsync($"{DatabasesUrl}/{databaseId}");

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "INSTANCE_NOT_READY");
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
    }

    [Fact]
    public async Task Delete_MissingDatabase_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync($"{DatabasesUrl}/{Guid.NewGuid()}");

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
    }

    [Fact]
    public async Task Delete_AfterDeletionCompleted_ReturnsNotFound_AndTheNameCanBeUsedAgain()
    {
        var instanceId = await CreateRunningInstanceAsync();
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        await _factory.ProcessJobAsync(await _client.DeleteDatabaseAsync(databaseId));

        var response = await _client.DeleteAsync($"{DatabasesUrl}/{databaseId}");

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
        Assert.Equal(HttpStatusCode.Accepted, (await PostDatabaseAsync(instanceId, "app")).StatusCode);
    }

    [Fact]
    public async Task Delete_NameOfDeletingDatabase_CannotBeReusedYet()
    {
        var instanceId = await CreateRunningInstanceAsync();
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        await _client.DeleteDatabaseAsync(databaseId);

        var response = await PostDatabaseAsync(instanceId, "app");

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_ALREADY_EXISTS");
    }

    [Fact]
    public async Task Delete_SecondRequestSlipsPastTheStatusCheck_IsStoppedByTheConcurrencyToken()
    {
        var instanceId = await CreateRunningInstanceAsync();
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");

        // A concurrent request deletes the database after this one loaded it as ready.
        var result = await WithServiceAsync(
            beforeSave: () => _client.DeleteDatabaseAsync(databaseId),
            service => service.DeleteAsync(databaseId, default));

        Assert.Equal(DeleteDatabaseStatus.Deleting, result.Status);
        Assert.Null(result.Operation);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Jobs.CountAsync(j => j.Type == JobType.DeleteDatabase)));
    }

    [Fact]
    public async Task Delete_RequestedConcurrently_ExactlyOneIsAccepted()
    {
        var instanceId = await CreateRunningInstanceAsync();
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => _client.DeleteAsync($"{DatabasesUrl}/{databaseId}")));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        foreach (var rejected in responses.Where(response => response.StatusCode != HttpStatusCode.Accepted))
        {
            await rejected.AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_DELETING");
        }

        Assert.Equal(1, await _factory.WithDbAsync(db => db.Jobs.CountAsync(j => j.Type == JobType.DeleteDatabase)));
    }

    // --- Instance deletion --------------------------------------------------------------------

    [Fact]
    public async Task DeleteInstance_WithSettledDatabases_RemovesTheirMetadataAndNoOtherInstances()
    {
        var doomed = await CreateRunningInstanceAsync("doomed");
        var kept = await CreateRunningInstanceAsync("kept");
        var doomedDatabase = await _factory.CreateReadyDatabaseAsync(_client, doomed, "app");
        var (failedDatabase, failedJob) = await _client.CreateDatabaseAsync(doomed, "analytics");
        _factory.DatabaseServers.FailAllCalls();
        await _factory.ProcessJobAsync(failedJob);
        _factory.DatabaseServers.FailNextCalls(0);
        var keptDatabase = await _factory.CreateReadyDatabaseAsync(_client, kept, "app");

        var response = await _client.DeleteAsync($"{InstancesUrl}/{doomed}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal([doomed], _factory.Provisioner.DeprovisionedInstanceIds);
        Assert.Equal([keptDatabase], await _factory.WithDbAsync(db => db.Databases.Select(d => d.Id).ToListAsync()));
        await (await _client.GetAsync($"{DatabasesUrl}/{doomedDatabase}")).AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
        await (await _client.GetAsync($"{DatabasesUrl}/{failedDatabase}")).AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Jobs.CountAsync(j => j.InstanceId == doomed)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteInstance_WhileADatabaseOperationIsUnfinished_IsRejectedAndNothingIsRemoved(bool deleting)
    {
        var instanceId = await CreateRunningInstanceAsync();
        Guid databaseId;
        if (deleting)
        {
            databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
            await _client.DeleteDatabaseAsync(databaseId);
        }
        else
        {
            (databaseId, _) = await _client.CreateDatabaseAsync(instanceId, "app");
        }

        var response = await _client.DeleteAsync($"{InstancesUrl}/{instanceId}");

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_OPERATION_IN_PROGRESS");
        Assert.Empty(_factory.Provisioner.DeprovisionedInstanceIds);
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
        Assert.Equal(deleting ? "deleting" : "creating", (await _client.GetDatabaseAsync(databaseId)).Status());
    }

    // --- Helpers ------------------------------------------------------------------------------

    private Task<Guid> CreateRunningInstanceAsync(string name = "production-db") =>
        _factory.CreateRunningInstanceAsync(_client, name);

    private Task<HttpResponseMessage> PostDatabaseAsync(Guid instanceId, string name) =>
        _client.PostAsJsonAsync(InstanceDatabasesUrl(instanceId), new { name });

    /// <summary>Stores a database row directly, the way a concurrent request would, bypassing the service's checks.</summary>
    private Task InsertDatabaseAsync(Guid instanceId, string name) =>
        _factory.WithDbAsync(async db =>
        {
            db.Databases.Add(Database.Create(instanceId, name, DateTime.UtcNow));
            return await db.SaveChangesAsync();
        });

    /// <summary>
    /// Calls the service on a context that runs <paramref name="beforeSave"/> right before its
    /// first save, which is where a concurrent request can get in between the checks and the write.
    /// </summary>
    private async Task<T> WithServiceAsync<T>(Func<Task> beforeSave, Func<DatabaseService, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var options = new DbContextOptionsBuilder<AppDbContext>(services.GetRequiredService<DbContextOptions<AppDbContext>>())
            .AddInterceptors(new BeforeFirstSaveInterceptor(beforeSave))
            .Options;
        await using var db = new AppDbContext(options);
        var service = new DatabaseService(
            db,
            services.GetRequiredService<JobQueue>(),
            services.GetRequiredService<IOptions<JobOptions>>(),
            TimeProvider.System,
            NullLogger<DatabaseService>.Instance);

        return await action(service);
    }

    private Task<int> DatabaseJobCountAsync() =>
        _factory.WithDbAsync(db => db.Jobs.CountAsync(j => j.DatabaseId != null));

    private async Task AssertNothingStoredAsync()
    {
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Databases.CountAsync()));
        Assert.Equal(0, await DatabaseJobCountAsync());
    }

    private static IEnumerable<string?> Names(JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString());

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
