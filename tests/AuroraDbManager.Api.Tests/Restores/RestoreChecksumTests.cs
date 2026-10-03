using System.Security.Cryptography;
using System.Text.Json;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Tests.Backups;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Tests.Restores;

/// <summary>
/// The checksum at restore time: an artifact that does not have the checksum recorded for its
/// backup is rejected before anything is run against the database or changed in it. Local and S3
/// storage, both engines.
/// </summary>
public sealed class RestoreChecksumTests : IDisposable
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

    private sealed record Scenario(
        ApiFactory Factory, HttpClient Client, Guid InstanceId, Guid DatabaseId, Guid BackupId, string Engine, bool S3)
    {
        public string Extension => Engine == "postgres" ? "dump" : "sql";

        public string ArtifactPath => Factory.BackupFilePath(InstanceId, DatabaseId, BackupId, Extension);

        public string ObjectKey => Factory.BackupObjectKey(InstanceId, DatabaseId, BackupId, Extension);

        public async Task<byte[]> ArtifactAsync() =>
            S3 ? Factory.ObjectStore.ObjectsIn()[ObjectKey].Content : await File.ReadAllBytesAsync(ArtifactPath);

        /// <summary>Changes the stored artifact's bytes and leaves its size, and for PostgreSQL its signature, as they were.</summary>
        public async Task CorruptKeepingSizeAsync()
        {
            var original = await ArtifactAsync();
            // An equal-length replacement in the middle: AAAA for whatever was there.
            var changed = (byte[])original.Clone();
            for (var i = 8; i < 12; i++)
            {
                changed[i] = (byte)(changed[i] == (byte)'A' ? 'B' : 'A');
            }

            Assert.Equal(original.Length, changed.Length);
            Assert.NotEqual(Sha256(original), Sha256(changed));

            if (S3)
            {
                Factory.ObjectStore.Put(ObjectKey, changed);
            }
            else
            {
                await File.WriteAllBytesAsync(ArtifactPath, changed);
            }
        }

        public async Task<JsonElement> RestoreAsync()
        {
            var jobId = await ApiFactory.RequestRestoreAsync(Client, BackupId);
            await Factory.ProcessJobAsync(jobId);
            return await Client.GetJobAsync(jobId);
        }

        /// <summary>Makes the backup one from before checksums were recorded.</summary>
        public Task MakeLegacyAsync() =>
            Factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
                $"UPDATE backups SET checksum = NULL, checksum_algorithm = NULL WHERE id = {BackupId}"));
    }

    private async Task<Scenario> BackedUpDatabaseAsync(string engine = "postgres", bool s3 = false)
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

        var instanceId = await factory.CreateRunningInstanceAsync(client, engine: engine);
        var databaseId = await factory.CreateReadyDatabaseAsync(client, instanceId, "orders");
        var backupId = await factory.CreateCompletedBackupAsync(client, databaseId);
        return new Scenario(factory, client, instanceId, databaseId, backupId, engine, s3);
    }

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static void AssertDatabaseNeverTouched(Scenario scenario)
    {
        // Not emptied, not connected to, and no restore program run, not even to look at the file.
        Assert.Empty(scenario.Factory.RestoreSql.Statements);
        Assert.Empty(scenario.Factory.RestoreSql.ConnectionStrings);
        Assert.DoesNotContain(scenario.Factory.DumpTools.Runs, run => run.Kind is
            FakeDumpTools.PgRestoreList or FakeDumpTools.PgRestore or FakeDumpTools.MySqlVersion or FakeDumpTools.MySql);
        Assert.Empty(scenario.Factory.RestoreStagingEntries());
    }

    // --- A valid artifact ---------------------------------------------------------------------

    [Theory]
    [InlineData("postgres", false)]
    [InlineData("mysql", false)]
    [InlineData("postgres", true)]
    [InlineData("mysql", true)]
    public async Task ArtifactWithTheRecordedChecksum_IsVerified_ThenRestored(string engine, bool s3)
    {
        var scenario = await BackedUpDatabaseAsync(engine, s3);
        var recorded = (await scenario.Client.GetBackupAsync(scenario.BackupId)).GetProperty("checksum").GetString();
        Assert.Equal(Sha256(await scenario.ArtifactAsync()), recorded);
        var computationsBefore = scenario.Factory.Hasher.Computations;
        var order = new List<string>();
        scenario.Factory.Hasher.AfterFileHashed = _ => order.Add("checksum");
        scenario.Factory.DumpTools.OnRun = run => order.Add(run.Kind);
        scenario.Factory.RestoreSql.StatementFailure = _ =>
        {
            order.Add("sql");
            return null;
        };

        var job = await scenario.RestoreAsync();

        Assert.Equal("completed", job.Status());
        // The fetched file was hashed once, before anything else happened.
        Assert.Equal(computationsBefore + 1, scenario.Factory.Hasher.Computations);
        Assert.Equal("checksum", order[0]);
        Assert.Contains("sql", order);
        // What was loaded is what was verified.
        var load = scenario.Factory.DumpTools.Runs.Last(run => run.Kind is FakeDumpTools.PgRestore or FakeDumpTools.MySql);
        Assert.Equal(recorded, Sha256(load.InputContent!));
    }

    // --- The same size, other bytes -----------------------------------------------------------

    [Theory]
    [InlineData("postgres", false)]
    [InlineData("mysql", false)]
    [InlineData("postgres", true)]
    [InlineData("mysql", true)]
    public async Task ArtifactCorruptedWithoutChangingItsSize_IsRejectedByItsChecksum_BeforeTheDatabaseIsTouched(string engine, bool s3)
    {
        var scenario = await BackedUpDatabaseAsync(engine, s3);
        var recordedSize = (await scenario.Client.GetBackupAsync(scenario.BackupId)).GetProperty("sizeBytes").GetInt64();
        var recordedChecksum = (await scenario.Client.GetBackupAsync(scenario.BackupId)).GetProperty("checksum").GetString()!;
        await scenario.CorruptKeepingSizeAsync();

        // What size and format checks see is unchanged; only the bytes differ.
        var corrupted = await scenario.ArtifactAsync();
        Assert.Equal(recordedSize, corrupted.Length);
        Assert.NotEqual(recordedChecksum, Sha256(corrupted));
        if (engine == "postgres")
        {
            Assert.True(corrupted.AsSpan().StartsWith("PGDMP"u8));
        }
        else
        {
            Assert.Contains("-- Dump completed", System.Text.Encoding.ASCII.GetString(corrupted));
        }

        var job = await scenario.RestoreAsync();

        Assert.Equal("failed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        Assert.Equal("RESTORE_ARTIFACT_CHECKSUM_MISMATCH", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "The backup's artifact does not have the checksum recorded for the backup.",
            job.GetProperty("error").GetProperty("message").GetString());
        AssertDatabaseNeverTouched(scenario);

        // Neither checksum is given to the client; both are in the log, for whoever investigates.
        Assert.DoesNotContain(recordedChecksum, job.GetRawText());
        Assert.DoesNotContain(Sha256(corrupted), job.GetRawText());
        Assert.Contains(scenario.Factory.Logs.Entries, entry =>
            entry.Contains(recordedChecksum, StringComparison.Ordinal) && entry.Contains(Sha256(corrupted), StringComparison.Ordinal));

        // The backup's record is not rewritten to match what is there now.
        Assert.Equal(recordedChecksum, (await scenario.Client.GetBackupAsync(scenario.BackupId)).GetProperty("checksum").GetString());
        Assert.Equal("ready", (await scenario.Client.GetDatabaseAsync(scenario.DatabaseId)).Status());
    }

    [Fact]
    public async Task RecordedChecksumThatIsNotTheArtifacts_IsAMismatchToo()
    {
        var scenario = await BackedUpDatabaseAsync();
        var other = new string('0', 64);
        await scenario.Factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE backups SET checksum = {other} WHERE id = {scenario.BackupId}"));

        var job = await scenario.RestoreAsync();

        Assert.Equal("RESTORE_ARTIFACT_CHECKSUM_MISMATCH", job.GetProperty("error").GetProperty("code").GetString());
        AssertDatabaseNeverTouched(scenario);
    }

    [Fact]
    public async Task WrongSize_IsStillReportedAsSuch_WithoutHashing()
    {
        var scenario = await BackedUpDatabaseAsync();
        await File.AppendAllTextAsync(scenario.ArtifactPath, "x");
        var computationsBefore = scenario.Factory.Hasher.Computations;

        var job = await scenario.RestoreAsync();

        Assert.Equal("RESTORE_ARTIFACT_INVALID", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(computationsBefore, scenario.Factory.Hasher.Computations);
        AssertDatabaseNeverTouched(scenario);
    }

    [Fact]
    public async Task ChecksumMismatchOnce_ThenTheArtifactIsAsRecordedAgain_ANewRestoreSucceeds()
    {
        var scenario = await BackedUpDatabaseAsync();
        var original = await File.ReadAllBytesAsync(scenario.ArtifactPath);
        await scenario.CorruptKeepingSizeAsync();
        Assert.Equal("failed", (await scenario.RestoreAsync()).Status());

        await File.WriteAllBytesAsync(scenario.ArtifactPath, original);

        Assert.Equal("completed", (await scenario.RestoreAsync()).Status());
    }

    // --- Backups from before checksums --------------------------------------------------------

    [Theory]
    [InlineData("postgres", false)]
    [InlineData("mysql", false)]
    [InlineData("postgres", true)]
    public async Task LegacyBackupWithoutChecksum_IsRestoredOnItsSizeAndFormat_AndNoChecksumIsInventedForIt(string engine, bool s3)
    {
        var scenario = await BackedUpDatabaseAsync(engine, s3);
        await scenario.MakeLegacyAsync();
        var computationsBefore = scenario.Factory.Hasher.Computations;

        var job = await scenario.RestoreAsync();

        Assert.Equal("completed", job.Status());
        // Nothing was hashed, and nothing was written back to the backup.
        Assert.Equal(computationsBefore, scenario.Factory.Hasher.Computations);
        var backup = await scenario.Client.GetBackupAsync(scenario.BackupId);
        Assert.Equal(JsonValueKind.Null, backup.GetProperty("checksum").ValueKind);
        Assert.Equal(JsonValueKind.Null, backup.GetProperty("checksumAlgorithm").ValueKind);
        Assert.Contains(scenario.Factory.Logs.Entries, entry => entry.Contains("no recorded checksum", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyBackup_IsStillHeldToItsSizeAndFormat()
    {
        var scenario = await BackedUpDatabaseAsync();
        await scenario.MakeLegacyAsync();
        var original = await File.ReadAllBytesAsync(scenario.ArtifactPath);
        // The recorded size, and no longer an archive.
        await File.WriteAllBytesAsync(scenario.ArtifactPath, [.. "XXXXX"u8, .. original[5..]]);

        var job = await scenario.RestoreAsync();

        Assert.Equal("RESTORE_ARTIFACT_INVALID", job.GetProperty("error").GetProperty("code").GetString());
        AssertDatabaseNeverTouched(scenario);
    }

    [Fact]
    public async Task LegacyBackup_SameSizeCorruptionThatKeepsTheFormat_IsNotDetected_WhichIsWhatAChecksumIsFor()
    {
        var scenario = await BackedUpDatabaseAsync();
        await scenario.MakeLegacyAsync();
        await scenario.CorruptKeepingSizeAsync();

        var job = await scenario.RestoreAsync();

        // The documented limitation of a backup without checksum: this gets as far as the restore program.
        Assert.Equal("completed", job.Status());
        Assert.Single(scenario.Factory.DumpTools.RunsOf(FakeDumpTools.PgRestore));
    }

    // --- Cancellation -------------------------------------------------------------------------

    [Fact]
    public async Task Cancelled_WhileTheChecksumIsVerified_TheRestoreNeverStarts_AndNothingIsLeftBehind()
    {
        var scenario = await BackedUpDatabaseAsync();
        var jobId = await ApiFactory.RequestRestoreAsync(scenario.Client, scenario.BackupId);
        using var shutdown = new CancellationTokenSource();
        scenario.Factory.Hasher.Block();

        var processing = scenario.Factory.ProcessJobAsync(jobId, shutdown.Token);
        await scenario.Factory.Hasher.WaitForComputationAsync();
        // The download is done and is being hashed; it is still only a .partial file.
        Assert.EndsWith(".partial", scenario.Factory.RestoreStagingEntries().Single(entry => File.Exists(entry)));
        await shutdown.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TestTimeout));

        AssertDatabaseNeverTouched(scenario);
        var job = await scenario.Client.GetJobAsync(jobId);
        Assert.Equal("pending", job.Status());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("error").ValueKind);

        scenario.Factory.Hasher.Release(computations: 10);
        await scenario.Factory.ProcessJobAsync(jobId);
        Assert.Equal("completed", (await scenario.Client.GetJobAsync(jobId)).Status());
    }
}
