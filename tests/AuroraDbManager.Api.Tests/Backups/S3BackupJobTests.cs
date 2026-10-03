using System.Net;
using System.Text.Json;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Backups;

/// <summary>
/// Backups with S3 selected as the storage: the real handler, backup managers and S3 storage, run
/// through the real job processor. The dump programs and the object store are fakes, so nothing
/// here needs Docker, LocalStack, AWS credentials or a network.
/// </summary>
public sealed class S3BackupJobTests : IDisposable
{
    private const string AccessKey = "AKIAAURORATESTKEY000";
    private const string SecretKey = "aurora-test-secret-key-9f2c7e41b6";
    private const string Endpoint = "http://object-store.internal.test:4566";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddHours(-1);

    private readonly ApiFactory _factory = S3Factory();
    private readonly HttpClient _client;

    public S3BackupJobTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static ApiFactory S3Factory(string? prefix = null) => new()
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
            options.S3.Prefix = prefix;
        }
    };

    private FakeS3ObjectStore Store => _factory.ObjectStore;

    private FakeDumpTools Tools => _factory.DumpTools;

    private async Task<(Guid InstanceId, Guid DatabaseId)> CreateReadyDatabaseAsync(string engine = "postgres")
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client, engine: engine);
        return (instanceId, await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app"));
    }

    private async Task<string?> JobErrorCodeAsync(Guid jobId)
    {
        var error = (await _client.GetJobAsync(jobId)).GetProperty("error");
        return error.ValueKind == JsonValueKind.Null ? null : error.GetProperty("code").GetString();
    }

    // --- Success ------------------------------------------------------------------------------

    [Theory]
    [InlineData("postgres", "dump", "application/octet-stream")]
    [InlineData("mysql", "sql", "application/sql")]
    public async Task Backup_IsDumpedLocally_UploadedUnderItsDeterministicKey_AndCompleted(string engine, string extension, string contentType)
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync(engine);

        var response = await _client.PostAsync(DatabaseBackupsUrl(databaseId), content: null);
        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        var backupId = body.GetProperty("backup").GetProperty("id").GetGuid();
        var jobId = body.GetProperty("job").GetProperty("id").GetGuid();
        // The same API and the same job as for local storage; only the storage type differs.
        Assert.Equal("s3", body.GetProperty("backup").GetProperty("storageType").GetString());
        Assert.Equal("backup_database", body.GetProperty("job").GetProperty("type").GetString());
        Assert.Empty(Store.ObjectsIn());

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());

        var key = $"backups/instances/{instanceId:D}/databases/{databaseId:D}/{backupId:D}.{extension}";
        Assert.Equal(key, _factory.BackupObjectKey(instanceId, databaseId, backupId, extension));
        var stored = Assert.Single(Store.ObjectsIn());
        Assert.Equal(key, stored.Key);
        Assert.Equal(contentType, stored.Value.ContentType);
        Assert.Equal(engine == "postgres" ? FakeDumpTools.PostgresDump : FakeDumpTools.MysqlDump, stored.Value.Content);

        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("completed", backup.Status());
        Assert.Equal("s3", backup.GetProperty("storageType").GetString());
        Assert.Equal(stored.Value.Content.Length, backup.GetProperty("sizeBytes").GetInt64());

        // The metadata holds the key, and nothing else about the storage.
        var entity = await _factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync());
        Assert.Equal(BackupStorageType.S3, entity.StorageType);
        Assert.Equal(key, entity.Path);

        // The dump program wrote into the local staging directory, which is empty again, and
        // nothing went to the local backup root.
        Assert.StartsWith(_factory.StagingRoot, Assert.Single(Tools.Runs).OutputPath);
        Assert.Empty(_factory.StagingFiles());
        Assert.Empty(_factory.BackupFiles());
    }

    [Fact]
    public async Task Backup_IsOnlyCompleted_AfterTheUploadHasBeenVerified_AndWhatIsUploadedIsNeverThePartialFile()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Store.Block();

        var processing = _factory.ProcessJobAsync(jobId);
        await Store.WaitForUploadAsync();

        // Dumped and validated, upload under way: still running, the local copy still there.
        Assert.Equal("running", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Empty(Store.ObjectsIn());
        var uploading = Assert.Single(_factory.StagingFiles());
        Assert.False(uploading.EndsWith(".partial", StringComparison.Ordinal));
        Assert.Equal(uploading, Assert.Single(Store.Uploads).FilePath);
        Assert.Equal(FakeDumpTools.PostgresDump, await File.ReadAllBytesAsync(uploading));

        Store.Release();
        await processing.WaitAsync(TestTimeout);

        Assert.Equal("completed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Single(Store.ObjectsIn());
        Assert.Empty(_factory.StagingFiles());
    }

    [Fact]
    public async Task MultipleBackups_EachGetItsOwnObject_AndEarlierOnesAreNotTouched()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();

        var first = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        Tools.Content = [.. FakeDumpTools.PostgresDump, .. "more data"u8];
        var second = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var third = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        var objects = Store.ObjectsIn();
        Assert.Equal(
            new[] { first, second, third }.Select(id => _factory.BackupObjectKey(instanceId, databaseId, id, "dump")).Order(),
            objects.Keys.Order());
        Assert.All(objects.Values, stored => Assert.Equal(1, stored.Version));
        Assert.Equal(FakeDumpTools.PostgresDump, objects[_factory.BackupObjectKey(instanceId, databaseId, first, "dump")].Content);
        Assert.All(objects, stored => Assert.Equal(
            Guid.Parse(Path.GetFileNameWithoutExtension(stored.Key)).ToString("D"), stored.Value.Metadata["aurora-backup-id"]));
    }

    [Fact]
    public async Task ConfiguredPrefix_IsPartOfEveryKey()
    {
        using var factory = S3Factory(prefix: "/env/prod/");
        using var client = factory.CreateClient();
        var instanceId = await factory.CreateRunningInstanceAsync(client);
        var databaseId = await factory.CreateReadyDatabaseAsync(client, instanceId, "app");

        var backupId = await factory.CreateCompletedBackupAsync(client, databaseId);

        Assert.Equal(
            [$"env/prod/backups/instances/{instanceId:D}/databases/{databaseId:D}/{backupId:D}.dump"],
            factory.ObjectStore.ObjectsIn().Keys);
    }

    [Fact]
    public async Task Client_CannotChooseBucketKeyOrStorage()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();

        var response = await _client.PostAsync(
            DatabaseBackupsUrl(databaseId),
            System.Net.Http.Json.JsonContent.Create(new
            {
                storageType = "local",
                bucket = "someone-elses-bucket",
                key = "../../stolen.dump",
                path = "/etc/passwd",
                endpoint = "http://attacker.example",
                prefix = "evil"
            }));

        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        var backupId = body.GetProperty("backup").GetProperty("id").GetGuid();
        Assert.Equal("s3", body.GetProperty("backup").GetProperty("storageType").GetString());
        await _factory.ProcessJobAsync(body.GetProperty("job").GetProperty("id").GetGuid());

        var upload = Assert.Single(Store.Uploads);
        Assert.Equal(FakeS3ObjectStore.Bucket, upload.Bucket);
        Assert.Equal(_factory.BackupObjectKey(instanceId, databaseId, backupId, "dump"), upload.Key);
        Assert.Empty(Store.ObjectsIn("someone-elses-bucket"));
    }

    // --- Failure ------------------------------------------------------------------------------

    [Fact]
    public async Task UploadFailsOnce_TheJobRetriesFromAFreshDump_AndCompletes()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Store.FailNextUploads(1);

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(2, job.GetProperty("attempt").GetInt32());
        Assert.Equal("completed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Equal(2, Tools.RunCount);
        Assert.Equal(2, Store.UploadCount);
        // Both attempts used the same key; there is one object.
        Assert.Single(Store.Uploads.Select(upload => upload.Key).Distinct());
        Assert.Single(Store.ObjectsIn());
        Assert.Empty(_factory.StagingFiles());
    }

    [Fact]
    public async Task UploadKeepsFailing_BackupIsNeverCompleted_FailsWithASafeError_AndLeavesNothingBehind()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Store.FailNextUploads(3);

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        // One upload per job attempt: the storage does not retry on its own.
        Assert.Equal(3, Store.UploadCount);

        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("failed", backup.Status());
        Assert.Equal("BACKUP_STORAGE_UPLOAD_FAILED", backup.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, backup.GetProperty("sizeBytes").ValueKind);
        Assert.DoesNotContain("raw-sdk-detail", job.GetRawText() + backup.GetRawText());
        Assert.DoesNotContain("s3.internal.example", job.GetRawText() + backup.GetRawText());

        Assert.Null((await _factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync())).Path);
        Assert.Empty(Store.ObjectsIn());
        Assert.Empty(_factory.StagingFiles());
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
    }

    [Fact]
    public async Task StoreUnreachable_FailsAsStorageUnavailable_WithoutEvenDumping()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Store.Unavailable = true;

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("BACKUP_STORAGE_UNAVAILABLE", await JobErrorCodeAsync(jobId));
        Assert.Equal("failed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Equal(0, Tools.RunCount);
        Assert.Empty(_factory.StagingFiles());
    }

    [Fact]
    public async Task StoreComesBackDuringRetries_BackupCompletes()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Store.FailNextUploads(2, new BackupOperationException(BackupErrorCodes.BackupStorageUnavailable, "The backup storage is not available."));

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal(3, (await _client.GetJobAsync(jobId)).GetProperty("attempt").GetInt32());
        Assert.Equal("completed", (await _client.GetBackupAsync(backupId)).Status());
    }

    [Fact]
    public async Task BucketDoesNotExist_FailsAsBucketNotFound()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Store.Buckets.Clear();

        await _factory.ProcessJobAsync(jobId);

        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("BACKUP_STORAGE_BUCKET_NOT_FOUND", backup.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(FakeS3ObjectStore.Bucket, backup.GetRawText());
        Assert.Empty(_factory.StagingFiles());
    }

    [Fact]
    public async Task StoredObjectIsNotWhatWasUploaded_FailsVerification_OnEveryAttempt_AndTheBackupIsNeverCompleted()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Store.TruncateUploadsTo = 3;

        await _factory.ProcessJobAsync(jobId);

        // The bad object of one attempt is not taken for the backup by the next: each attempt
        // dumps and uploads again, and each fails verification.
        Assert.Equal("BACKUP_STORAGE_VERIFICATION_FAILED", await JobErrorCodeAsync(jobId));
        Assert.Equal(3, (await _client.GetJobAsync(jobId)).GetProperty("attempt").GetInt32());
        Assert.Equal(3, Store.UploadCount);
        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("failed", backup.Status());
        Assert.Equal(JsonValueKind.Null, backup.GetProperty("sizeBytes").ValueKind);
        // No dump is left behind, only the empty marker that the object is not a backup.
        Assert.EndsWith(".rejected", Assert.Single(_factory.StagingFiles()));
    }

    [Fact]
    public async Task VerificationFailsOnce_TheBadObjectIsReplacedByTheRetry_AndTheBackupCompletes()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Store.TruncateUploadsTo = 3;
        Store.Block();

        var processing = _factory.ProcessJobAsync(jobId);
        await Store.WaitForUploadAsync();
        Store.Release();
        // The first attempt has failed verification; the second is now held at its upload.
        await Store.WaitForUploadAsync();
        Store.TruncateUploadsTo = null;
        Store.Release();
        await processing.WaitAsync(TestTimeout);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(2, job.GetProperty("attempt").GetInt32());
        Assert.Equal(FakeDumpTools.PostgresDump.Length, (await _client.GetBackupAsync(backupId)).GetProperty("sizeBytes").GetInt64());
        Assert.Equal(FakeDumpTools.PostgresDump, Assert.Single(Store.ObjectsIn()).Value.Content);
        Assert.Empty(_factory.StagingFiles());
    }

    [Fact]
    public async Task DumpFails_NothingIsUploaded()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Tools.FailAllRuns();
        Tools.WritePartOfADumpThenFail();

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("failed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Equal(0, Store.UploadCount);
        Assert.Empty(Store.ObjectsIn());
        Assert.Empty(_factory.StagingFiles());
    }

    [Fact]
    public async Task DumpIsIncomplete_ItIsNeverUploaded()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (_, jobId) = await _client.CreateBackupAsync(databaseId);
        Tools.Content = "not a PostgreSQL archive"u8.ToArray();

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("BACKUP_ARTIFACT_INVALID", await JobErrorCodeAsync(jobId));
        Assert.Equal(0, Store.UploadCount);
        Assert.Empty(_factory.StagingFiles());
    }

    // --- Recovery and idempotency -------------------------------------------------------------

    [Fact]
    public async Task UploadSucceededButCompletionWasInterrupted_TheObjectIsAdopted_WithoutDumpingOrUploadingAgain()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        var key = _factory.BackupObjectKey(instanceId, databaseId, backupId, "dump");
        byte[] uploaded = [.. FakeDumpTools.PostgresDump, .. "uploaded by the attempt that was interrupted"u8];

        // The attempt dumped, uploaded and verified; then the process died before the backup and
        // the job could be marked completed. The job is left running under a lease nobody renews.
        Store.Put(key, uploaded);
        await _factory.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);
        await _factory.WithDbAsync(async db =>
        {
            (await db.Backups.SingleAsync()).MarkRunning();
            return await db.SaveChangesAsync();
        });

        Assert.Equal([jobId], await _factory.RecoverJobsAsync(includePending: false));
        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("completed", backup.Status());
        Assert.Equal(uploaded.Length, backup.GetProperty("sizeBytes").GetInt64());

        // The same object, untouched: no second dump, no second upload, no second key.
        Assert.Equal(0, Tools.RunCount);
        Assert.Equal(0, Store.UploadCount);
        var stored = Assert.Single(Store.ObjectsIn());
        Assert.Equal(key, stored.Key);
        Assert.Equal(1, stored.Value.Version);
        Assert.Equal(uploaded, stored.Value.Content);
        Assert.Equal(key, (await _factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync())).Path);
    }

    [Fact]
    public async Task InterruptedBeforeTheUpload_LeftoverLocalFilesAreDiscarded_AndTheBackupIsDumpedAndUploaded()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        await _factory.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);

        // The process died while the dump was being written: a .partial file is all there is.
        Directory.CreateDirectory(_factory.StagingRoot);
        var partial = Path.Combine(_factory.StagingRoot, $"{backupId:D}.dump.partial");
        await File.WriteAllBytesAsync(partial, [.. FakeDumpTools.PostgresDump, .. new byte[4000]]);

        await _factory.RecoverJobsAsync(includePending: false);
        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
        Assert.Equal(1, Tools.RunCount);
        // What was uploaded is the new dump, not the leftover.
        var stored = Assert.Single(Store.ObjectsIn());
        Assert.Equal(_factory.BackupObjectKey(instanceId, databaseId, backupId, "dump"), stored.Key);
        Assert.Equal(FakeDumpTools.PostgresDump, stored.Value.Content);
        Assert.Empty(_factory.StagingFiles());
    }

    [Fact]
    public async Task ObjectThatDoesNotMatchTheUploadedFile_IsNotAdopted_AndIsReplacedByAVerifiedUpload()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        var key = _factory.BackupObjectKey(instanceId, databaseId, backupId, "dump");

        // The local file of the interrupted attempt says the object should have 300 bytes; it has 10.
        Directory.CreateDirectory(_factory.StagingRoot);
        await File.WriteAllBytesAsync(Path.Combine(_factory.StagingRoot, $"{backupId:D}.dump"), new byte[300]);
        Store.Put(key, new byte[10]);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
        Assert.Equal(1, Tools.RunCount);
        var stored = Assert.Single(Store.ObjectsIn()).Value;
        Assert.Equal(FakeDumpTools.PostgresDump, stored.Content);
        Assert.Equal(2, stored.Version);
        Assert.Equal(FakeDumpTools.PostgresDump.Length, (await _client.GetBackupAsync(backupId)).GetProperty("sizeBytes").GetInt64());
        Assert.Empty(_factory.StagingFiles());
    }

    [Fact]
    public async Task Cancelled_WhileUploading_StopsTheUpload_LeavesNoObjectAndNoLocalFile_AndTheJobRunsAgainLater()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        using var shutdown = new CancellationTokenSource();
        Store.Block();

        var processing = _factory.ProcessJobAsync(jobId, shutdown.Token);
        await Store.WaitForUploadAsync();
        await shutdown.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TestTimeout));

        Assert.Empty(Store.ObjectsIn());
        Assert.Empty(_factory.StagingFiles());
        Assert.Equal("running", (await _client.GetBackupAsync(backupId)).Status());
        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("pending", job.Status());
        Assert.Equal(0, job.GetProperty("attempt").GetInt32());

        Store.Release(uploads: 10);
        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Equal([_factory.BackupObjectKey(instanceId, databaseId, backupId, "dump")], Store.ObjectsIn().Keys);
    }

    [Fact]
    public async Task BackupRequestedForAnotherStorageThanTheConfiguredOne_IsNotQuietlyStoredElsewhere()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        // Requested while the server stored backups locally; the server now uses S3.
        var (backupId, jobId) = await _factory.WithDbAsync(async db =>
        {
            var backup = Backup.Create(databaseId, BackupStorageType.Local, DateTime.UtcNow);
            var job = Job.Create(JobType.BackupDatabase, instanceId, 3, DateTime.UtcNow, databaseId, backup.Id);
            db.Backups.Add(backup);
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            return (backup.Id, job.Id);
        });

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("BACKUP_INVALID_STATE", await JobErrorCodeAsync(jobId));
        Assert.Equal("failed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Equal(0, Tools.RunCount);
        Assert.Empty(Store.ObjectsIn());
        Assert.Empty(_factory.BackupFiles());
    }

    // --- Lifecycle guards ---------------------------------------------------------------------

    [Fact]
    public async Task UnfinishedBackup_StillProtectsItsDatabaseAndInstance_AndBlocksASecondBackup()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        await _client.CreateBackupAsync(databaseId);

        await (await _client.PostAsync(DatabaseBackupsUrl(databaseId), content: null)).AssertErrorAsync(HttpStatusCode.Conflict, "BACKUP_OPERATION_IN_PROGRESS");
        await (await _client.DeleteAsync($"{DatabasesUrl}/{databaseId}")).AssertErrorAsync(HttpStatusCode.Conflict, "BACKUP_OPERATION_IN_PROGRESS");
        await (await _client.DeleteAsync($"{InstancesUrl}/{instanceId}")).AssertErrorAsync(HttpStatusCode.Conflict, "BACKUP_OPERATION_IN_PROGRESS");
    }

    [Fact]
    public async Task DeletingTheDatabase_RemovesBackupMetadata_AndNeverDeletesObjects()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        await _factory.CreateCompletedBackupAsync(_client, databaseId);

        await _factory.ProcessJobAsync(await _client.DeleteDatabaseAsync(databaseId));

        Assert.Equal(0, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
        Assert.Single(Store.ObjectsIn());
    }

    // --- Security -----------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageSecretsAndConfiguration_NeverReachAClient_TheLogs_OrTheMetadata(bool uploadFails)
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        if (uploadFails)
        {
            Store.FailNextUploads(3);
        }

        await _factory.ProcessJobAsync(jobId);

        string[] responses =
        [
            (await _client.GetBackupAsync(backupId)).GetRawText(),
            (await _client.GetJobAsync(jobId)).GetRawText(),
            await _client.GetStringAsync(DatabaseBackupsUrl(databaseId)),
            (await _client.GetDatabaseAsync(databaseId)).GetRawText(),
            (await _client.GetInstanceAsync(instanceId)).GetRawText()
        ];

        // Not the credentials, and not where or how the server stores backups either.
        foreach (var hidden in new[] { SecretKey, AccessKey, Endpoint, "object-store.internal.test", FakeS3ObjectStore.Bucket, "backups/instances/" })
        {
            Assert.All(responses, response => Assert.DoesNotContain(hidden, response));
        }

        Assert.NotEmpty(_factory.Logs.Entries);
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(SecretKey, StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(AccessKey, StringComparison.Ordinal));

        // Neither in what is stored about the backup, nor in the key, nor with the object.
        var entity = await _factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync());
        var jobs = await _factory.WithDbAsync(db => db.Jobs.AsNoTracking().ToListAsync());
        var stored = string.Join('|', new[] { entity.Path, entity.ErrorCode, entity.ErrorMessage }
            .Concat(jobs.SelectMany(job => new[] { job.ErrorCode, job.ErrorMessage }))
            .Concat(Store.Uploads.Select(upload => upload.Key))
            .Concat(Store.Uploads.SelectMany(upload => upload.Metadata.SelectMany(entry => new[] { entry.Key, entry.Value }))));
        Assert.DoesNotContain(SecretKey, stored);
        Assert.DoesNotContain(AccessKey, stored);
    }
}
