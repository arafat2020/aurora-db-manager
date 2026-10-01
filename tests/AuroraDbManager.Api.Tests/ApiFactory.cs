using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
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
/// Hosts the API in-process with the system database swapped for a private SQLite file and the
/// provisioner swapped for a <see cref="FakeInstanceProvisioner"/>, so tests need neither a
/// PostgreSQL server nor Docker. A file rather than an in-memory database, because the job worker and the requests
/// use the database concurrently and each needs its own connection.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"aurora-db-manager-tests-{Guid.NewGuid():N}.db");

    /// <summary>
    /// When false the background worker is not started: created jobs stay pending until a test
    /// runs them with <see cref="ProcessJobAsync"/>.
    /// </summary>
    public bool RunWorker { get; init; }

    public int MaxConcurrency { get; init; } = 2;

    public int MaxAttempts { get; init; } = 3;

    public FakeInstanceProvisioner Provisioner { get; } = new();

    /// <summary>Processes a job the way the worker would, on the calling test's own schedule.</summary>
    public async Task ProcessJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAsync(jobId, cancellationToken);
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite($"Data Source={_databasePath};Pooling=False"));

            services.RemoveAll<IInstanceProvisioner>();
            services.AddSingleton<IInstanceProvisioner>(Provisioner);

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
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                File.Delete(_databasePath + suffix);
            }
        }
    }
}
