using System.Net;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Infrastructure.Backups;
using AuroraDbManager.Api.Infrastructure.Docker;
using AuroraDbManager.Api.Infrastructure.Monitoring;
using AuroraDbManager.Api.Infrastructure.Persistence;
using AuroraDbManager.Api.Tests.Backups;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Monitoring;

/// <summary>
/// The health endpoints: liveness, which depends on nothing outside the process; readiness, which
/// reflects the system database and Docker; and the separate check of the S3 backup storage.
/// Docker is the in-memory engine and S3 the in-memory object store, each of which can be made
/// unreachable.
/// </summary>
public sealed class HealthEndpointTests : IDisposable
{
    private const string AccessKey = "AKIAHEALTHTEST000001";
    private const string SecretKey = "health-test-secret-7f3a9c1e";
    private const string Endpoint = "https://s3.internal.example";

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }
    }

    private (ApiFactory Factory, HttpClient Client) Application(bool s3Configured = false, string bucket = FakeS3ObjectStore.Bucket)
    {
        var factory = new ApiFactory
        {
            ConfigureBackups = options =>
            {
                if (s3Configured)
                {
                    options.StorageType = BackupStorageType.S3;
                    options.S3.Bucket = bucket;
                    options.S3.Region = "us-east-1";
                    options.S3.Endpoint = Endpoint;
                    options.S3.AccessKey = AccessKey;
                    options.S3.SecretKey = SecretKey;
                }
            }
        };
        var client = factory.CreateClient();
        _disposables.Add(factory);
        _disposables.Add(client);
        return (factory, client);
    }

    /// <summary>The application on a system database that cannot be opened.</summary>
    private HttpClient WithUnreachableDatabase(ApiFactory factory)
    {
        var broken = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite($"Data Source={MissingDatabasePath};Mode=ReadWrite;Pooling=False"));
        }));
        var client = broken.CreateClient();
        _disposables.Add(broken);
        _disposables.Add(client);
        return client;
    }

    private static string MissingDatabasePath =>
        Path.Combine(Path.GetTempPath(), $"aurora-no-such-directory-{Guid.NewGuid():N}", "system.db");

    private static Dictionary<string, string?> Checks(System.Text.Json.JsonElement body) =>
        body.GetProperty("checks").EnumerateObject().ToDictionary(check => check.Name, check => check.Value.GetString());

    // --- Liveness -----------------------------------------------------------------------------

    [Fact]
    public async Task Liveness_ProcessIsServing_IsHealthy_AndRunsNoCheck()
    {
        var (factory, client) = Application();

        var body = await (await client.GetAsync(HealthUrl)).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal("healthy", body.Status());
        Assert.Empty(Checks(body));
        // Nothing outside the process was asked.
        Assert.Empty(factory.Docker.Calls);
    }

    [Fact]
    public async Task Liveness_StaysHealthy_WhenDockerS3AndTheSystemDatabaseAreAllUnavailable()
    {
        var (factory, _) = Application(s3Configured: true);
        factory.Docker.Unavailable = true;
        factory.ObjectStore.Unavailable = true;
        var client = WithUnreachableDatabase(factory);

        var body = await (await client.GetAsync(HealthUrl)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("healthy", body.Status());

        // The same application is not ready, so the two endpoints do answer different questions.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(ReadinessUrl)).StatusCode);
    }

    // --- Readiness ----------------------------------------------------------------------------

    [Fact]
    public async Task Readiness_SystemDatabaseAndDockerReachable_IsHealthy()
    {
        var (_, client) = Application();

        var response = await client.GetAsync(ReadinessUrl);

        var body = await response.ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("healthy", body.Status());
        Assert.Equal(
            new Dictionary<string, string?> { ["metadataDatabase"] = "healthy", ["docker"] = "healthy" },
            Checks(body));
        Assert.Equal(["checks", "status"], body.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task Readiness_ChecksDockerWithOnePing_AndInspectsNoContainer()
    {
        var (factory, client) = Application();
        await factory.CreateRunningInstanceAsync(client);
        factory.Docker.Calls.Clear();

        await client.GetAsync(ReadinessUrl);

        Assert.Equal([$"{nameof(IDockerEngine.PingAsync)} "], factory.Docker.Calls);
    }

    [Fact]
    public async Task Readiness_DockerUnavailable_IsDegraded_NotUnavailable_AndTellsNothingAboutTheDaemon()
    {
        var (factory, client) = Application();
        factory.Docker.Unavailable = true;

        var response = await client.GetAsync(ReadinessUrl);

        // Reads, job status and monitoring still work without Docker: degraded, still 200.
        var body = await response.ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("degraded", body.Status());
        Assert.Equal("degraded", Checks(body)["docker"]);
        Assert.Equal("healthy", Checks(body)["metadataDatabase"]);

        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("docker.sock", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-daemon-detail", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_SystemDatabaseUnavailable_IsUnhealthy_AndTellsNothingAboutTheDatabase()
    {
        var (factory, _) = Application();
        var client = WithUnreachableDatabase(factory);

        var response = await client.GetAsync(ReadinessUrl);

        var body = await response.ReadJsonAsync(HttpStatusCode.ServiceUnavailable);
        Assert.Equal("unhealthy", body.Status());
        Assert.Equal("unhealthy", Checks(body)["metadataDatabase"]);
        Assert.Equal("healthy", Checks(body)["docker"]);

        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Data Source", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("aurora-no-such-directory", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SQLite", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_DoesNotDependOnS3_EvenWhenBackupsGoThere()
    {
        var (factory, client) = Application(s3Configured: true);
        factory.ObjectStore.Unavailable = true;

        var body = await (await client.GetAsync(ReadinessUrl)).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal("healthy", body.Status());
        Assert.DoesNotContain("s3", Checks(body).Keys);
    }

    // --- The system database check itself -----------------------------------------------------

    [Fact]
    public async Task MetadataDatabaseCheck_ReachableDatabase_IsHealthy()
    {
        var (factory, _) = Application();

        var result = await factory.WithDbAsync(db => new MetadataDatabaseHealthCheck(db).CheckHealthAsync(CheckContext()));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task MetadataDatabaseCheck_UnreachableDatabase_IsUnhealthy_WithASafeDescription()
    {
        var path = MissingDatabasePath;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Mode=ReadWrite;Pooling=False").Options);

        var result = await new MetadataDatabaseHealthCheck(db).CheckHealthAsync(CheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("The system database cannot be reached.", result.Description);
        // Kept for the log, where the framework writes it; the endpoint never returns it.
        Assert.NotNull(result.Exception);
        Assert.False(File.Exists(path));
    }

    private static HealthCheckContext CheckContext() => new()
    {
        Registration = new HealthCheckRegistration("metadataDatabase", _ => null!, HealthStatus.Unhealthy, tags: null)
    };

    // --- S3 -----------------------------------------------------------------------------------

    [Fact]
    public async Task Storage_S3NotConfigured_IsNotApplicable_NotUnhealthy()
    {
        var (_, client) = Application();

        var body = await (await client.GetAsync(StorageHealthUrl)).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal("healthy", body.Status());
        Assert.Equal(new Dictionary<string, string?> { ["s3"] = "not_applicable" }, Checks(body));
    }

    [Fact]
    public async Task Storage_S3ConfiguredAndReachable_IsHealthy_WithoutTouchingAnyObject()
    {
        var (factory, client) = Application(s3Configured: true);

        var body = await (await client.GetAsync(StorageHealthUrl)).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal("healthy", body.Status());
        Assert.Equal("healthy", Checks(body)["s3"]);
        Assert.Equal(0, factory.ObjectStore.UploadCount);
        Assert.Empty(factory.ObjectStore.Downloads);
        Assert.Equal(0, factory.ObjectStore.Reads);
        Assert.Empty(factory.ObjectStore.ObjectsIn());
    }

    [Fact]
    public async Task Storage_S3ConfiguredButUnreachable_IsUnhealthy_AndTellsNothingAboutTheStore()
    {
        var (factory, client) = Application(s3Configured: true);
        factory.ObjectStore.Unavailable = true;

        var response = await client.GetAsync(StorageHealthUrl);

        var body = await response.ReadJsonAsync(HttpStatusCode.ServiceUnavailable);
        Assert.Equal("unhealthy", body.Status());
        Assert.Equal("unhealthy", Checks(body)["s3"]);

        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("raw-sdk-detail", text, StringComparison.Ordinal);
        Assert.DoesNotContain("s3.internal.example", text, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeS3ObjectStore.Bucket, text, StringComparison.Ordinal);

        // Neither liveness nor readiness is affected.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(HealthUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ReadinessUrl)).StatusCode);
    }

    [Fact]
    public async Task Storage_S3BucketMissing_IsUnhealthy()
    {
        var (_, client) = Application(s3Configured: true, bucket: "no-such-bucket");

        var body = await (await client.GetAsync(StorageHealthUrl)).ReadJsonAsync(HttpStatusCode.ServiceUnavailable);

        Assert.Equal("unhealthy", Checks(body)["s3"]);
    }

    [Fact]
    public async Task HealthLogs_NeverContainTheS3Credentials()
    {
        var (factory, client) = Application(s3Configured: true);
        factory.ObjectStore.Unavailable = true;
        factory.Docker.Unavailable = true;

        await client.GetAsync(StorageHealthUrl);
        await client.GetAsync(ReadinessUrl);

        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(SecretKey, StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(AccessKey, StringComparison.Ordinal));
    }
}
