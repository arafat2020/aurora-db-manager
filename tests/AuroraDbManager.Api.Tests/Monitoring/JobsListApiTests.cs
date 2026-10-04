using System.Net;
using System.Text.Json;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Monitoring;

/// <summary><c>GET /api/v1/jobs</c>: the job history, newest first, filtered and always paged.</summary>
public sealed class JobsListApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public JobsListApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<JsonElement> ListAsync(string query = "") =>
        await (await _client.GetAsync($"{JobsUrl}{query}")).ReadJsonAsync(HttpStatusCode.OK);

    private static List<Guid> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(job => job.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task List_NoJobs_ReturnsAnEmptyPage()
    {
        var page = await ListAsync();

        Assert.Empty(Ids(page));
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(20, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, page.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task List_ReturnsJobsNewestFirst_InTheSameShapeAsASingleJob()
    {
        var (_, first) = await _client.CreateInstanceAsync(name: "first");
        var (_, second) = await _client.CreateInstanceAsync(name: "second");
        var (_, third) = await _client.CreateInstanceAsync(name: "third");

        var page = await ListAsync();

        Assert.Equal([third, second, first], Ids(page));
        Assert.Equal(3, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            (await _client.GetJobAsync(third)).GetRawText(),
            page.GetProperty("items")[0].GetRawText());
    }

    [Fact]
    public async Task List_IsPaged_AndNeverReturnsMoreThanThePageSize()
    {
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            created.Add((await _client.CreateInstanceAsync(name: $"instance-{i}")).JobId);
        }

        created.Reverse();

        var firstPage = await ListAsync("?pageSize=2");
        var secondPage = await ListAsync("?pageSize=2&page=2");
        var lastPage = await ListAsync("?pageSize=2&page=3");
        var beyond = await ListAsync("?pageSize=2&page=4");

        Assert.Equal(created[..2], Ids(firstPage));
        Assert.Equal(created[2..4], Ids(secondPage));
        Assert.Equal(created[4..], Ids(lastPage));
        Assert.Empty(Ids(beyond));
        Assert.All(new[] { firstPage, secondPage, lastPage, beyond }, page => Assert.Equal(5, page.GetProperty("totalCount").GetInt32()));
    }

    [Fact]
    public async Task List_DefaultPageSizeIsTwenty_HoweverLongTheHistory()
    {
        for (var i = 0; i < 23; i++)
        {
            await _client.CreateInstanceAsync(name: $"instance-{i}");
        }

        var page = await ListAsync();

        Assert.Equal(20, Ids(page).Count);
        Assert.Equal(23, page.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?pageSize=101", "pageSize")]
    [InlineData("?page=0", "page")]
    [InlineData("?status=exploded", "status")]
    [InlineData("?status=Running", "status")]
    [InlineData("?type=drop_everything", "type")]
    [InlineData("?instanceId=not-a-guid", "instanceId")]
    [InlineData("?databaseId=not-a-guid", "databaseId")]
    public async Task List_InvalidQuery_IsRejected(string query, string field)
    {
        var response = await _client.GetAsync($"{JobsUrl}{query}");

        var error = await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(error.GetProperty("details").TryGetProperty(field, out _), error.GetRawText());
    }

    [Fact]
    public async Task List_FiltersByStatus()
    {
        var (_, completed) = await _client.CreateInstanceAsync(name: "completed");
        await _factory.ProcessJobAsync(completed);
        var (_, failed) = await _client.CreateInstanceAsync(name: "failed");
        _factory.Provisioner.FailAllCalls();
        await _factory.ProcessJobAsync(failed);
        var (_, pending) = await _client.CreateInstanceAsync(name: "pending");

        Assert.Equal([completed], Ids(await ListAsync("?status=completed")));
        Assert.Equal([pending], Ids(await ListAsync("?status=pending")));
        Assert.Empty(Ids(await ListAsync("?status=running")));

        var failures = await ListAsync("?status=failed");
        Assert.Equal([failed], Ids(failures));
        Assert.Equal(1, failures.GetProperty("totalCount").GetInt32());
        // What an operator is looking for: the stable code of the failure.
        Assert.Equal("PROVISIONING_FAILED", failures.GetProperty("items")[0].GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task List_FiltersByTypeInstanceAndDatabase_AlsoCombined()
    {
        var instanceA = await _factory.CreateRunningInstanceAsync(_client, name: "a");
        var instanceB = await _factory.CreateRunningInstanceAsync(_client, name: "b");
        var databaseA = await _factory.CreateReadyDatabaseAsync(_client, instanceA, "app");
        var databaseB = await _factory.CreateReadyDatabaseAsync(_client, instanceB, "app");
        var (_, backupJobA) = await _client.CreateBackupAsync(databaseA);
        await _factory.ProcessJobAsync(backupJobA);
        var (_, backupJobB) = await _client.CreateBackupAsync(databaseB);

        Assert.Equal(2, (await ListAsync("?type=provision_instance")).GetProperty("totalCount").GetInt32());
        Assert.Equal([backupJobB, backupJobA], Ids(await ListAsync("?type=backup_database")));
        Assert.Empty(Ids(await ListAsync("?type=restore_database")));

        Assert.Equal(3, (await ListAsync($"?instanceId={instanceA}")).GetProperty("totalCount").GetInt32());
        Assert.All(
            (await ListAsync($"?instanceId={instanceA}")).GetProperty("items").EnumerateArray(),
            job => Assert.Equal(instanceA, job.GetProperty("instanceId").GetGuid()));

        // The database's own jobs: its creation and its backup.
        Assert.Equal(2, (await ListAsync($"?databaseId={databaseA}")).GetProperty("totalCount").GetInt32());
        Assert.Equal([backupJobA], Ids(await ListAsync($"?databaseId={databaseA}&type=backup_database")));
        Assert.Equal([backupJobB], Ids(await ListAsync("?type=backup_database&status=pending")));
        Assert.Empty(Ids(await ListAsync($"?instanceId={instanceA}&databaseId={databaseB}")));
        Assert.Empty(Ids(await ListAsync($"?instanceId={Guid.NewGuid()}")));
    }

    [Fact]
    public async Task ListBackups_FiltersByStatus_SoTheLatestCompletedOrFailedBackupIsOneRequest()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var older = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var newer = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var (failed, failedJob) = await _client.CreateBackupAsync(databaseId);
        _factory.DumpTools.FailAllRuns();
        await _factory.ProcessJobAsync(failedJob);

        var latestCompleted = await (await _client.GetAsync($"{DatabaseBackupsUrl(databaseId)}?status=completed&pageSize=1")).ReadJsonAsync(HttpStatusCode.OK);
        var failures = await (await _client.GetAsync($"{DatabaseBackupsUrl(databaseId)}?status=failed")).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal([newer], Ids(latestCompleted));
        Assert.Equal(2, latestCompleted.GetProperty("totalCount").GetInt32());
        Assert.NotEqual(older, newer);
        Assert.Equal([failed], Ids(failures));
        Assert.Equal("BACKUP_PROCESS_FAILED", failures.GetProperty("items")[0].GetProperty("error").GetProperty("code").GetString());

        var invalid = await _client.GetAsync($"{DatabaseBackupsUrl(databaseId)}?status=lost");
        await invalid.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }
}
