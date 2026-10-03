using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Docker;
using AuroraDbManager.Api.Infrastructure.Persistence;
using AuroraDbManager.Api.Tests.Databases;
using AuroraDbManager.Api.Tests.Docker;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AuroraDbManager.Api.Tests;

/// <summary>
/// Hosts the API in-process with the system database swapped for a private SQLite file, the
/// provisioner swapped for a <see cref="FakeInstanceProvisioner"/> and the database managers
/// swapped for those of <see cref="FakeDatabaseServers"/>, so tests need neither a PostgreSQL
/// server nor Docker. A file rather than an in-memory database, because the job worker
/// and the requests use the database concurrently and each needs its own connection.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _ownDatabasePath =
        Path.Combine(Path.GetTempPath(), $"aurora-db-manager-tests-{Guid.NewGuid():N}.db");

    /// <summary>
    /// When false the background worker is not started: created jobs stay pending until a test
    /// runs them with <see cref="ProcessJobAsync"/>, and no startup recovery takes place.
    /// </summary>
    public bool RunWorker { get; init; }

    public int MaxConcurrency { get; init; } = 2;

    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// A database file to use instead of a private one. It is not deleted with the factory, so a
    /// second factory can be started on it, the way the application is restarted on its database.
    /// </summary>
    public string? DatabasePath { get; init; }

    /// <summary>A clock to use instead of the system clock, for tests that move time themselves.</summary>
    public TimeProvider? Clock { get; init; }

    /// <summary>
    /// When true the real Docker provisioner is used, on top of <see cref="Docker"/>, instead of
    /// <see cref="Provisioner"/>.
    /// </summary>
    public bool UseDockerProvisioner { get; init; }

    /// <summary>
    /// When set, nothing Docker-related is swapped: instances are real containers on this Docker
    /// network and databases are managed in them by the real managers. For opt-in integration tests.
    /// </summary>
    public string? RealDockerNetwork { get; init; }

    public FakeInstanceProvisioner Provisioner { get; } = new();

    public FakeDatabaseServers DatabaseServers { get; } = new();

    public FakeDockerEngine Docker { get; } = new();

    /// <summary>Processes a job the way the worker would, on the calling test's own schedule.</summary>
    public async Task ProcessJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAsync(jobId, cancellationToken);
    }

    /// <summary>Runs job recovery the way the worker does at startup or at its periodic check.</summary>
    public async Task<IReadOnlyList<Guid>> RecoverJobsAsync(bool includePending)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobRecovery>().RecoverAsync(includePending, default);
    }

    public Task<ReconciliationReport> ReconcileAsync() =>
        Services.GetRequiredService<InstanceReconciler>().ReconcileAsync(default);

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>Creates an instance and runs its provisioning job, leaving it <c>running</c>.</summary>
    public async Task<Guid> CreateRunningInstanceAsync(HttpClient client, string name = "production-db", string engine = "postgres")
    {
        var (instanceId, jobId) = await client.CreateInstanceAsync(name: name, engine: engine);
        await ProcessJobAsync(jobId);
        Assert.Equal("running", (await client.GetInstanceAsync(instanceId)).Status());
        return instanceId;
    }

    /// <summary>Creates a database and runs its job, leaving it <c>ready</c>.</summary>
    public async Task<Guid> CreateReadyDatabaseAsync(HttpClient client, Guid instanceId, string name)
    {
        var (databaseId, jobId) = await client.CreateDatabaseAsync(instanceId, name);
        await ProcessJobAsync(jobId);
        Assert.Equal("ready", (await client.GetDatabaseAsync(databaseId)).Status());
        return databaseId;
    }

    // No API moves an instance to "stopped" or back yet, so the status is written directly.
    public Task SetInstanceStatusAsync(Guid instanceId, InstanceStatus status) =>
        WithDbAsync(db => db.Instances
            .Where(i => i.Id == instanceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(i => i.Status, status)));

    public Task<Job> GetJobEntityAsync(Guid jobId) =>
        WithDbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId));

    /// <summary>
    /// Puts a pending job into the state a dead execution leaves behind: running, under a lease
    /// that expires at <paramref name="leaseExpiresAt"/>.
    /// </summary>
    /// <returns>The lease id of the simulated execution.</returns>
    public Task<Guid> SimulateAbandonedExecutionAsync(Guid jobId, DateTime leaseExpiresAt) =>
        WithDbAsync(async db =>
        {
            var leaseId = Guid.NewGuid();
            var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
            job.Start(leaseId, leaseExpiresAt, DateTime.UtcNow);
            await db.SaveChangesAsync();
            return leaseId;
        });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite($"Data Source={DatabasePath ?? _ownDatabasePath};Pooling=False"));

            if (RealDockerNetwork is not null)
            {
                services.Configure<DockerOptions>(options =>
                {
                    options.NetworkName = RealDockerNetwork;
                    options.ReadinessTimeoutSeconds = 180;
                    options.ReadinessPollIntervalMilliseconds = 500;
                });
            }
            else if (UseDockerProvisioner)
            {
                services.RemoveAll<IDockerEngine>();
                services.AddSingleton<IDockerEngine>(Docker);
                services.Configure<DockerOptions>(options => options.ReadinessPollIntervalMilliseconds = 50);
            }
            else
            {
                services.RemoveAll<IInstanceProvisioner>();
                services.AddSingleton<IInstanceProvisioner>(Provisioner);
            }

            if (RealDockerNetwork is null)
            {
                services.RemoveAll<IDatabaseManager>();
                services.AddSingleton(DatabaseServers.ManagerFor(InstanceEngine.Postgres));
                services.AddSingleton(DatabaseServers.ManagerFor(InstanceEngine.Mysql));
            }

            if (Clock is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(Clock);
            }

            // Keys that live and die with the test, instead of a key ring in the user's home directory.
            services.RemoveAll<IDataProtectionProvider>();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());

            services.Configure<JobOptions>(options =>
            {
                options.MaxConcurrency = MaxConcurrency;
                options.MaxAttempts = MaxAttempts;
                options.RetryDelaySeconds = 0;
            });

            if (!RunWorker)
            {
                var worker = services.Single(service =>
                    service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(JobWorker));
                services.Remove(worker);
            }

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            TempDatabase.Delete(_ownDatabasePath);
        }
    }
}

/// <summary>A database file that outlives the factories using it, for tests that restart the application.</summary>
public sealed class TempDatabase : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aurora-db-manager-tests-{Guid.NewGuid():N}.db");

    public void Dispose() => Delete(Path);

    public static void Delete(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(path + suffix);
        }
    }
}
