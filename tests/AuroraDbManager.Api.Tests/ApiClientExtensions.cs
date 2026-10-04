using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AuroraDbManager.Api.Tests;

internal static class ApiClientExtensions
{
    public const string InstancesUrl = "/api/v1/instances";
    public const string JobsUrl = "/api/v1/jobs";
    public const string DatabasesUrl = "/api/v1/databases";
    public const string BackupsUrl = "/api/v1/backups";
    public const string MonitoringSummaryUrl = "/api/v1/monitoring/summary";
    public const string HealthUrl = "/health";
    public const string ReadinessUrl = "/health/ready";
    public const string StorageHealthUrl = "/health/storage";

    public static string InstanceHealthUrl(Guid instanceId) => $"{InstancesUrl}/{instanceId}/health";

    public static string DatabaseBackupsUrl(Guid databaseId) => $"{DatabasesUrl}/{databaseId}/backups";

    public static string InstanceDatabasesUrl(Guid instanceId) => $"{InstancesUrl}/{instanceId}/databases";

    public static object ValidInstanceRequest(
        string name = "production-db",
        string engine = "postgres",
        string version = "16",
        int cpu = 1,
        int memoryMb = 1024,
        int storageGb = 20) =>
        new { name, engine, version, cpu, memoryMb, storageGb };

    /// <summary>Creates an instance and returns the ids of the instance and of its provisioning job.</summary>
    public static async Task<(Guid InstanceId, Guid JobId)> CreateInstanceAsync(
        this HttpClient client, string name = "production-db", string engine = "postgres")
    {
        var response = await client.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest(name: name, engine: engine));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.ReadJsonAsync();
        return (body.GetProperty("instance").GetProperty("id").GetGuid(), body.GetProperty("job").GetProperty("id").GetGuid());
    }

    public static async Task<JsonElement> GetInstanceAsync(this HttpClient client, Guid id) =>
        await (await client.GetAsync($"{InstancesUrl}/{id}")).ReadJsonAsync(HttpStatusCode.OK);

    /// <summary>Requests a database and returns the ids of the database and of its creation job.</summary>
    public static async Task<(Guid DatabaseId, Guid JobId)> CreateDatabaseAsync(this HttpClient client, Guid instanceId, string name)
    {
        var response = await client.PostAsJsonAsync(InstanceDatabasesUrl(instanceId), new { name });
        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        return (body.GetProperty("database").GetProperty("id").GetGuid(), body.GetProperty("job").GetProperty("id").GetGuid());
    }

    /// <summary>Requests the deletion of a database and returns the id of its deletion job.</summary>
    public static async Task<Guid> DeleteDatabaseAsync(this HttpClient client, Guid databaseId)
    {
        var response = await client.DeleteAsync($"{DatabasesUrl}/{databaseId}");
        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        return body.GetProperty("job").GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement> GetDatabaseAsync(this HttpClient client, Guid id) =>
        await (await client.GetAsync($"{DatabasesUrl}/{id}")).ReadJsonAsync(HttpStatusCode.OK);

    /// <summary>Requests a backup and returns the ids of the backup and of its job.</summary>
    public static async Task<(Guid BackupId, Guid JobId)> CreateBackupAsync(this HttpClient client, Guid databaseId)
    {
        var response = await client.PostAsync(DatabaseBackupsUrl(databaseId), content: null);
        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        return (body.GetProperty("backup").GetProperty("id").GetGuid(), body.GetProperty("job").GetProperty("id").GetGuid());
    }

    public static async Task<JsonElement> GetBackupAsync(this HttpClient client, Guid id) =>
        await (await client.GetAsync($"{BackupsUrl}/{id}")).ReadJsonAsync(HttpStatusCode.OK);

    public static async Task<JsonElement> GetJobAsync(this HttpClient client, Guid id) =>
        await (await client.GetAsync($"{JobsUrl}/{id}")).ReadJsonAsync(HttpStatusCode.OK);

    /// <summary>Polls a job until the background worker has brought it to <c>completed</c> or <c>failed</c>.</summary>
    public static async Task<JsonElement> WaitForFinishedJobAsync(this HttpClient client, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var job = await client.GetJobAsync(id);
            if (job.Status() is "completed" or "failed")
            {
                return job;
            }

            if (timeout.IsCancellationRequested)
            {
                Assert.Fail($"Job {id} did not finish in time; status is '{job.Status()}'.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    public static string? Status(this JsonElement element) => element.GetProperty("status").GetString();

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response, HttpStatusCode? expectedStatus = null)
    {
        if (expectedStatus is not null)
        {
            Assert.Equal(expectedStatus, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<JsonElement> AssertErrorAsync(
        this HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedCode)
    {
        var error = (await response.ReadJsonAsync(expectedStatus)).GetProperty("error");
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        return error;
    }
}
