using System.Net;
using System.Net.Http.Json;
using AuroraDbManager.Api.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests;

public sealed class InstancesApiTests : IDisposable
{
    // xUnit creates a new instance of this class per test, so every test gets an empty database.
    // The worker is off, so created instances stay in "provisioning" with a pending job.
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public InstancesApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Create_ValidRequest_ReturnsAcceptedWithInstanceAndJob()
    {
        var before = DateTimeOffset.UtcNow;

        var response = await _client.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest());

        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);

        var instance = body.GetProperty("instance");
        var id = instance.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal("production-db", instance.GetProperty("name").GetString());
        Assert.Equal("postgres", instance.GetProperty("engine").GetString());
        Assert.Equal("16", instance.GetProperty("version").GetString());
        Assert.Equal(1, instance.GetProperty("cpu").GetInt32());
        Assert.Equal(1024, instance.GetProperty("memoryMb").GetInt32());
        Assert.Equal(20, instance.GetProperty("storageGb").GetInt32());

        var createdAt = instance.GetProperty("createdAt").GetDateTimeOffset();
        Assert.InRange(createdAt, before, DateTimeOffset.UtcNow);
        Assert.Equal(createdAt, instance.GetProperty("updatedAt").GetDateTimeOffset());

        var job = body.GetProperty("job");
        Assert.NotEqual(Guid.Empty, job.GetProperty("id").GetGuid());
        Assert.Equal("provision_instance", job.GetProperty("type").GetString());
        Assert.Equal("pending", job.Status());
        Assert.Equal(id, job.GetProperty("instanceId").GetGuid());

        Assert.NotNull(response.Headers.Location);
        Assert.Equal($"{InstancesUrl}/{id}", response.Headers.Location.AbsolutePath);
    }

    [Fact]
    public async Task Create_ValidRequest_DefaultsStatusToProvisioning()
    {
        var response = await _client.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest());

        var body = await response.ReadJsonAsync();
        Assert.Equal("provisioning", body.GetProperty("instance").Status());

        var stored = await (await _client.GetAsync(response.Headers.Location)).ReadJsonAsync();
        Assert.Equal("provisioning", stored.Status());
    }

    [Fact]
    public async Task Create_ValidRequest_StoresExactlyOnePendingProvisioningJob()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();

        var jobs = await _factory.WithDbAsync(db => db.Jobs.AsNoTracking().ToListAsync());

        var job = Assert.Single(jobs);
        Assert.Equal(jobId, job.Id);
        Assert.Equal(instanceId, job.InstanceId);
        Assert.Equal(JobType.ProvisionInstance, job.Type);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(0, job.Attempt);
        Assert.Equal(3, job.MaxAttempts);
    }

    [Fact]
    public async Task Create_InvalidRequest_StoresNoJob()
    {
        await _client.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest(cpu: 0));

        Assert.Equal(0, await _factory.WithDbAsync(db => db.Jobs.CountAsync()));
    }

    [Fact]
    public async Task Create_MysqlEngine_IsAccepted()
    {
        var response = await _client.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest(engine: "mysql", version: "8.4"));

        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        Assert.Equal("mysql", body.GetProperty("instance").GetProperty("engine").GetString());
    }

    [Theory]
    [InlineData("oracle")]
    [InlineData("Postgres")]
    [InlineData("0")]
    [InlineData("")]
    public async Task Create_InvalidEngine_ReturnsValidationError(string engine)
    {
        var response = await _client.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest(engine: engine));

        await AssertValidationErrorAsync(response, "engine");
        await AssertNoInstancesAsync();
    }

    [Theory]
    [InlineData(0, 1024, 20, "cpu")]
    [InlineData(-1, 1024, 20, "cpu")]
    [InlineData(1, 0, 20, "memoryMb")]
    [InlineData(1, -512, 20, "memoryMb")]
    [InlineData(1, 1024, 0, "storageGb")]
    [InlineData(1, 1024, -20, "storageGb")]
    public async Task Create_InvalidResourceValue_ReturnsValidationError(
        int cpu, int memoryMb, int storageGb, string invalidField)
    {
        var response = await _client.PostAsJsonAsync(
            InstancesUrl, ValidInstanceRequest(cpu: cpu, memoryMb: memoryMb, storageGb: storageGb));

        await AssertValidationErrorAsync(response, invalidField);
        await AssertNoInstancesAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_EmptyName_ReturnsValidationError(string name)
    {
        var response = await _client.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest(name: name));

        await AssertValidationErrorAsync(response, "name");
    }

    [Fact]
    public async Task Create_NameTooLong_ReturnsValidationError()
    {
        var response = await _client.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest(name: new string('a', 101)));

        await AssertValidationErrorAsync(response, "name");
    }

    [Fact]
    public async Task Create_MissingFields_ReportsEveryRequiredField()
    {
        var response = await _client.PostAsJsonAsync(InstancesUrl, new { });

        await AssertValidationErrorAsync(response, "name", "engine", "version", "cpu", "memoryMb", "storageGb");
    }

    [Fact]
    public async Task Create_MalformedJson_ReturnsValidationErrorWithoutInternals()
    {
        var response = await _client.PostAsync(
            InstancesUrl, new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));

        await AssertValidationErrorAsync(response, "body");
        Assert.DoesNotContain("System.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_ExistingInstance_ReturnsInstance()
    {
        var (instanceId, _) = await _client.CreateInstanceAsync(name: "orders-db");

        var response = await _client.GetAsync($"{InstancesUrl}/{instanceId}");

        var body = await response.ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(instanceId, body.GetProperty("id").GetGuid());
        Assert.Equal("orders-db", body.GetProperty("name").GetString());
        Assert.Equal("postgres", body.GetProperty("engine").GetString());
        Assert.Equal("provisioning", body.Status());
        Assert.Equal(1024, body.GetProperty("memoryMb").GetInt32());
    }

    [Fact]
    public async Task Get_MissingInstance_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"{InstancesUrl}/{Guid.NewGuid()}");

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "INSTANCE_NOT_FOUND");
    }

    [Fact]
    public async Task Get_MalformedId_ReturnsNotFoundInErrorFormat()
    {
        var response = await _client.GetAsync($"{InstancesUrl}/not-a-guid");

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task List_NoInstances_ReturnsEmptyItems()
    {
        var body = await (await _client.GetAsync(InstancesUrl)).ReadJsonAsync();

        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task List_ReturnsInstancesNewestFirst()
    {
        await _client.CreateInstanceAsync(name: "first");
        await _client.CreateInstanceAsync(name: "second", engine: "mysql");

        var response = await _client.GetAsync(InstancesUrl);

        var body = await response.ReadJsonAsync(HttpStatusCode.OK);
        var names = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString());
        Assert.Equal(["second", "first"], names);
        Assert.Equal(2, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task List_Paginates()
    {
        await _client.CreateInstanceAsync(name: "first");
        await _client.CreateInstanceAsync(name: "second");
        await _client.CreateInstanceAsync(name: "third");

        var body = await (await _client.GetAsync($"{InstancesUrl}?page=2&pageSize=2")).ReadJsonAsync();

        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal("first", item.GetProperty("name").GetString());
        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, body.GetProperty("page").GetInt32());
        Assert.Equal(2, body.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData("page=0", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    public async Task List_InvalidPaging_ReturnsValidationError(string query, string invalidField)
    {
        var response = await _client.GetAsync($"{InstancesUrl}?{query}");

        await AssertValidationErrorAsync(response, invalidField);
    }

    [Fact]
    public async Task Delete_ExistingInstance_RemovesItAndItsJobs()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        var url = $"{InstancesUrl}/{instanceId}";

        var response = await _client.DeleteAsync(url);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(url)).StatusCode);
        await AssertNoInstancesAsync();
        await (await _client.GetAsync($"{JobsUrl}/{jobId}")).AssertErrorAsync(HttpStatusCode.NotFound, "JOB_NOT_FOUND");
    }

    [Fact]
    public async Task Delete_MissingInstance_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync($"{InstancesUrl}/{Guid.NewGuid()}");

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "INSTANCE_NOT_FOUND");
    }

    private async Task AssertNoInstancesAsync()
    {
        var body = await (await _client.GetAsync(InstancesUrl)).ReadJsonAsync();
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }

    private static async Task AssertValidationErrorAsync(HttpResponseMessage response, params string[] invalidFields)
    {
        var error = await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var reported = error.GetProperty("details").EnumerateObject().Select(p => p.Name).Order();
        Assert.Equal(invalidFields.Order(), reported);
    }
}
