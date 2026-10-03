using System.Net;
using System.Text.Json;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests;

public sealed class JobsApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public JobsApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Get_PendingJob_ReturnsCurrentState()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();

        var response = await _client.GetAsync($"{JobsUrl}/{jobId}");

        var job = await response.ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(jobId, job.GetProperty("id").GetGuid());
        Assert.Equal("provision_instance", job.GetProperty("type").GetString());
        Assert.Equal("pending", job.Status());
        Assert.Equal(instanceId, job.GetProperty("instanceId").GetGuid());
        Assert.Equal(0, job.GetProperty("attempt").GetInt32());
        Assert.Equal(3, job.GetProperty("maxAttempts").GetInt32());
        Assert.NotNull(job.GetProperty("createdAt").GetString());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("startedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, job.GetProperty("completedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, job.GetProperty("error").ValueKind);
        // A provisioning job works on no database, so the field is left out altogether.
        Assert.False(job.TryGetProperty("databaseId", out _));
    }

    [Fact]
    public async Task Get_AfterProcessing_ReflectsNewState()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();
        Assert.Equal("pending", (await _client.GetJobAsync(jobId)).Status());

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(JsonValueKind.String, job.GetProperty("startedAt").ValueKind);
        Assert.Equal(JsonValueKind.String, job.GetProperty("completedAt").ValueKind);
    }

    [Fact]
    public async Task Get_FailedJob_ReturnsErrorCodeAndMessage()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();
        _factory.Provisioner.FailAllCalls();
        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);

        Assert.Equal("failed", job.Status());
        var error = job.GetProperty("error");
        Assert.Equal("PROVISIONING_FAILED", error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.Equal(2, error.EnumerateObject().Count());
    }

    [Fact]
    public async Task Get_MissingJob_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"{JobsUrl}/{Guid.NewGuid()}");

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "JOB_NOT_FOUND");
    }

    [Fact]
    public async Task Get_MalformedId_ReturnsNotFoundInErrorFormat()
    {
        var response = await _client.GetAsync($"{JobsUrl}/not-a-guid");

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }
}
