using System.Net;
using System.Runtime.InteropServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Infrastructure.Backups.S3;
using AuroraDbManager.Api.Infrastructure.Docker;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Integration;

/// <summary>
/// Backups to S3-compatible object storage, against LocalStack: the whole application with the
/// real backup managers, the real dump programs, the real S3 storage and the AWS SDK's own
/// client. Opt-in: see <see cref="DockerFactAttribute"/>. Each test starts its own LocalStack
/// container on its own network and removes everything it created.
/// </summary>
/// <remarks>
/// LocalStack is only the test's S3 endpoint. The application is given an endpoint, a bucket, a
/// region and credentials through its ordinary configuration, exactly as it would be for any
/// other S3-compatible service, and uses nothing but standard S3 requests. What is in the bucket
/// is checked with a separate SDK client of the test's own. Run with
/// <c>tests/run-docker-integration-tests.sh</c>.
/// </remarks>
[Trait("Category", "DockerIntegration")]
[Collection(BackupIntegrationCollection.Name)]
public sealed class S3BackupIntegrationTests : IAsyncLifetime
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable(DockerFactAttribute.EnableVariable) == "1";
    private static readonly bool InContainer = File.Exists("/.dockerenv");
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddHours(-1);

    // A release from before LocalStack's images began to require an account; S3 needs none here.
    private static readonly string LocalStackImage =
        Environment.GetEnvironmentVariable("AURORA_LOCALSTACK_IMAGE") is { Length: > 0 } image ? image : "localstack/localstack:4.4.0";

    private const string Bucket = "aurora-it-backups";
    private const string Region = "us-east-1";

    // LocalStack accepts any credentials; these are distinctive so a leak would be recognisable.
    private const string AccessKey = "AKIAAURORAITEST00001";
    private const string SecretKey = "aurora-it-secret-3b9d5f7a1c";
    private const string Psql = "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -v ON_ERROR_STOP=1";
    private const string Mysql = "mysql -h 127.0.0.1 -uroot -p\"$MYSQL_ROOT_PASSWORD\"";

    private readonly string _network = $"aurora-db-test-{Guid.NewGuid():N}";
    private readonly string _localStackName = $"aurora-localstack-test-{Guid.NewGuid():N}";
    private readonly List<Guid> _instanceIds = [];
    private readonly List<ApiFactory> _factories = [];
    private DockerEngine _engine = null!;
    private DockerClient _docker = null!;
    private AmazonS3Client _s3 = null!;
    private string _endpoint = null!;
    private string _localStackAddress = null!;
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        if (!InContainer && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new InvalidOperationException(
                "Neither instance containers nor LocalStack publish ports, and on Docker Desktop their network is not reachable from the host. "
                + "Run these tests with tests/run-docker-integration-tests.sh, which runs them in a container.");
        }

        var options = new DockerOptions { NetworkName = _network };
        _engine = new DockerEngine(Options.Create(options));
        _docker = DockerClientFactory.Create(options);

        await _engine.CreateNetworkAsync(_network, DockerResourceNaming.NetworkLabels(), default);
        if (InContainer)
        {
            await _docker.Networks.ConnectNetworkAsync(_network, new NetworkConnectParameters { Container = Environment.MachineName });
        }

        await StartLocalStackAsync();

        _factory = S3Factory();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        if (!Enabled || _engine is null)
        {
            return;
        }

        _client?.Dispose();
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        _s3?.Dispose();

        foreach (var instanceId in _instanceIds)
        {
            await _engine.RemoveContainerAsync(DockerResourceNaming.ContainerName(instanceId), default);
            await _engine.RemoveVolumeAsync(DockerResourceNaming.VolumeName(instanceId), default);
        }

        await _engine.RemoveContainerAsync(_localStackName, default);

        if (InContainer)
        {
            await _docker.Networks.DisconnectNetworkAsync(
                _network, new NetworkDisconnectParameters { Container = Environment.MachineName, Force = true });
        }

        await _docker.Networks.DeleteNetworkAsync(_network);
        _docker.Dispose();
        _engine.Dispose();
    }

    /// <summary>LocalStack with S3 only, on the test's network, with no port published; and the bucket.</summary>
    private async Task StartLocalStackAsync()
    {
        if (!await _engine.ImageExistsAsync(LocalStackImage, default))
        {
            await _engine.PullImageAsync(LocalStackImage, default);
        }

        await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = _localStackName,
            Image = LocalStackImage,
            Env = ["SERVICES=s3"],
            HostConfig = new HostConfig { NetworkMode = _network }
        });
        await _docker.Containers.StartContainerAsync(_localStackName, new ContainerStartParameters());

        var container = await _docker.Containers.InspectContainerAsync(_localStackName);
        _localStackAddress = container.NetworkSettings.Networks[_network].IPAddress;
        _endpoint = $"http://{_localStackAddress}:4566";

        _s3 = new AmazonS3Client(
            new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config { ServiceURL = _endpoint, ForcePathStyle = true, AuthenticationRegion = Region });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        while (true)
        {
            try
            {
                await _s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, timeout.Token);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Still starting.
                await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
            }
        }
    }

    /// <summary>The application, configured for S3 the way a deployment would configure it.</summary>
    private ApiFactory S3Factory(string? endpoint = null, string bucket = Bucket)
    {
        var factory = new ApiFactory
        {
            RealDockerNetwork = _network,
            ConfigureBackups = options =>
            {
                options.StorageType = BackupStorageType.S3;
                options.S3.Bucket = bucket;
                options.S3.Region = Region;
                options.S3.Endpoint = endpoint ?? _endpoint;
                options.S3.AccessKey = AccessKey;
                options.S3.SecretKey = SecretKey;
                options.S3.PathStyle = true;
            }
        };
        _factories.Add(factory);
        return factory;
    }

    // --- Connectivity and bucket --------------------------------------------------------------

    [DockerFact]
    public async Task Application_ReachesTheConfiguredEndpoint_AndItsBucket_WithTheRealSdkClient()
    {
        var storage = _factory.Services.GetRequiredService<IBackupStorage>();
        Assert.IsType<S3BackupStorage>(storage);
        Assert.IsType<AwsS3ObjectClient>(_factory.Services.GetRequiredService<IS3ObjectClient>());

        // A signed request the store answers: no such object yet.
        Assert.Null(await storage.FindAsync(new BackupLocation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "dump"), default));

        // And one that stores and finds an object, through the storage alone.
        var location = new BackupLocation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "dump");
        await using (var staging = await storage.BeginAsync(location, default))
        {
            await File.WriteAllTextAsync(staging.FilePath, "connectivity check");
            var artifact = await staging.CommitAsync(default);
            Assert.Equal(BackupStorageType.S3, artifact.StorageType);
            Assert.Equal(18, artifact.SizeBytes);
        }

        Assert.Equal(18, (await storage.FindAsync(location, default))!.SizeBytes);
        Assert.Single(await ObjectKeysAsync());
    }

    [DockerFact]
    public async Task BucketDoesNotExist_BackupFailsAsBucketNotFound_AndIsNotCompleted()
    {
        var factory = S3Factory(bucket: "aurora-it-no-such-bucket");
        using var client = factory.CreateClient();
        var (_, databaseId) = await CreateDatabaseAsync(factory, client, "postgres", "16", "shop");

        var (backupId, jobId) = await client.CreateBackupAsync(databaseId);
        await factory.ProcessJobAsync(jobId);

        var job = await client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        var backup = await client.GetBackupAsync(backupId);
        Assert.Equal("failed", backup.Status());
        Assert.Equal("BACKUP_STORAGE_BUCKET_NOT_FOUND", backup.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("aurora-it-no-such-bucket", backup.GetRawText() + job.GetRawText());
        Assert.Empty(factory.StagingFiles());
        Assert.Empty(await ObjectKeysAsync());
    }

    // --- Backups ------------------------------------------------------------------------------

    [DockerFact]
    public async Task Postgres_Backup_IsStoredAsAnObject_UnderItsKey_WithItsSizeAndContentType()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync(_factory, _client, "postgres", "16", "shop");
        await ExecAsync(instanceId, $"{Psql} -d shop -c \"create table customers (id int primary key, name text); insert into customers values (1, 'alice'), (2, 'bob');\"");

        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(_client, jobId);
        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("completed", backup.Status());
        Assert.Equal("s3", backup.GetProperty("storageType").GetString());

        var key = $"backups/instances/{instanceId:D}/databases/{databaseId:D}/{backupId:D}.dump";
        Assert.Equal([key], await ObjectKeysAsync());
        var stored = await _s3.GetObjectMetadataAsync(Bucket, key);
        Assert.Equal(backup.GetProperty("sizeBytes").GetInt64(), stored.ContentLength);
        Assert.Equal("application/octet-stream", stored.Headers.ContentType);
        Assert.Equal(backupId.ToString("D"), stored.Metadata["aurora-backup-id"]);

        // What is in the bucket is the archive pg_dump wrote, whole.
        var downloaded = Path.Combine(Path.GetTempPath(), $"aurora-it-{Guid.NewGuid():N}.dump");
        try
        {
            using (var response = await _s3.GetObjectAsync(Bucket, key))
            {
                await response.WriteResponseStreamToFileAsync(downloaded, append: false, default);
            }

            Assert.Equal(stored.ContentLength, new FileInfo(downloaded).Length);
            var listing = await RunAsync("pg_restore", "--list", downloaded);
            Assert.Contains("TABLE DATA public customers", listing);
            Assert.Contains("alice", await RunAsync("pg_restore", "--data-only", "--file=-", downloaded));
        }
        finally
        {
            File.Delete(downloaded);
        }

        // The metadata holds the key; nothing stays on the local disk.
        Assert.Equal(key, (await _factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync())).Path);
        Assert.Empty(_factory.StagingFiles());
        Assert.Empty(_factory.BackupFiles());
    }

    [DockerFact]
    public async Task Mysql_Backup_IsStoredAsAnObject_UnderItsKey_WithItsSizeAndContentType()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync(_factory, _client, "mysql", "8.4", "shop");
        await ExecAsync(instanceId, $"{Mysql} shop -e \"create table customers (id int primary key, name varchar(50)); insert into customers values (1, 'alice'), (2, 'bob');\"");

        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(_client, jobId);
        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("completed", backup.Status());
        Assert.Equal("s3", backup.GetProperty("storageType").GetString());

        var key = $"backups/instances/{instanceId:D}/databases/{databaseId:D}/{backupId:D}.sql";
        Assert.Equal([key], await ObjectKeysAsync());
        var stored = await _s3.GetObjectMetadataAsync(Bucket, key);
        Assert.Equal(backup.GetProperty("sizeBytes").GetInt64(), stored.ContentLength);
        Assert.Equal("application/sql", stored.Headers.ContentType);

        using var response = await _s3.GetObjectAsync(Bucket, key);
        using var reader = new StreamReader(response.ResponseStream);
        var dump = await reader.ReadToEndAsync();
        Assert.Contains("CREATE TABLE `customers`", dump);
        Assert.Contains("'alice'", dump);
        Assert.Contains("-- Dump completed", dump);
        Assert.Empty(_factory.StagingFiles());
    }

    [DockerFact]
    public async Task MultipleBackups_EachGetADistinctDeterministicObject_AndEarlierOnesAreNotRewritten()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync(_factory, _client, "postgres", "16", "shop");
        await ExecAsync(instanceId, $"{Psql} -d shop -c \"create table customers (name text); insert into customers values ('alice');\"");

        var first = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var firstKey = $"backups/instances/{instanceId:D}/databases/{databaseId:D}/{first:D}.dump";
        var firstObject = await _s3.GetObjectMetadataAsync(Bucket, firstKey);

        await ExecAsync(instanceId, $"{Psql} -d shop -c \"insert into customers select md5(g::text) from generate_series(1, 5000) g;\"");
        var second = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var third = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        Assert.Equal(
            new[] { first, second, third }.Select(id => $"backups/instances/{instanceId:D}/databases/{databaseId:D}/{id:D}.dump").Order(),
            (await ObjectKeysAsync()).Order());

        var firstAgain = await _s3.GetObjectMetadataAsync(Bucket, firstKey);
        Assert.Equal(firstObject.ETag, firstAgain.ETag);
        Assert.Equal(firstObject.ContentLength, firstAgain.ContentLength);
        // The later backups hold more data, and each reports its own object's size.
        foreach (var id in new[] { second, third })
        {
            var size = (await _client.GetBackupAsync(id)).GetProperty("sizeBytes").GetInt64();
            Assert.True(size > firstObject.ContentLength);
            Assert.Equal(size, (await _s3.GetObjectMetadataAsync(Bucket, $"backups/instances/{instanceId:D}/databases/{databaseId:D}/{id:D}.dump")).ContentLength);
        }
    }

    // --- Recovery -----------------------------------------------------------------------------

    [DockerFact]
    public async Task UploadSucceededButCompletionWasInterrupted_TheJobIsRecovered_AndAdoptsTheObject()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync(_factory, _client, "postgres", "16", "shop");
        await ExecAsync(instanceId, $"{Psql} -d shop -c \"create table customers (name text); insert into customers values ('alice');\"");
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        var key = $"backups/instances/{instanceId:D}/databases/{databaseId:D}/{backupId:D}.dump";

        // An execution claims the job, dumps, uploads and verifies, and dies before it can record
        // that: the job is left running under a lease nobody renews, the backup running.
        await _factory.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
            var backup = await db.Backups.SingleAsync(b => b.Id == backupId);
            var database = await db.Databases.AsNoTracking().SingleAsync(d => d.Id == databaseId);
            var instance = await db.Instances.AsNoTracking().SingleAsync(i => i.Id == instanceId);
            backup.MarkRunning();
            await db.SaveChangesAsync();

            var manager = scope.ServiceProvider.GetServices<IBackupManager>().Single(candidate => candidate.Engine == instance.Engine);
            await manager.BackupAsync(instance, database, backup, default);
        }

        var uploaded = await _s3.GetObjectMetadataAsync(Bucket, key);
        Assert.Equal("running", (await _client.GetBackupAsync(backupId)).Status());

        Assert.Equal([jobId], await _factory.RecoverJobsAsync(includePending: false));
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(_client, jobId);
        var completed = await _client.GetBackupAsync(backupId);
        Assert.Equal("completed", completed.Status());
        Assert.Equal(uploaded.ContentLength, completed.GetProperty("sizeBytes").GetInt64());

        // The same object under the same key, not uploaded a second time.
        Assert.Equal([key], await ObjectKeysAsync());
        var after = await _s3.GetObjectMetadataAsync(Bucket, key);
        Assert.Equal(uploaded.ETag, after.ETag);
        Assert.Equal(uploaded.LastModified, after.LastModified);
        Assert.Contains(_factory.Logs.Entries, entry => entry.Contains("adopted", StringComparison.Ordinal));
        Assert.Empty(_factory.StagingFiles());
    }

    // --- Failure ------------------------------------------------------------------------------

    [DockerFact]
    public async Task EndpointUnavailable_BackupIsNeverCompleted_TheJobUsesItsAttempts_AndNothingLeaks()
    {
        // Nothing listens on this port of the LocalStack container.
        var factory = S3Factory(endpoint: $"http://{_localStackAddress}:4599");
        using var client = factory.CreateClient();
        var (instanceId, databaseId) = await CreateDatabaseAsync(factory, client, "postgres", "16", "shop");

        var (backupId, jobId) = await client.CreateBackupAsync(databaseId);
        await factory.ProcessJobAsync(jobId);

        var job = await client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        var backup = await client.GetBackupAsync(backupId);
        Assert.Equal("failed", backup.Status());
        Assert.Equal("BACKUP_STORAGE_UNAVAILABLE", backup.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("The backup storage is not available.", backup.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, backup.GetProperty("sizeBytes").ValueKind);
        Assert.Null((await factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync())).Path);
        Assert.Empty(await ObjectKeysAsync());
        Assert.Empty(factory.StagingFiles());
        // A failed backup says nothing about the database, and the guards have been lifted again.
        Assert.Equal("ready", (await client.GetDatabaseAsync(databaseId)).Status());

        // Neither the credentials nor the endpoint reach a client, and the secret reaches no log.
        var responses = backup.GetRawText() + job.GetRawText() + await client.GetStringAsync(DatabaseBackupsUrl(databaseId))
            + (await client.GetInstanceAsync(instanceId)).GetRawText();
        foreach (var hidden in new[] { SecretKey, AccessKey, _localStackAddress, ":4599", Bucket })
        {
            Assert.DoesNotContain(hidden, responses);
        }

        Assert.NotEmpty(factory.Logs.Entries);
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(SecretKey, StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(AccessKey, StringComparison.Ordinal));
    }

    [DockerFact]
    public async Task SuccessfulBackup_LeaksNoStorageSecretOrConfiguration()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync(_factory, _client, "postgres", "16", "shop");
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        await _factory.ProcessJobAsync(jobId);
        await AssertJobCompletedAsync(_client, jobId);

        var responses = (await _client.GetBackupAsync(backupId)).GetRawText() + (await _client.GetJobAsync(jobId)).GetRawText()
            + await _client.GetStringAsync(DatabaseBackupsUrl(databaseId)) + (await _client.GetInstanceAsync(instanceId)).GetRawText();
        foreach (var hidden in new[] { SecretKey, AccessKey, _localStackAddress, "4566", Bucket, "backups/instances/" })
        {
            Assert.DoesNotContain(hidden, responses);
        }

        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(SecretKey, StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(AccessKey, StringComparison.Ordinal));

        // Nothing of the credentials is stored with the object or in its key either.
        var key = Assert.Single(await ObjectKeysAsync());
        var stored = await _s3.GetObjectMetadataAsync(Bucket, key);
        var about = key + string.Join('|', stored.Metadata.Keys.Select(name => $"{name}={stored.Metadata[name]}"));
        Assert.DoesNotContain(SecretKey, about);
        Assert.DoesNotContain(AccessKey, about);
        // LocalStack publishes nothing on the host: the application reached it over the network.
        var localStack = await _docker.Containers.InspectContainerAsync(_localStackName);
        Assert.DoesNotContain(localStack.NetworkSettings.Ports.Values, bindings => bindings is { Count: > 0 });
    }

    // --- Cancellation -------------------------------------------------------------------------

    [DockerFact]
    public async Task CancelledDuringTheUpload_NothingIsCompleted_AndTheJobFinishesTheBackupWhenRunAgain()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync(_factory, _client, "postgres", "16", "big");
        // Enough incompressible data for the dump, and so its upload, to be large.
        await ExecAsync(instanceId, $"{Psql} -d big -c \"create table filler as select g as id, md5(g::text) || md5((g * 7)::text) || md5((g * 13)::text) as payload from generate_series(1, 3000000) g;\"");
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        var key = $"backups/instances/{instanceId:D}/databases/{databaseId:D}/{backupId:D}.dump";
        var uploading = Path.Combine(_factory.StagingRoot, $"{backupId:D}.dump");
        using var shutdown = new CancellationTokenSource();

        var processing = _factory.ProcessJobAsync(jobId, shutdown.Token);
        // The dump is done and validated: the file has its final local name and the upload begins.
        await WaitUntilAsync(() => Task.FromResult(File.Exists(uploading)));
        await shutdown.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);

        // Not completed, not failed: interrupted, with nothing left on the local disk.
        Assert.Equal("running", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Equal("pending", (await _client.GetJobAsync(jobId)).Status());
        Assert.Empty(_factory.StagingFiles());

        // The existing recovery path: the job is simply run again, and uses the same key.
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(_client, jobId);
        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("completed", backup.Status());
        Assert.Equal([key], await ObjectKeysAsync());
        Assert.Equal(backup.GetProperty("sizeBytes").GetInt64(), (await _s3.GetObjectMetadataAsync(Bucket, key)).ContentLength);
        Assert.True(backup.GetProperty("sizeBytes").GetInt64() > 50_000_000);
        Assert.Empty(_factory.StagingFiles());
    }

    // --- Helpers ------------------------------------------------------------------------------

    private async Task<(Guid InstanceId, Guid DatabaseId)> CreateDatabaseAsync(
        ApiFactory factory, HttpClient client, string engine, string version, string name)
    {
        var response = await client.PostAsync(
            InstancesUrl,
            System.Net.Http.Json.JsonContent.Create(ValidInstanceRequest(engine: engine, version: version, memoryMb: engine == "mysql" ? 1024 : 512, storageGb: 1)));
        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        var instanceId = body.GetProperty("instance").GetProperty("id").GetGuid();
        _instanceIds.Add(instanceId);

        await factory.ProcessJobAsync(body.GetProperty("job").GetProperty("id").GetGuid());
        Assert.Equal("running", (await client.GetInstanceAsync(instanceId)).Status());
        return (instanceId, await factory.CreateReadyDatabaseAsync(client, instanceId, name));
    }

    private static async Task AssertJobCompletedAsync(HttpClient client, Guid jobId)
    {
        var job = await client.GetJobAsync(jobId);
        Assert.True(job.Status() == "completed", $"Job is {job.Status()}: {job.GetProperty("error").GetRawText()}");
    }

    /// <summary>The keys of every object in the test's bucket, asked of the store itself.</summary>
    private async Task<List<string>> ObjectKeysAsync()
    {
        var listing = await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket });
        return (listing.S3Objects ?? []).Select(stored => stored.Key).ToList();
    }

    /// <summary>Runs a command with the engine's own client inside the instance's container. Tests only.</summary>
    private async Task ExecAsync(Guid instanceId, string shellCommand) =>
        Assert.Equal(0, await _engine.ExecAsync(DockerResourceNaming.ContainerName(instanceId), ["sh", "-c", shellCommand], default));

    /// <summary>Runs a program here, next to the tests, and returns what it printed.</summary>
    private static async Task<string> RunAsync(string executable, params string[] arguments)
    {
        var result = await new Infrastructure.Backups.SystemProcessRunner().RunAsync(
            new Infrastructure.Backups.ProcessRequest(executable, arguments, new Dictionary<string, string>(), TimeSpan.FromMinutes(2)), default);
        Assert.True(result.ExitCode == 0, $"{executable} exited with {result.ExitCode}: {result.StandardError}");
        return result.StandardOutput;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }
}
