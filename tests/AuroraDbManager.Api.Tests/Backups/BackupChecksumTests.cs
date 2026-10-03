using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using AuroraDbManager.Api.Domain.Backups;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Tests.Backups;

/// <summary>
/// The checksum of a backup, from the job's point of view: what is recorded, and that a backup is
/// never completed without a checksum that was verified against the stored artifact. Local and S3
/// storage, with the real hasher; only the dump programs and the object store are fakes.
/// </summary>
public sealed class BackupChecksumTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }
    }

    private (ApiFactory Factory, HttpClient Client) Application(bool s3 = false)
    {
        var factory = new ApiFactory
        {
            ConfigureBackups = options =>
            {
                if (s3)
                {
                    options.StorageType = BackupStorageType.S3;
                    options.S3.Bucket = FakeS3ObjectStore.Bucket;
                    options.S3.Region = "us-east-1";
                }
            }
        };
        var client = factory.CreateClient();
        _disposables.Add(factory);
        _disposables.Add(client);
        return (factory, client);
    }

    private static async Task<(Guid InstanceId, Guid DatabaseId)> CreateReadyDatabaseAsync(ApiFactory factory, HttpClient client, string engine = "postgres")
    {
        var instanceId = await factory.CreateRunningInstanceAsync(client, engine: engine);
        return (instanceId, await factory.CreateReadyDatabaseAsync(client, instanceId, "app"));
    }

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    // --- What is recorded ---------------------------------------------------------------------

    [Theory]
    [InlineData("postgres", "dump")]
    [InlineData("mysql", "sql")]
    public async Task Local_CompletedBackup_RecordsTheSha256OfTheArtifactAsItIsOnDisk(string engine, string extension)
    {
        var (factory, client) = Application();
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync(factory, client, engine);
        var content = new byte[50_000];
        Random.Shared.NextBytes(content);
        factory.DumpTools.Content = engine == "postgres"
            ? [.. "PGDMP"u8, .. content]
            : [.. content.Select(value => (byte)('a' + (value % 26))), .. "\n-- Dump completed on 2026-01-01\n"u8];

        var backupId = await factory.CreateCompletedBackupAsync(client, databaseId);

        var onDisk = await File.ReadAllBytesAsync(factory.BackupFilePath(instanceId, databaseId, backupId, extension));
        var stored = await factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync());
        Assert.Equal(BackupChecksumAlgorithm.Sha256, stored.ChecksumAlgorithm);
        Assert.Equal(Sha256(onDisk), stored.Checksum);
        Assert.Equal(onDisk.Length, stored.SizeBytes);

        var backup = await client.GetBackupAsync(backupId);
        Assert.Equal("sha256", backup.GetProperty("checksumAlgorithm").GetString());
        Assert.Equal(Sha256(onDisk), backup.GetProperty("checksum").GetString());
        // Calculated before storing and again from the stored file.
        Assert.Equal(2, factory.Hasher.Computations);
    }

    [Fact]
    public async Task S3_CompletedBackup_RecordsTheSha256OfTheObjectAsTheStoreReturnsIt()
    {
        var (factory, client) = Application(s3: true);
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync(factory, client);

        var backupId = await factory.CreateCompletedBackupAsync(client, databaseId);

        var stored = factory.ObjectStore.ObjectsIn()[factory.BackupObjectKey(instanceId, databaseId, backupId, "dump")];
        var backup = await client.GetBackupAsync(backupId);
        Assert.Equal("sha256", backup.GetProperty("checksumAlgorithm").GetString());
        Assert.Equal(Sha256(stored.Content), backup.GetProperty("checksum").GetString());
        // The object was read back once to be verified, and says what it was uploaded as.
        Assert.Equal(1, factory.ObjectStore.Reads);
        Assert.Equal(Sha256(stored.Content), stored.Metadata["aurora-checksum-sha256"]);
    }

    [Fact]
    public async Task UnfinishedAndFailedBackups_HaveNoChecksum()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await CreateReadyDatabaseAsync(factory, client);
        var (backupId, jobId) = await client.CreateBackupAsync(databaseId);
        Assert.Equal(JsonValueKind.Null, (await client.GetBackupAsync(backupId)).GetProperty("checksum").ValueKind);

        factory.DumpTools.FailNextRuns(3);
        await factory.ProcessJobAsync(jobId);

        var failed = await client.GetBackupAsync(backupId);
        Assert.Equal("failed", failed.Status());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("checksum").ValueKind);
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("checksumAlgorithm").ValueKind);
    }

    [Fact]
    public async Task TwoBackupsOfDifferentContent_HaveDifferentChecksums_AndOfTheSameContent_TheSame()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await CreateReadyDatabaseAsync(factory, client);

        var first = (await client.GetBackupAsync(await factory.CreateCompletedBackupAsync(client, databaseId))).GetProperty("checksum").GetString();
        var same = (await client.GetBackupAsync(await factory.CreateCompletedBackupAsync(client, databaseId))).GetProperty("checksum").GetString();
        factory.DumpTools.Content = [.. FakeDumpTools.PostgresDump, .. "one more row"u8];
        var other = (await client.GetBackupAsync(await factory.CreateCompletedBackupAsync(client, databaseId))).GetProperty("checksum").GetString();

        Assert.Equal(first, same);
        Assert.NotEqual(first, other);
    }

    // --- Never completed without a verified checksum ------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChecksumCannotBeCalculated_BackupIsNotCompleted_AndNothingIsStored(bool s3)
    {
        var (factory, client) = Application(s3);
        var (_, databaseId) = await CreateReadyDatabaseAsync(factory, client);
        var (backupId, jobId) = await client.CreateBackupAsync(databaseId);
        factory.Hasher.FailNextComputations(3);

        await factory.ProcessJobAsync(jobId);

        var job = await client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        Assert.Equal("BACKUP_STORAGE_FAILED", job.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("raw-io-detail", job.GetRawText());

        var backup = await client.GetBackupAsync(backupId);
        Assert.Equal("failed", backup.Status());
        Assert.Equal(JsonValueKind.Null, backup.GetProperty("checksum").ValueKind);
        Assert.Empty(factory.BackupFiles());
        Assert.Empty(factory.StagingFiles());
        Assert.Equal(0, factory.ObjectStore.UploadCount);
    }

    [Fact]
    public async Task ChecksumFailsOnce_TheJobRetries_AndTheBackupCompletesWithAVerifiedChecksum()
    {
        var (factory, client) = Application();
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync(factory, client);
        var (backupId, jobId) = await client.CreateBackupAsync(databaseId);
        factory.Hasher.FailNextComputations(1);

        await factory.ProcessJobAsync(jobId);

        var job = await client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(2, job.GetProperty("attempt").GetInt32());
        // Each attempt dumps afresh; the checksum is that of the artifact the second one stored.
        Assert.Equal(2, factory.DumpTools.RunCount);
        var path = factory.BackupFilePath(instanceId, databaseId, backupId, "dump");
        Assert.Equal(Sha256(await File.ReadAllBytesAsync(path)), (await client.GetBackupAsync(backupId)).GetProperty("checksum").GetString());
        Assert.Equal([path], factory.BackupFiles());
    }

    [Fact]
    public async Task Local_ArtifactChangesBetweenTheChecksumAndTheStore_SameSize_IsNotCompleted_AndNoFileIsLeftToBeFound()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await CreateReadyDatabaseAsync(factory, client);
        var (backupId, jobId) = await client.CreateBackupAsync(databaseId);
        // Right after the staging file has been hashed, one of its bytes changes on disk.
        factory.Hasher.AfterFileHashed = path =>
        {
            if (path.EndsWith(".partial", StringComparison.Ordinal))
            {
                var bytes = File.ReadAllBytes(path);
                bytes[^1] ^= 0xFF;
                File.WriteAllBytes(path, bytes);
            }
        };

        await factory.ProcessJobAsync(jobId);

        var job = await client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        Assert.Equal("BACKUP_CHECKSUM_MISMATCH", job.GetProperty("error").GetProperty("code").GetString());
        var backup = await client.GetBackupAsync(backupId);
        Assert.Equal("failed", backup.Status());
        Assert.Equal(JsonValueKind.Null, backup.GetProperty("checksum").ValueKind);
        // The mismatching file was at the final path for a moment; it is not left there.
        Assert.Empty(factory.BackupFiles());
        // No checksum, expected or actual, reaches the client.
        Assert.DoesNotMatch("[0-9a-f]{64}", job.GetRawText() + backup.GetRawText());
    }

    [Fact]
    public async Task S3_StoreHoldsOtherBytesOfTheSameSize_IsNotCompleted_AndTheRetryReplacesTheObjectWhenTheStoreBehaves()
    {
        var (factory, client) = Application(s3: true);
        var (_, databaseId) = await CreateReadyDatabaseAsync(factory, client);
        var (backupId, jobId) = await client.CreateBackupAsync(databaseId);
        factory.ObjectStore.CorruptUploads = true;
        factory.ObjectStore.Block();

        var processing = factory.ProcessJobAsync(jobId);
        await factory.ObjectStore.WaitForUploadAsync();
        factory.ObjectStore.Release();
        // The first attempt has failed its checksum; the second is held at its upload.
        await factory.ObjectStore.WaitForUploadAsync();

        var running = await client.GetJobAsync(jobId);
        Assert.Equal("BACKUP_CHECKSUM_MISMATCH", running.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("running", (await client.GetBackupAsync(backupId)).Status());

        factory.ObjectStore.CorruptUploads = false;
        factory.ObjectStore.Release();
        await processing.WaitAsync(TestTimeout);

        Assert.Equal("completed", (await client.GetJobAsync(jobId)).Status());
        var stored = Assert.Single(factory.ObjectStore.ObjectsIn()).Value;
        Assert.Equal(2, stored.Version);
        Assert.Equal(Sha256(stored.Content), (await client.GetBackupAsync(backupId)).GetProperty("checksum").GetString());
    }

    [Fact]
    public async Task S3_InterruptedAfterUpload_ObjectIsAdoptedOnlyAfterItsBytesWereHashed_AndThatChecksumIsRecorded()
    {
        var (factory, client) = Application(s3: true);
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync(factory, client);
        var (backupId, jobId) = await client.CreateBackupAsync(databaseId);
        byte[] uploaded = [.. FakeDumpTools.PostgresDump, .. "uploaded before the interruption"u8];
        factory.ObjectStore.Put(factory.BackupObjectKey(instanceId, databaseId, backupId, "dump"), uploaded);

        await factory.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await client.GetJobAsync(jobId)).Status());
        Assert.Equal(0, factory.DumpTools.RunCount);
        Assert.Equal(0, factory.ObjectStore.UploadCount);
        Assert.Equal(1, factory.ObjectStore.Reads);
        Assert.Equal(Sha256(uploaded), (await client.GetBackupAsync(backupId)).GetProperty("checksum").GetString());
    }

    [Fact]
    public async Task S3_InterruptedAfterUpload_ObjectWhoseBytesNoLongerMatchWhatItWasUploadedAs_IsNotAdopted_ButReplaced()
    {
        var (factory, client) = Application(s3: true);
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync(factory, client);
        var (backupId, jobId) = await client.CreateBackupAsync(databaseId);
        var key = factory.BackupObjectKey(instanceId, databaseId, backupId, "dump");
        // The right size for a dump of this database, and other bytes than it was uploaded as.
        factory.ObjectStore.Put(key, FakeDumpTools.PostgresDump);
        factory.ObjectStore.Corrupt(key);

        await factory.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await client.GetJobAsync(jobId)).Status());
        Assert.Equal(1, factory.DumpTools.RunCount);
        Assert.Equal(1, factory.ObjectStore.UploadCount);
        var stored = factory.ObjectStore.ObjectsIn()[key];
        Assert.Equal(FakeDumpTools.PostgresDump, stored.Content);
        Assert.Equal(Sha256(FakeDumpTools.PostgresDump), (await client.GetBackupAsync(backupId)).GetProperty("checksum").GetString());
    }

    [Fact]
    public async Task Cancelled_WhileTheChecksumIsCalculated_NothingIsStoredOrCompleted_AndTheJobRunsAgainLater()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await CreateReadyDatabaseAsync(factory, client);
        var (backupId, jobId) = await client.CreateBackupAsync(databaseId);
        using var shutdown = new CancellationTokenSource();
        factory.Hasher.Block();

        var processing = factory.ProcessJobAsync(jobId, shutdown.Token);
        await factory.Hasher.WaitForComputationAsync();
        await shutdown.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TestTimeout));

        Assert.Empty(factory.BackupFiles());
        var backup = await client.GetBackupAsync(backupId);
        Assert.Equal("running", backup.Status());
        Assert.Equal(JsonValueKind.Null, backup.GetProperty("checksum").ValueKind);
        Assert.Equal("pending", (await client.GetJobAsync(jobId)).Status());

        factory.Hasher.Release(computations: 10);
        await factory.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await client.GetBackupAsync(backupId)).Status());
        Assert.Equal(JsonValueKind.String, (await client.GetBackupAsync(backupId)).GetProperty("checksum").ValueKind);
    }

    // --- The database's own rules -------------------------------------------------------------

    [Fact]
    public async Task CheckConstraints_KeepChecksumAndAlgorithmTogether_WellFormed_AndOnCompletedBackupsOnly()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await CreateReadyDatabaseAsync(factory, client);
        var completed = await factory.CreateCompletedBackupAsync(client, databaseId);
        var (pending, _) = await client.CreateBackupAsync(databaseId);
        var checksum = new string('a', 64);

        Task Update(FormattableString sql) => factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(sql));

        await Assert.ThrowsAnyAsync<DbException>(() => Update($"UPDATE backups SET checksum = NULL WHERE id = {completed}"));
        await Assert.ThrowsAnyAsync<DbException>(() => Update($"UPDATE backups SET checksum_algorithm = NULL WHERE id = {completed}"));
        await Assert.ThrowsAnyAsync<DbException>(() => Update($"UPDATE backups SET checksum = 'abc' WHERE id = {completed}"));
        await Assert.ThrowsAnyAsync<DbException>(() => Update($"UPDATE backups SET checksum_algorithm = 'md5' WHERE id = {completed}"));
        await Assert.ThrowsAnyAsync<DbException>(() => Update($"UPDATE backups SET checksum = {checksum}, checksum_algorithm = 'sha256' WHERE id = {pending}"));

        // A completed backup without either is allowed: one from before checksums were recorded.
        await Update($"UPDATE backups SET checksum = NULL, checksum_algorithm = NULL WHERE id = {completed}");
        var legacy = await client.GetBackupAsync(completed);
        Assert.Equal("completed", legacy.Status());
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("checksum").ValueKind);
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("checksumAlgorithm").ValueKind);
    }
}
