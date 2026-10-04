using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Security;

/// <summary>
/// What comes in from outside: every value is bounded, an enum is one of its values, an id is an
/// id, and nothing a client sends makes the server fail or work hard.
/// </summary>
public sealed class InputValidationHardeningTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public InputValidationHardeningTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    // --- Paging -------------------------------------------------------------------------------

    public static TheoryData<string> Listings => new()
    {
        "/api/v1/instances",
        "/api/v1/jobs",
        "/api/v1/users",
        $"/api/v1/instances/{Guid.Empty}/databases",
        $"/api/v1/databases/{Guid.Empty}/backups"
    };

    [Theory]
    [MemberData(nameof(Listings))]
    public async Task PageNumber_ThatWouldOverflowTheOffset_IsRejected_NotPassedToTheDatabase(string url)
    {
        // (page - 1) * pageSize no longer fits an int for these; it used to become a negative offset.
        foreach (var page in new[] { "2147483647", "21474837", "1000001" })
        {
            var response = await _client.GetAsync($"{url}?page={page}&pageSize=100");

            var error = await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
            Assert.True(error.GetProperty("details").TryGetProperty("page", out _), error.GetRawText());
        }
    }

    [Theory]
    [InlineData("/api/v1/instances")]
    [InlineData("/api/v1/jobs")]
    [InlineData("/api/v1/users")]
    public async Task LastAllowedPage_IsServed_AsAnEmptyPage(string url)
    {
        var page = await (await _client.GetAsync($"{url}?page=1000000&pageSize=100")).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Empty(page.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [MemberData(nameof(Listings))]
    public async Task PagingValues_ThatAreNotNumbersInRange_AreRejected(string url)
    {
        foreach (var query in new[] { "page=-1", "page=abc", "page=99999999999999999999", "pageSize=-5", "pageSize=1000000", "pageSize=1e3", "page=1;drop" })
        {
            var response = await _client.GetAsync($"{url}?{query}");

            await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        }
    }

    // --- Instances ----------------------------------------------------------------------------

    [Theory]
    [InlineData("cpu", 257)]
    [InlineData("cpu", int.MaxValue)]
    [InlineData("memoryMb", 1048577)]
    [InlineData("memoryMb", int.MaxValue)]
    [InlineData("storageGb", 65537)]
    [InlineData("storageGb", int.MaxValue)]
    public async Task InstanceResources_HaveAnUpperBound(string field, int value)
    {
        var request = new Dictionary<string, object>
        {
            ["name"] = "production-db",
            ["engine"] = "postgres",
            ["version"] = "16",
            ["cpu"] = 1,
            ["memoryMb"] = 512,
            ["storageGb"] = 1
        };
        request[field] = value;

        var response = await _client.PostAsJsonAsync(InstancesUrl, request);

        var error = await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(error.GetProperty("details").TryGetProperty(field, out _), error.GetRawText());
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Instances.CountAsync()));
    }

    [Fact]
    public async Task InstanceResources_AtTheUpperBound_AreAccepted()
    {
        var response = await _client.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest(cpu: 256, memoryMb: 1048576, storageGb: 65536));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("carriage\rreturn")]
    [InlineData("tab\there")]
    [InlineData("escape\u001b[31mred")]
    [InlineData("null\u0000byte")]
    public async Task InstanceName_WithControlCharacters_IsRejected(string name)
    {
        var response = await _client.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest(name: name));

        var error = await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(error.GetProperty("details").TryGetProperty("name", out _), error.GetRawText());
    }

    [Theory]
    [InlineData("Production DB (eu-west) #2")]
    [InlineData("データベース")]
    [InlineData("$(not a command); just `a name` && nothing > else")]
    public async Task InstanceName_IsADisplayName_AndMayContainAnythingPrintable_BecauseItIsNeverUsedAsAnythingElse(string name)
    {
        var (instanceId, _) = await _client.CreateInstanceAsync(name: name);

        Assert.Equal(name, (await _client.GetInstanceAsync(instanceId)).GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("engine", "sqlite")]
    [InlineData("engine", "Postgres")]
    [InlineData("engine", "0")]
    public async Task Engine_IsOneOfTheSupportedOnes_Exactly(string field, string value)
    {
        var request = new Dictionary<string, object>
        {
            ["name"] = "production-db",
            ["engine"] = "postgres",
            ["version"] = "16",
            ["cpu"] = 1,
            ["memoryMb"] = 512,
            ["storageGb"] = 1
        };
        request[field] = value;

        var response = await _client.PostAsJsonAsync(InstancesUrl, request);

        await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Theory]
    [InlineData("{\"name\":\"a\",\"engine\":0,\"version\":\"16\",\"cpu\":1,\"memoryMb\":512,\"storageGb\":1}")]
    [InlineData("{\"name\":\"a\",\"engine\":\"postgres\",\"version\":\"16\",\"cpu\":\"1\",\"memoryMb\":512,\"storageGb\":1}")]
    [InlineData("{\"name\":[\"a\"],\"engine\":\"postgres\",\"version\":\"16\",\"cpu\":1,\"memoryMb\":512,\"storageGb\":1}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"name\":")]
    [InlineData("")]
    public async Task MalformedOrMistypedBody_IsAValidationError_NeverAServerError(string json)
    {
        var response = await _client.PostAsync(InstancesUrl, Json(json));

        await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task DeeplyNestedJson_IsRejected_WithoutExhaustingTheStack()
    {
        var json = new string('[', 100_000) + new string(']', 100_000);

        var response = await _client.PostAsync(InstancesUrl, Json(json));

        await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    // --- Ids ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("/api/v1/instances/not-a-guid")]
    [InlineData("/api/v1/instances/1")]
    [InlineData("/api/v1/instances/..%2F..%2Fetc%2Fpasswd")]
    [InlineData("/api/v1/jobs/' OR '1'='1")]
    [InlineData("/api/v1/backups/00000000-0000-0000-0000-00000000000g")]
    [InlineData("/api/v1/users/admin")]
    public async Task IdThatIsNotAGuid_MatchesNoRoute_AndIsACleanNotFound(string url)
    {
        var response = await _client.GetAsync(url);

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Theory]
    [InlineData("instanceId=not-a-guid")]
    [InlineData("databaseId=1%20OR%201=1")]
    [InlineData("status=running%27--")]
    [InlineData("status=1")]
    [InlineData("type=0")]
    [InlineData("type=backup_database%00")]
    public async Task JobFilters_AcceptOnlyIdsAndKnownValues(string query)
    {
        var response = await _client.GetAsync($"{JobsUrl}?{query}");

        await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    // --- Schedules ----------------------------------------------------------------------------

    private async Task<string> ScheduleUrlAsync()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        return $"{DatabasesUrl}/{databaseId}/backup-schedule";
    }

    public static TheoryData<string> HostileCronExpressions => new()
    {
        "* * * * * *",
        "@reboot",
        "0 2 * * * ; rm -rf /",
        "$(id) * * * *",
        "0 0 30 2 *",
        "*/0 * * * *",
        "0-59/1,0-59/1,0-59/1,0-59/1,0-59/1,0-59/1,0-59/1,0-59/1,0-59/1,0-59/1,0-59/1,0-59/1,0-59/1,0-59/1,0-59/1 * * * *",
        string.Join(',', Enumerable.Repeat("0-59", 5000)) + " * * * *",
        new string('*', 50_000),
        string.Join(' ', Enumerable.Repeat("*", 20_000)),
        "\u0000 * * * *"
    };

    [Theory]
    [MemberData(nameof(HostileCronExpressions))]
    public async Task CronExpression_ThatIsInvalidHugeOrNeverOccurs_IsRejectedQuickly(string cron)
    {
        var url = await ScheduleUrlAsync();
        var stopwatch = Stopwatch.StartNew();

        var response = await _client.PostAsJsonAsync(url, new { cronExpression = cron, timeZoneId = "UTC" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}.");
        Assert.Equal(0, await _factory.WithDbAsync(db => db.BackupSchedules.CountAsync()));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/localtime")]
    [InlineData("UTC\u0000")]
    [InlineData("Europe/Berlin; rm -rf /")]
    [InlineData("Not/AZone")]
    [InlineData(" UTC")]
    [InlineData("")]
    public async Task TimeZone_ThatIsNotAnIanaName_IsRejected_AndNeverCrashesAnything(string timeZoneId)
    {
        var url = await ScheduleUrlAsync();

        var response = await _client.PostAsJsonAsync(url, new { cronExpression = "0 2 * * *", timeZoneId });
        var huge = await _client.PostAsJsonAsync(url, new { cronExpression = "0 2 * * *", timeZoneId = new string('Z', 100_000) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
        // The application is as alive as before.
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(ReadinessUrl)).StatusCode);
    }
}
