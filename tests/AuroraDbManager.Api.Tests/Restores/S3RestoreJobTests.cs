using System.Text.Json;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Tests.Backups;

namespace AuroraDbManager.Api.Tests.Restores;

/// <summary>
/// Restores with S3 selected as the storage: the artifact is downloaded from the object store,
/// here the in-memory one, and everything after that is the same restore.
/// </summary>
public sealed class S3RestoreJobTests : IDisposable
{
    private const string AccessKey = "AKIAAURORATESTKEY000";
    private const string SecretKey = "aurora-test-secret-key-9f2c7e41b6";
    private const string Endpoint = "http://object-store.internal.test:4566";

    private readonly ApiFactory _factory = new()
    {
        ConfigureBackups = options =>
        {
            options.StorageType = BackupStorageType.S3;
            options.S3.Bucket = FakeS3ObjectStore.Bucket;
            options.S3.Region = "us-east-1";
            options.S3.Endpoint = Endpoint;
            options.S3.AccessKey = AccessKey;
            options.S3.SecretKey = SecretKey;
            options.S3.PathStyle = true;
        }
    };

    private readonly HttpClient _client;

    public S3RestoreJobTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private FakeS3ObjectStore Store => _factory.ObjectStore;

    private FakeDumpTools Tools => _factory.DumpTools;

    private async Task<(Guid InstanceId, Guid DatabaseId, Guid BackupId, string Key)> CreateBackedUpDatabaseAsync(string engine = "postgres")
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client, engine: engine);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "orders");
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        return (instanceId, databaseId, backupId, _factory.BackupObjectKey(instanceId, databaseId, backupId, engine == "postgres" ? "dump" : "sql"));
    }

    private async Task<JsonElement> RestoreAsync(Guid backupId)
    {
        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await _factory.ProcessJobAsync(jobId);
        return await _client.GetJobAsync(jobId);
    }

    [Theory]
    [InlineData("postgres", FakeDumpTools.PgRestore)]
    [InlineData("mysql", FakeDumpTools.MySql)]
    public async Task Restore_DownloadsTheBackupsObject_LoadsIt_AndNeverChangesTheObject(string engine, string loadKind)
    {
        var (_, _, backupId, key) = await CreateBackedUpDatabaseAsync(engine);
        var uploaded = Store.ObjectsIn()[key];

        var job = await RestoreAsync(backupId);

        Assert.Equal("completed", job.Status());
        // One GET of the backup's own key, in the server's bucket.
        Assert.Equal([(FakeS3ObjectStore.Bucket, key)], Store.Downloads);

        var load = Assert.Single(Tools.RunsOf(loadKind));
        Assert.Equal(uploaded.Content, load.InputContent);
        Assert.StartsWith(_factory.RestoreStagingRoot, load.InputPath);

        // The object is exactly the one that was uploaded: nothing was put, replaced or removed.
        Assert.Equal(1, Store.UploadCount);
        var after = Assert.Single(Store.ObjectsIn());
        Assert.Equal(key, after.Key);
        Assert.Same(uploaded, after.Value);
        Assert.Equal(1, after.Value.Version);

        Assert.Empty(_factory.RestoreStagingEntries());
        Assert.Empty(_factory.StagingFiles());
    }

    [Fact]
    public async Task ObjectIsNoLongerInTheBucket_FailsAsArtifactNotFound_AndTheDatabaseIsNeverTouched()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        Store.Buckets.Clear();
        // The metadata still names the object, but the store has nothing under that key.
        var emptied = new FakeS3ObjectStore();
        Assert.Empty(emptied.ObjectsIn());
        await _factory.WithDbAsync(async db =>
        {
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlAsync(
                db.Database, $"UPDATE backups SET path = 'backups/instances/gone/databases/gone/gone.dump' WHERE id = {backupId}");
            return 0;
        });

        var job = await RestoreAsync(backupId);

        Assert.Equal("failed", job.Status());
        Assert.Equal("RESTORE_ARTIFACT_NOT_FOUND", job.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("backups/instances", job.GetRawText());
        Assert.Empty(Tools.RunsOf(FakeDumpTools.PgRestore));
        Assert.Empty(_factory.RestoreSql.Statements);
        Assert.Empty(_factory.RestoreStagingEntries());
    }

    [Fact]
    public async Task ObjectIsNotTheSizeTheBackupRecorded_FailsAsArtifactInvalid_AndTheDatabaseIsNeverTouched()
    {
        var (_, _, backupId, key) = await CreateBackedUpDatabaseAsync();
        // Replaced behind the application's back by something of another size.
        Store.Put(key, [.. FakeDumpTools.PostgresDump, .. "tampered"u8]);

        var job = await RestoreAsync(backupId);

        Assert.Equal("RESTORE_ARTIFACT_INVALID", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(Tools.RunsOf(FakeDumpTools.PgRestore));
        Assert.Empty(_factory.RestoreSql.Statements);
    }

    [Fact]
    public async Task StoreUnreachable_FailsWithTheStoragesOwnCode_WithoutTouchingTheDatabase_AndTheRetryCompletesWhenItIsBack()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        Store.Unavailable = true;

        var failed = await RestoreAsync(backupId);

        Assert.Equal("failed", failed.Status());
        Assert.Equal("BACKUP_STORAGE_UNAVAILABLE", failed.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("raw-sdk-detail", failed.GetRawText());
        Assert.Empty(_factory.RestoreSql.Statements);
        Assert.Empty(_factory.RestoreStagingEntries());

        Store.Unavailable = false;
        Assert.Equal("completed", (await RestoreAsync(backupId)).Status());
    }

    [Fact]
    public async Task StorageSecretsAndConfiguration_NeverReachAClientOrTheLogs()
    {
        var (instanceId, databaseId, backupId, _) = await CreateBackedUpDatabaseAsync();

        var job = await RestoreAsync(backupId);

        string[] responses =
        [
            job.GetRawText(),
            (await _client.GetBackupAsync(backupId)).GetRawText(),
            (await _client.GetDatabaseAsync(databaseId)).GetRawText(),
            (await _client.GetInstanceAsync(instanceId)).GetRawText()
        ];
        foreach (var hidden in new[] { SecretKey, AccessKey, Endpoint, FakeS3ObjectStore.Bucket, "backups/instances/" })
        {
            Assert.All(responses, response => Assert.DoesNotContain(hidden, response));
        }

        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(SecretKey, StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(AccessKey, StringComparison.Ordinal));
        Assert.All(Tools.Runs, run => Assert.DoesNotContain(run.Arguments, argument =>
            argument.Contains(SecretKey, StringComparison.Ordinal) || argument.Contains(AccessKey, StringComparison.Ordinal)));
    }
}
