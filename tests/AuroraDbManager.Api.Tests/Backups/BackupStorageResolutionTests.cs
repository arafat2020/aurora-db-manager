using System.Net;
using System.Text.Json;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Backups;
using AuroraDbManager.Api.Infrastructure.Backups.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Backups;

/// <summary>
/// Which storage is used for what: the configured default for a backup that is being requested,
/// and the storage a backup's own record names for everything that happens to it afterwards. A
/// "restart" disposes one factory and starts a second one with a different default on the same
/// system database, backup directory and object store, as an operator changing the setting would.
/// </summary>
public sealed class BackupStorageResolutionTests : IDisposable
{
    private readonly TempDatabase _database = new();
    private readonly string _backupRoot = Path.Combine(Path.GetTempPath(), $"aurora-resolution-tests-{Guid.NewGuid():N}");
    private readonly FakeS3ObjectStore _objects = new();

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_backupRoot))
        {
            Directory.Delete(_backupRoot, recursive: true);
        }
    }

    /// <summary>The application with a default storage, and with or without settings for S3.</summary>
    private ApiFactory Application(BackupStorageType defaultStorage, bool s3Configured = true) => new()
    {
        DatabasePath = _database.Path,
        BackupRootPath = _backupRoot,
        ObjectStore = _objects,
        ConfigureBackups = options =>
        {
            options.StorageType = defaultStorage;
            if (s3Configured)
            {
                options.S3.Bucket = FakeS3ObjectStore.Bucket;
                options.S3.Region = "us-east-1";
                options.S3.AccessKey = "AKIAAURORATESTKEY000";
                options.S3.SecretKey = "aurora-test-secret-key-9f2c7e41b6";
            }
        }
    };

    private static async Task<(Guid InstanceId, Guid DatabaseId, Guid BackupId)> CreateBackupAsync(ApiFactory factory, HttpClient client)
    {
        var instanceId = await factory.CreateRunningInstanceAsync(client);
        var databaseId = await factory.CreateReadyDatabaseAsync(client, instanceId, "orders");
        return (instanceId, databaseId, await factory.CreateCompletedBackupAsync(client, databaseId));
    }

    private static async Task<JsonElement> RestoreAsync(ApiFactory factory, HttpClient client, Guid backupId)
    {
        var jobId = await ApiFactory.RequestRestoreAsync(client, backupId);
        await factory.ProcessJobAsync(jobId);
        return await client.GetJobAsync(jobId);
    }

    // --- The resolver -------------------------------------------------------------------------

    private static BackupStorageResolver Resolver(BackupOptions options)
    {
        var wrapped = Options.Create(options);
        var hasher = new Sha256ArtifactHasher();
        return new BackupStorageResolver(
            new LocalBackupStorage(hasher, wrapped, NullLogger<LocalBackupStorage>.Instance),
            new S3BackupStorage(new FakeS3ObjectStore(), hasher, wrapped, NullLogger<S3BackupStorage>.Instance),
            wrapped);
    }

    private static BackupOptions FullyConfigured(BackupStorageType defaultStorage) => new()
    {
        StorageType = defaultStorage,
        Local = new LocalBackupOptions { RootPath = "/var/lib/aurora/backups" },
        S3 = new S3BackupOptions { Bucket = "aurora-backups", Region = "us-east-1" }
    };

    [Theory]
    [InlineData(BackupStorageType.Local, BackupStorageType.Local, typeof(LocalBackupStorage))]
    [InlineData(BackupStorageType.Local, BackupStorageType.S3, typeof(S3BackupStorage))]
    [InlineData(BackupStorageType.S3, BackupStorageType.Local, typeof(LocalBackupStorage))]
    [InlineData(BackupStorageType.S3, BackupStorageType.S3, typeof(S3BackupStorage))]
    public void Resolve_ReturnsTheStorageOfTheAskedType_WhateverTheDefaultIs(
        BackupStorageType defaultStorage, BackupStorageType asked, Type expected)
    {
        var storage = Resolver(FullyConfigured(defaultStorage)).Resolve(asked);

        Assert.IsType(expected, storage);
        Assert.Equal(asked, storage.Type);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(99)]
    public void Resolve_UnknownStorageType_FailsExplicitly_AndNeverFallsBackToAnotherStorage(int unknown)
    {
        var resolver = Resolver(FullyConfigured(BackupStorageType.Local));

        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.Resolve((BackupStorageType)unknown));
    }

    [Fact]
    public void Resolve_S3WithoutS3Settings_FailsAsNotConfigured_WhileLocalStillResolves()
    {
        var options = FullyConfigured(BackupStorageType.Local);
        options.S3 = new S3BackupOptions();
        var resolver = Resolver(options);

        Assert.IsType<LocalBackupStorage>(resolver.Resolve(BackupStorageType.Local));
        var exception = Assert.Throws<BackupOperationException>(() => resolver.Resolve(BackupStorageType.S3));

        Assert.Equal("BACKUP_STORAGE_NOT_CONFIGURED", exception.Code);
        // Which setting is missing is kept for the log; the client's message names none.
        Assert.DoesNotContain("Backups:", exception.Message);
        Assert.Contains("Backups:S3:Bucket", exception.InnerException!.Message);
    }

    [Fact]
    public void Resolve_LocalWithoutARoot_FailsAsNotConfigured_WhileS3StillResolves()
    {
        var options = FullyConfigured(BackupStorageType.S3);
        options.Local = new LocalBackupOptions();
        var resolver = Resolver(options);

        Assert.IsType<S3BackupStorage>(resolver.Resolve(BackupStorageType.S3));
        Assert.Equal(
            "BACKUP_STORAGE_NOT_CONFIGURED",
            Assert.Throws<BackupOperationException>(() => resolver.Resolve(BackupStorageType.Local)).Code);
    }

    [Fact]
    public void Resolve_HalfConfiguredS3_FailsAsNotConfigured_WithoutEchoingCredentials()
    {
        var options = FullyConfigured(BackupStorageType.Local);
        options.S3.AccessKey = "AKIAEXAMPLEKEY";
        var resolver = Resolver(options);

        var exception = Assert.Throws<BackupOperationException>(() => resolver.Resolve(BackupStorageType.S3));

        Assert.Equal("BACKUP_STORAGE_NOT_CONFIGURED", exception.Code);
        Assert.DoesNotContain("AKIAEXAMPLEKEY", exception.ToString());
    }

    [Theory]
    [InlineData(BackupStorageType.Local, typeof(LocalBackupStorage))]
    [InlineData(BackupStorageType.S3, typeof(S3BackupStorage))]
    public void Application_HasOneDefaultStorage_AndOneResolver_OverTheSameTwoStorages(BackupStorageType defaultStorage, Type expectedDefault)
    {
        using var factory = Application(defaultStorage);
        var services = factory.Services;

        // One unambiguous default for new backups...
        Assert.IsType(expectedDefault, Assert.Single(services.GetServices<IBackupStorage>()));
        // ...and one way to find the storage of an existing one; both hand out the same instances.
        var resolver = services.GetRequiredService<IBackupStorageResolver>();
        Assert.Same(services.GetRequiredService<LocalBackupStorage>(), resolver.Resolve(BackupStorageType.Local));
        Assert.Same(services.GetRequiredService<S3BackupStorage>(), resolver.Resolve(BackupStorageType.S3));
        Assert.Same(services.GetRequiredService<IBackupStorage>(), resolver.Resolve(defaultStorage));
    }

    // --- New backups: the configured default --------------------------------------------------

    [Theory]
    [InlineData(BackupStorageType.Local, "local")]
    [InlineData(BackupStorageType.S3, "s3")]
    public async Task NewBackup_GetsTheConfiguredDefault_WhateverTheClientAsksFor(BackupStorageType defaultStorage, string expected)
    {
        using var factory = Application(defaultStorage);
        using var client = factory.CreateClient();
        var instanceId = await factory.CreateRunningInstanceAsync(client);
        var databaseId = await factory.CreateReadyDatabaseAsync(client, instanceId, "orders");

        var response = await client.PostAsync(
            DatabaseBackupsUrl(databaseId),
            System.Net.Http.Json.JsonContent.Create(new { storageType = expected == "local" ? "s3" : "local", storage = "s3", bucket = "other" }));

        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        Assert.Equal(expected, body.GetProperty("backup").GetProperty("storageType").GetString());
        await factory.ProcessJobAsync(body.GetProperty("job").GetProperty("id").GetGuid());

        Assert.Equal(defaultStorage, (await factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync())).StorageType);
        Assert.Equal(expected == "local" ? 1 : 0, factory.BackupFiles().Count);
        Assert.Equal(expected == "s3" ? 1 : 0, _objects.ObjectsIn().Count);
    }

    // --- Existing backups: their own storage --------------------------------------------------

    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    public async Task S3Backup_IsRestoredFromS3_AfterTheDefaultWasChangedToLocal(string engine)
    {
        Guid databaseId, backupId;
        string key;
        using (var before = Application(BackupStorageType.S3))
        using (var client = before.CreateClient())
        {
            var instanceId = await before.CreateRunningInstanceAsync(client, engine: engine);
            databaseId = await before.CreateReadyDatabaseAsync(client, instanceId, "orders");
            backupId = await before.CreateCompletedBackupAsync(client, databaseId);
            key = before.BackupObjectKey(instanceId, databaseId, backupId, engine == "postgres" ? "dump" : "sql");
        }

        using var after = Application(BackupStorageType.Local);
        using var restarted = after.CreateClient();
        Assert.Equal("s3", (await restarted.GetBackupAsync(backupId)).GetProperty("storageType").GetString());

        var job = await RestoreAsync(after, restarted, backupId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        // Read from the object store, not looked for in the local directory.
        Assert.Equal([(FakeS3ObjectStore.Bucket, key)], _objects.Downloads);
        Assert.Empty(after.BackupFiles());
        var load = after.DumpTools.Runs.Last(run => run.Kind is FakeDumpTools.PgRestore or FakeDumpTools.MySql);
        Assert.Equal(_objects.ObjectsIn()[key].Content, load.InputContent);

        // The default still governs what is new: the next backup is local.
        var next = await after.CreateCompletedBackupAsync(restarted, databaseId);
        Assert.Equal("local", (await restarted.GetBackupAsync(next)).GetProperty("storageType").GetString());
        Assert.Single(after.BackupFiles());
        Assert.Single(_objects.ObjectsIn());
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    public async Task LocalBackup_IsRestoredFromTheLocalDirectory_AfterTheDefaultWasChangedToS3(string engine)
    {
        Guid databaseId, backupId;
        string path;
        using (var before = Application(BackupStorageType.Local))
        using (var client = before.CreateClient())
        {
            var instanceId = await before.CreateRunningInstanceAsync(client, engine: engine);
            databaseId = await before.CreateReadyDatabaseAsync(client, instanceId, "orders");
            backupId = await before.CreateCompletedBackupAsync(client, databaseId);
            path = before.BackupFilePath(instanceId, databaseId, backupId, engine == "postgres" ? "dump" : "sql");
        }

        using var after = Application(BackupStorageType.S3);
        using var restarted = after.CreateClient();
        Assert.Equal("local", (await restarted.GetBackupAsync(backupId)).GetProperty("storageType").GetString());

        var job = await RestoreAsync(after, restarted, backupId);

        Assert.Equal("completed", job.Status());
        // Read from the local directory; the object store was never asked.
        Assert.Empty(_objects.Downloads);
        var load = after.DumpTools.Runs.Last(run => run.Kind is FakeDumpTools.PgRestore or FakeDumpTools.MySql);
        Assert.Equal(await File.ReadAllBytesAsync(path), load.InputContent);

        var next = await after.CreateCompletedBackupAsync(restarted, databaseId);
        Assert.Equal("s3", (await restarted.GetBackupAsync(next)).GetProperty("storageType").GetString());
        Assert.Equal([path], after.BackupFiles());
    }

    [Fact]
    public async Task BackupsOfBothStorages_AreRestorableSideBySide_UnderEitherDefault()
    {
        Guid s3BackupId, localBackupId;
        using (var s3Default = Application(BackupStorageType.S3))
        using (var client = s3Default.CreateClient())
        {
            (_, _, s3BackupId) = await CreateBackupAsync(s3Default, client);
        }

        using (var localDefault = Application(BackupStorageType.Local))
        using (var client = localDefault.CreateClient())
        {
            var databaseId = await localDefault.WithDbAsync(db => db.Databases.Select(d => d.Id).SingleAsync());
            localBackupId = await localDefault.CreateCompletedBackupAsync(client, databaseId);
        }

        foreach (var defaultStorage in new[] { BackupStorageType.Local, BackupStorageType.S3 })
        {
            using var factory = Application(defaultStorage);
            using var client = factory.CreateClient();

            Assert.Equal("completed", (await RestoreAsync(factory, client, s3BackupId)).Status());
            Assert.Equal("completed", (await RestoreAsync(factory, client, localBackupId)).Status());
        }
    }

    [Fact]
    public async Task HistoricalS3Backup_IsStillHeldToItsChecksum_WhenRestoredUnderALocalDefault()
    {
        Guid backupId;
        string key;
        using (var before = Application(BackupStorageType.S3))
        using (var client = before.CreateClient())
        {
            var (instanceId, databaseId, id) = await CreateBackupAsync(before, client);
            backupId = id;
            key = before.BackupObjectKey(instanceId, databaseId, backupId, "dump");
        }

        _objects.Corrupt(key);
        using var after = Application(BackupStorageType.Local);
        using var restarted = after.CreateClient();

        var job = await RestoreAsync(after, restarted, backupId);

        Assert.Equal("RESTORE_ARTIFACT_CHECKSUM_MISMATCH", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(after.RestoreSql.Statements);
    }

    [Fact]
    public async Task PendingS3Backup_IsStillStoredInS3_AfterTheDefaultWasChangedToLocal_AndAnInterruptedUploadIsStillAdopted()
    {
        Guid instanceId, databaseId, pendingId, pendingJobId, interruptedId, interruptedJobId;
        using (var before = Application(BackupStorageType.S3))
        using (var client = before.CreateClient())
        {
            instanceId = await before.CreateRunningInstanceAsync(client);
            databaseId = await before.CreateReadyDatabaseAsync(client, instanceId, "orders");
            var otherDatabaseId = await before.CreateReadyDatabaseAsync(client, instanceId, "other");
            (pendingId, pendingJobId) = await client.CreateBackupAsync(databaseId);
            (interruptedId, interruptedJobId) = await client.CreateBackupAsync(otherDatabaseId);
            // The second one's upload finished before the application was stopped.
            _objects.Put(before.BackupObjectKey(instanceId, otherDatabaseId, interruptedId, "dump"), FakeDumpTools.PostgresDump);
        }

        using var after = Application(BackupStorageType.Local);
        using var restarted = after.CreateClient();
        await after.ProcessJobAsync(pendingJobId);
        await after.ProcessJobAsync(interruptedJobId);

        foreach (var backupId in new[] { pendingId, interruptedId })
        {
            var backup = await restarted.GetBackupAsync(backupId);
            Assert.Equal("completed", backup.Status());
            Assert.Equal("s3", backup.GetProperty("storageType").GetString());
        }

        Assert.Equal(2, _objects.ObjectsIn().Count);
        Assert.Empty(after.BackupFiles());
        // One was dumped and uploaded; the other's object was found and adopted.
        Assert.Equal(1, after.DumpTools.RunCount);
        Assert.Equal(1, _objects.UploadCount);
    }

    // --- A storage the server has no settings for ---------------------------------------------

    [Fact]
    public async Task LocalDefaultWithoutS3Settings_StartsAndWorks_AndAnS3BackupIsRejectedAsNotConfigured_NotAsAStartupFailure()
    {
        Guid s3BackupId, databaseId;
        using (var before = Application(BackupStorageType.S3))
        using (var client = before.CreateClient())
        {
            (_, databaseId, s3BackupId) = await CreateBackupAsync(before, client);
        }

        // The S3 settings are gone altogether. The application starts all the same.
        using var after = Application(BackupStorageType.Local, s3Configured: false);
        using var restarted = after.CreateClient();

        // Local backups and restores work as ever.
        var localBackupId = await after.CreateCompletedBackupAsync(restarted, databaseId);
        Assert.Equal("local", (await restarted.GetBackupAsync(localBackupId)).GetProperty("storageType").GetString());
        Assert.Equal("completed", (await RestoreAsync(after, restarted, localBackupId)).Status());

        // The S3 backup is still listed, and cannot be restored here.
        Assert.Equal("completed", (await restarted.GetBackupAsync(s3BackupId)).Status());
        var statementsBefore = after.RestoreSql.Statements.Count;
        var response = await restarted.PostAsync($"{BackupsUrl}/{s3BackupId}/restore", content: null);

        var error = await response.AssertErrorAsync(HttpStatusCode.Conflict, "BACKUP_STORAGE_NOT_CONFIGURED");
        Assert.DoesNotContain("Backups:", error.GetRawText());
        Assert.DoesNotContain(FakeS3ObjectStore.Bucket, error.GetRawText());
        Assert.Equal(0, await after.WithDbAsync(db => db.Jobs.CountAsync(j => j.Type == JobType.RestoreDatabase && j.BackupId == s3BackupId)));
        Assert.Equal(statementsBefore, after.RestoreSql.Statements.Count);
        Assert.Empty(_objects.Downloads);
        // What is missing is in the log, for the operator.
        Assert.Contains(after.Logs.Entries, entry => entry.Contains("Backups:S3:Bucket", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoreJobOfAnS3Backup_RunWhereS3IsNotConfigured_FailsAsNotConfigured_WithoutTouchingTheDatabase()
    {
        Guid instanceId, databaseId, backupId;
        using (var before = Application(BackupStorageType.S3))
        using (var client = before.CreateClient())
        {
            (instanceId, databaseId, backupId) = await CreateBackupAsync(before, client);
        }

        // The restore was accepted while S3 was configured, and runs after the settings were removed.
        using var after = Application(BackupStorageType.Local, s3Configured: false);
        using var restarted = after.CreateClient();
        var jobId = await after.WithDbAsync(async db =>
        {
            var job = Job.Create(JobType.RestoreDatabase, instanceId, 3, DateTime.UtcNow, databaseId, backupId);
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            return job.Id;
        });

        await after.ProcessJobAsync(jobId);

        var job = await restarted.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal("BACKUP_STORAGE_NOT_CONFIGURED", job.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("Backups:", job.GetRawText());
        Assert.Empty(after.RestoreSql.Statements);
        Assert.Empty(_objects.Downloads);
        Assert.Empty(after.RestoreStagingEntries());
    }

    [Fact]
    public async Task PendingS3Backup_RunWhereS3IsNotConfigured_FailsAsNotConfigured_AndIsNotPutIntoLocalStorageInstead()
    {
        Guid backupId, jobId;
        using (var before = Application(BackupStorageType.S3))
        using (var client = before.CreateClient())
        {
            var instanceId = await before.CreateRunningInstanceAsync(client);
            var databaseId = await before.CreateReadyDatabaseAsync(client, instanceId, "orders");
            (backupId, jobId) = await client.CreateBackupAsync(databaseId);
        }

        using var after = Application(BackupStorageType.Local, s3Configured: false);
        using var restarted = after.CreateClient();
        await after.ProcessJobAsync(jobId);

        var backup = await restarted.GetBackupAsync(backupId);
        Assert.Equal("failed", backup.Status());
        Assert.Equal("BACKUP_STORAGE_NOT_CONFIGURED", backup.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, after.DumpTools.RunCount);
        Assert.Empty(after.BackupFiles());
        Assert.Empty(_objects.ObjectsIn());
    }

    [Fact]
    public void S3DefaultWithoutS3Settings_IsStillAStartupFailure()
    {
        // Only the default has to be usable to start; this is the default.
        using var factory = Application(BackupStorageType.S3, s3Configured: false);

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }
}
