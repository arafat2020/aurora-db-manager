using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.BackupSchedules;
using AuroraDbManager.Api.Application.Credentials;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Infrastructure.Backups;
using AuroraDbManager.Api.Infrastructure.Backups.S3;
using AuroraDbManager.Api.Infrastructure.Docker;
using AuroraDbManager.Api.Infrastructure.Persistence;
using AuroraDbManager.Api.Infrastructure.Secrets;
using AuroraDbManager.Api.Tests.Backups;
using AuroraDbManager.Api.Tests.Credentials;
using AuroraDbManager.Api.Tests.Databases;
using AuroraDbManager.Api.Tests.Docker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuroraDbManager.Api.Tests;

/// <summary>
/// Hosts the API in-process with the system database swapped for a private SQLite file, the
/// provisioner swapped for a <see cref="FakeInstanceProvisioner"/> and the database managers
/// swapped for those of <see cref="FakeDatabaseServers"/>, so tests need neither a PostgreSQL
/// server nor Docker. Backups run the real backup managers and the real local storage, on a
/// private directory, with the dump programs swapped for <see cref="FakeDumpTools"/>. A file rather than an in-memory database, because the job worker
/// and the requests use the database concurrently and each needs its own connection.
/// </summary>
public sealed class ApiFactory : TestHostFactory<Program>;

/// <summary>
/// The UI host, <c>AuroraDbManager.Web</c>, in-process with the same replacements as
/// <see cref="ApiFactory"/>: the Razor Pages UI, and the REST API it serves alongside.
/// </summary>
public sealed class WebFactory : TestHostFactory<AuroraDbManager.Web.WebProgram>;

/// <summary>What <see cref="ApiFactory"/> and <see cref="WebFactory"/> have in common: everything but the host.</summary>
public class TestHostFactory<TProgram> : WebApplicationFactory<TProgram>
    where TProgram : class
{
    private static readonly EphemeralDataProtectionProvider DataProtection = new();

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

    /// <summary>
    /// The key this host signs and validates access tokens with. Generated for the test run, as
    /// a deployment's is generated for the deployment; there is no key in the application to fall back to.
    /// </summary>
    public static readonly string SigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

    /// <summary>
    /// The first administrator to create at startup, the way <c>Authentication:BootstrapAdmin</c>
    /// does in a deployment. None by default: most tests need a token, not a user.
    /// </summary>
    public (string Username, string Password)? BootstrapAdmin { get; init; }

    /// <summary>Changes the security settings after the test defaults were applied.</summary>
    public Action<Infrastructure.Security.SecurityOptions>? ConfigureSecurity { get; init; }

    /// <summary>
    /// The header a test names the address its request "arrives from" with. The test server has
    /// no sockets, so there is no peer address unless a test says what it is; this stands in for
    /// the TCP connection, which no client can choose, and is not something the application reads.
    /// </summary>
    public const string RemoteAddressHeader = "X-Test-Connection-Address";

    /// <summary>Host settings, as environment variables with the ASPNETCORE_ prefix would set them.</summary>
    public IReadOnlyDictionary<string, string> HostSettings { get; init; } = new Dictionary<string, string>();

    /// <summary>The environment the application runs in. Development by default, as the test host makes it.</summary>
    public string? EnvironmentName { get; init; }

    /// <summary>Changes the authentication settings after the test defaults were applied.</summary>
    public Action<AuthOptions>? ConfigureAuth { get; init; }

    /// <summary>
    /// An access token for a user of the given role: a real JWT, signed with this host's key and
    /// validated by the application's real authentication like any other. It is issued here
    /// rather than by signing in so that tests of everything else need no user and no password
    /// hashing; signing in itself is tested through the login endpoint. It does not expire within
    /// a test, however far a test moves the application's clock.
    /// </summary>
    public static string TokenFor(UserRole role, Guid? userId = null, string? username = null) =>
        new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = new JwtOptions().Issuer,
            Audience = new JwtOptions().Audience,
            NotBefore = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IssuedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Expires = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Claims = new Dictionary<string, object>
            {
                [AuroraPolicies.SubjectClaim] = (userId ?? Guid.NewGuid()).ToString("D"),
                [AuroraPolicies.NameClaim] = username ?? $"test-{AuroraPolicies.RoleName(role)}",
                [AuroraPolicies.RoleClaim] = AuroraPolicies.RoleName(role)
            },
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                TokenService.SigningKey(new JwtOptions { SigningKey = SigningKey }),
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256)
        });

    /// <summary>A client that sends the token of a user of the given role with every request.</summary>
    public HttpClient CreateClientAs(UserRole role)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TokenFor(role));
        return client;
    }

    /// <summary>A client that sends no token.</summary>
    public HttpClient CreateAnonymousClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }

    /// <summary>
    /// Every client is an administrator's unless a test says otherwise: the tests of what the
    /// application does are not tests of who may do it. Those are in the Security tests, which
    /// use <see cref="CreateClientAs"/>, <see cref="CreateAnonymousClient"/> and real sign-ins.
    /// </summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TokenFor(UserRole.Admin));
    }

    public FakeInstanceProvisioner Provisioner { get; } = new();

    public FakeDatabaseServers DatabaseServers { get; } = new();

    /// <summary>What every instance's database server accepts as its administrator's password. Unused with real Docker.</summary>
    public FakeAdminCredentials AdminCredentials { get; } = new();

    /// <summary>The real secret store's faults, for a test to inject.</summary>
    public SecretStoreFaults SecretFaults { get; } = new();

    public FakeDumpTools DumpTools { get; } = new();

    public FakeInstanceEndpoints Endpoints { get; } = new();

    /// <summary>The real artifact hasher, which a test can make fail or hold.</summary>
    public FaultInjectingHasher Hasher { get; } = new();

    /// <summary>
    /// The object store behind S3 backup storage. Unused unless a backup is in that storage. Can
    /// be given, so a second factory finds the objects a first one stored, as a restarted
    /// application finds its bucket.
    /// </summary>
    public FakeS3ObjectStore ObjectStore { get; init; } = new();

    /// <summary>
    /// The databases the restore managers connect to, to empty and to verify them: every statement
    /// is recorded and nothing is interpreted.
    /// </summary>
    public FakeSqlServer RestoreSql { get; } = new('"', permissive: true);

    /// <summary>The local directory restores stage artifacts in. Deleted with the factory.</summary>
    public string RestoreStagingRoot { get; } =
        Path.Combine(Path.GetTempPath(), $"aurora-db-manager-tests-restore-{Guid.NewGuid():N}");

    /// <summary>Everything in the directory restores stage artifacts in: files and directories.</summary>
    public IReadOnlyList<string> RestoreStagingEntries() =>
        Directory.Exists(RestoreStagingRoot) ? Directory.GetFileSystemEntries(RestoreStagingRoot, "*", SearchOption.AllDirectories) : [];

    /// <summary>Requests a restore and returns the id of its job.</summary>
    public static async Task<Guid> RequestRestoreAsync(HttpClient client, Guid backupId)
    {
        var response = await client.PostAsync($"{ApiClientExtensions.BackupsUrl}/{backupId}/restore", content: null);
        var body = await response.ReadJsonAsync(System.Net.HttpStatusCode.Accepted);
        return body.GetProperty("job").GetProperty("id").GetGuid();
    }

    /// <summary>The local directory S3 backup storage stages backups in. Deleted with the factory.</summary>
    public string StagingRoot { get; } =
        Path.Combine(Path.GetTempPath(), $"aurora-db-manager-tests-staging-{Guid.NewGuid():N}");

    /// <summary>Everything the application logged.</summary>
    public RecordingLoggerProvider Logs { get; } = new();

    /// <summary>
    /// A backup root directory to use instead of a private one. Like <see cref="DatabasePath"/>
    /// it is not deleted with the factory, so a restarted application finds what the first left.
    /// </summary>
    public string? BackupRootPath { get; init; }

    /// <summary>Changes the external-access settings: the bind address, the advertised host, the port range.</summary>
    public Action<Application.Connectivity.ExternalAccessOptions>? ConfigureExternalAccess { get; init; }

    /// <summary>Changes the Docker settings after the test defaults were applied, the readiness timeout for instance.</summary>
    public Action<DockerOptions>? ConfigureDocker { get; init; }

    /// <summary>For how long a rotated password can be retrieved; the application's default if not set.</summary>
    public int? CredentialResultTtlMinutes { get; init; }

    /// <summary>Changes the backup settings after the test defaults were applied.</summary>
    public Action<BackupOptions>? ConfigureBackups { get; init; }

    private readonly string _ownBackupRoot =
        Path.Combine(Path.GetTempPath(), $"aurora-db-manager-tests-backups-{Guid.NewGuid():N}");

    public string BackupRoot => BackupRootPath ?? _ownBackupRoot;

    /// <summary>The path the local storage keeps a backup's artifact at.</summary>
    public string BackupFilePath(Guid instanceId, Guid databaseId, Guid backupId, string extension) =>
        Services.GetRequiredService<LocalBackupStorage>()
            .PathFor(new BackupLocation(instanceId, databaseId, backupId, extension));

    /// <summary>The key the S3 storage keeps a backup's artifact under.</summary>
    public string BackupObjectKey(Guid instanceId, Guid databaseId, Guid backupId, string extension) =>
        Services.GetRequiredService<S3BackupStorage>()
            .KeyFor(new BackupLocation(instanceId, databaseId, backupId, extension));

    /// <summary>Every file in the directory backups are staged in before they are uploaded.</summary>
    public IReadOnlyList<string> StagingFiles() =>
        Directory.Exists(StagingRoot) ? Directory.GetFiles(StagingRoot, "*", SearchOption.AllDirectories) : [];

    /// <summary>Every file under the backup root, finished or not.</summary>
    public IReadOnlyList<string> BackupFiles() =>
        Directory.Exists(BackupRoot) ? Directory.GetFiles(BackupRoot, "*", SearchOption.AllDirectories) : [];

    /// <summary>The administrator password of an instance, as the managers get it.</summary>
    public async Task<string> AdminPasswordAsync(Guid instanceId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IInstanceSecretStore>()
            .GetOrCreateAdminPasswordAsync(instanceId, default);
    }

    public FakeDockerEngine Docker { get; } = new();

    /// <summary>Processes a job the way the worker would, on the calling test's own schedule.</summary>
    public async Task ProcessJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAsync(jobId, cancellationToken);
    }

    /// <summary>One pass of the backup scheduler, the way its worker makes one at every interval.</summary>
    /// <returns>The ids of the backup jobs the pass created.</returns>
    public async Task<IReadOnlyList<Guid>> RunSchedulerAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BackupScheduler>().RunDueAsync(cancellationToken);
    }

    /// <summary>
    /// Listens to the application's own meter, this host's and no other's: hosts of other tests
    /// in the same process have meters of their own, and their measurements never arrive here.
    /// </summary>
    public MetricsRecorder RecordMetrics() => new(Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());

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
        // The real provisioner gives the server a password from the secret store; the fake one
        // creates no server, so the password it would have been given is put there here.
        await AdminPasswordAsync(instanceId);
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

    /// <summary>Requests a backup and runs its job, leaving it <c>completed</c>.</summary>
    public async Task<Guid> CreateCompletedBackupAsync(HttpClient client, Guid databaseId)
    {
        var (backupId, jobId) = await client.CreateBackupAsync(databaseId);
        await ProcessJobAsync(jobId);
        Assert.Equal("completed", (await client.GetBackupAsync(backupId)).Status());
        return backupId;
    }

    // No API moves an instance to "stopped" or back yet, so the status is written directly.
    public Task SetInstanceStatusAsync(Guid instanceId, InstanceStatus status) =>
        WithDbAsync(db => db.Instances
            .Where(i => i.Id == instanceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(i => i.Status, status)));

    /// <summary>The replacement password a rotation has stored for an instance, or null.</summary>
    public async Task<string?> AdminPasswordReplacementAsync(Guid instanceId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IInstanceSecretStore>()
            .GetAdminPasswordReplacementAsync(instanceId, default);
    }

    /// <summary>Asks for a rotation of an instance's administrator password and returns the id of its job.</summary>
    public static async Task<Guid> RequestRotationAsync(HttpClient client, Guid instanceId)
    {
        var response = await client.PostAsync($"{ApiClientExtensions.InstancesUrl}/{instanceId}/credentials/rotate", content: null);
        var body = await response.ReadJsonAsync(System.Net.HttpStatusCode.Accepted);
        return body.GetProperty("job").GetProperty("id").GetGuid();
    }

    /// <summary>The address the new password of a completed rotation is retrieved from, once.</summary>
    public static string RotationResultUrl(Guid instanceId, Guid jobId) =>
        $"{ApiClientExtensions.InstancesUrl}/{instanceId}/credentials/rotate/{jobId}/result";

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
        if (EnvironmentName is not null)
        {
            builder.UseEnvironment(EnvironmentName);
        }

        foreach (var (key, value) in HostSettings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new ConnectionAddressFilter());
            services.Configure<Infrastructure.Security.SecurityOptions>(options =>
            {
                // Every test client has the same (unknown) address; the limit of a real deployment
                // would have tests that sign in a lot throttle each other. Tests of the limit set their own.
                options.LoginRateLimit.PermitLimit = 10_000;
                ConfigureSecurity?.Invoke(options);
            });

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
            else
            {
                // Never the real engine: an ordinary test must not reach a Docker daemon, not
                // even for the ping of a health check.
                services.RemoveAll<IDockerEngine>();
                services.AddSingleton<IDockerEngine>(Docker);

                if (UseDockerProvisioner)
                {
                    services.Configure<DockerOptions>(options => options.ReadinessPollIntervalMilliseconds = 50);
                }
                else
                {
                    services.RemoveAll<IInstanceProvisioner>();
                    services.AddSingleton<IInstanceProvisioner>(Provisioner);
                }
            }

            if (ConfigureDocker is not null)
            {
                services.Configure(ConfigureDocker);
            }

            if (ConfigureExternalAccess is not null)
            {
                services.Configure(ConfigureExternalAccess);
            }

            services.AddSingleton<ILoggerProvider>(Logs);
            services.RemoveAll<IArtifactHasher>();
            services.AddSingleton<IArtifactHasher>(Hasher);
            services.Configure<BackupOptions>(options =>
            {
                options.Local.RootPath = BackupRoot;
                options.S3.StagingPath = StagingRoot;
                options.Restore.StagingPath = RestoreStagingRoot;
                ConfigureBackups?.Invoke(options);
            });

            if (RealDockerNetwork is null)
            {
                services.RemoveAll<IInstanceEndpointResolver>();
                services.AddSingleton<IInstanceEndpointResolver>(Endpoints);
                services.RemoveAll<IProcessRunner>();
                services.AddSingleton<IProcessRunner>(DumpTools);
                // The restore managers' own connections to the target database.
                services.AddSingleton<Func<string, System.Data.Common.DbConnection>>(RestoreSql.Connect);
                // Never the real client: an ordinary test must not be able to reach a network.
                services.RemoveAll<IS3ObjectClient>();
                services.AddSingleton<IS3ObjectClient>(ObjectStore);

                services.RemoveAll<IDatabaseManager>();
                services.AddSingleton(DatabaseServers.ManagerFor(InstanceEngine.Postgres));
                services.AddSingleton(DatabaseServers.ManagerFor(InstanceEngine.Mysql));

                AdminCredentials.ProvisionedPassword = AdminPasswordAsync;
                services.RemoveAll<IAdminCredentialManager>();
                services.AddSingleton(AdminCredentials.ManagerFor(InstanceEngine.Postgres));
                services.AddSingleton(AdminCredentials.ManagerFor(InstanceEngine.Mysql));
            }

            if (CredentialResultTtlMinutes is { } ttl)
            {
                services.Configure<CredentialOptions>(options => options.ResultTtlMinutes = ttl);
            }

            // The real store, behind something a test can make fail.
            services.RemoveAll<IInstanceSecretStore>();
            services.AddScoped<ProtectedInstanceSecretStore>();
            services.AddScoped<IInstanceSecretStore>(provider => new FaultInjectingSecretStore(
                provider.GetRequiredService<ProtectedInstanceSecretStore>(), SecretFaults));

            if (Clock is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(Clock);
            }

            // Keys that live and die with the test run, instead of a key ring in the user's home
            // directory. One key ring for all factories, as a restarted application has the one
            // it had before: what one factory encrypted, a second one on the same database can read.
            services.RemoveAll<IDataProtectionProvider>();
            services.AddSingleton<IDataProtectionProvider>(DataProtection);

            services.Configure<AuthOptions>(options =>
            {
                options.Jwt.SigningKey = SigningKey;
                options.BootstrapAdmin.Username = BootstrapAdmin?.Username;
                options.BootstrapAdmin.Password = BootstrapAdmin?.Password;
                ConfigureAuth?.Invoke(options);
            });

            services.Configure<JobOptions>(options =>
            {
                options.MaxConcurrency = MaxConcurrency;
                options.MaxAttempts = MaxAttempts;
                options.RetryDelaySeconds = 0;
            });

            // The scheduler never runs by itself in a test: a test lets it pass with
            // RunSchedulerAsync, at a time of the test's choosing.
            services.Remove(services.Single(service =>
                service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(ScheduledBackupWorker)));

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
            foreach (var directory in new[] { _ownBackupRoot, StagingRoot, RestoreStagingRoot })
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }
}

/// <summary>Sets the connection's remote address, before anything of the application runs, to what <see cref="ApiFactory.RemoteAddressHeader"/> says.</summary>
internal sealed class ConnectionAddressFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, following) =>
        {
            if (context.Request.Headers.TryGetValue(ApiFactory.RemoteAddressHeader, out var address))
            {
                context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(address.ToString());
            }

            return following(context);
        });
        next(app);
    };
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
