using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Infrastructure.Backups;
using AuroraDbManager.Api.Infrastructure.Backups.S3;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Tests.Backups;

/// <summary>The S3 backup storage against an object store in memory; no network, no AWS credentials.</summary>
public sealed class S3BackupStorageTests : IDisposable
{
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _staging = Path.Combine(Path.GetTempPath(), $"aurora-s3-storage-tests-{Guid.NewGuid():N}");
    private readonly FakeS3ObjectStore _store = new();
    private readonly BackupLocation _location = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "dump");

    public void Dispose()
    {
        if (Directory.Exists(_staging))
        {
            Directory.Delete(_staging, recursive: true);
        }
    }

    private S3BackupStorage Storage(string? prefix = null, int uploadTimeoutSeconds = 60) => new(
        _store,
        new Sha256ArtifactHasher(),
        Options.Create(new BackupOptions
        {
            StorageType = BackupStorageType.S3,
            S3 = new S3BackupOptions
            {
                Bucket = FakeS3ObjectStore.Bucket,
                Region = "us-east-1",
                Prefix = prefix,
                StagingPath = _staging,
                UploadTimeoutSeconds = uploadTimeoutSeconds
            }
        }),
        NullLogger<S3BackupStorage>.Instance);

    private string KeyOf(BackupLocation location) =>
        $"backups/instances/{location.InstanceId:D}/databases/{location.DatabaseId:D}/{location.BackupId:D}.{location.Extension}";

    private string[] LocalFiles() => Directory.Exists(_staging) ? Directory.GetFiles(_staging, "*", SearchOption.AllDirectories) : [];

    private static string Sha256(byte[] content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

    private static async Task<BackupArtifact> WriteAsync(S3BackupStorage storage, BackupLocation location, byte[] content)
    {
        await using var staging = await storage.BeginAsync(location, default);
        await File.WriteAllBytesAsync(staging.FilePath, content);
        return await staging.CommitAsync(Sha256(content), default);
    }

    [Fact]
    public void Type_IsS3()
    {
        Assert.Equal(BackupStorageType.S3, Storage().Type);
    }

    // --- Keys ---------------------------------------------------------------------------------

    [Fact]
    public void KeyFor_IsBuiltFromIdsOnly_AndIsTheSameEveryTime()
    {
        var storage = Storage();

        Assert.Equal(KeyOf(_location), storage.KeyFor(_location));
        Assert.Equal(storage.KeyFor(_location), storage.KeyFor(_location with { }));
        Assert.NotEqual(storage.KeyFor(_location), storage.KeyFor(_location with { BackupId = Guid.NewGuid() }));
        Assert.EndsWith(".sql", storage.KeyFor(_location with { Extension = "sql" }));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("aurora", "aurora/")]
    [InlineData("/aurora/", "aurora/")]
    [InlineData("env/prod", "env/prod/")]
    [InlineData(" /env/prod// ", "env/prod/")]
    public void KeyFor_PutsTheNormalizedPrefixInFront(string? prefix, string expectedStart)
    {
        Assert.Equal(expectedStart + KeyOf(_location), Storage(prefix).KeyFor(_location));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../../other-bucket/x")]
    [InlineData("dump/../x")]
    [InlineData("a/b")]
    [InlineData("dump?versionId=1")]
    [InlineData("DUMP")]
    [InlineData("du mp")]
    public async Task ExtensionThatIsNotPlainLettersAndDigits_IsRejected_AndNothingIsContacted(string extension)
    {
        var storage = Storage();
        var location = _location with { Extension = extension };

        Assert.Throws<ArgumentException>(() => storage.KeyFor(location));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.FindAsync(location, default));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.BeginAsync(location, default));

        Assert.Empty(LocalFiles());
        Assert.Equal(0, _store.UploadCount);
    }

    // --- Storing ------------------------------------------------------------------------------

    [Fact]
    public async Task Begin_CreatesAnEmptyPrivateStagingFile_AndUploadsNothing()
    {
        await using var staging = await Storage().BeginAsync(_location, default);

        Assert.Equal(Path.Combine(Path.GetFullPath(_staging), $"{_location.BackupId:D}.dump.partial"), staging.FilePath);
        Assert.Equal(0, new FileInfo(staging.FilePath).Length);
        Assert.Equal(0, _store.UploadCount);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(staging.FilePath));
            Assert.Equal(OwnerOnlyFile | UnixFileMode.UserExecute, File.GetUnixFileMode(_staging));
        }
    }

    [Fact]
    public async Task Commit_UploadsTheValidatedFileUnderTheBackupsKey_VerifiesIt_AndReturnsTheObjectAsTheArtifact()
    {
        var content = new byte[12_345];
        Random.Shared.NextBytes(content);

        var artifact = await WriteAsync(Storage(), _location, content);

        Assert.Equal(new BackupArtifact(BackupStorageType.S3, KeyOf(_location), 12_345, Sha256(content)), artifact);
        var stored = Assert.Single(_store.ObjectsIn());
        Assert.Equal(KeyOf(_location), stored.Key);
        Assert.Equal(content, stored.Value.Content);

        var upload = Assert.Single(_store.Uploads);
        Assert.Equal(FakeS3ObjectStore.Bucket, upload.Bucket);
        // What was uploaded is the validated file, never the one still called .partial.
        Assert.False(upload.FilePath.EndsWith(".partial", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(Path.GetFullPath(_staging), $"{_location.BackupId:D}.dump"), upload.FilePath);
    }

    [Theory]
    [InlineData("dump", "application/octet-stream")]
    [InlineData("sql", "application/sql")]
    public async Task Commit_SetsTheContentTypeOfTheDumpFormat_AndInformationalMetadataOnly(string extension, string contentType)
    {
        var location = _location with { Extension = extension };

        await WriteAsync(Storage(), location, "a backup"u8.ToArray());

        var stored = Assert.Single(_store.ObjectsIn()).Value;
        Assert.Equal(contentType, stored.ContentType);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["aurora-backup-id"] = location.BackupId.ToString("D"),
                ["aurora-database-id"] = location.DatabaseId.ToString("D"),
                ["aurora-instance-id"] = location.InstanceId.ToString("D"),
                ["aurora-checksum-sha256"] = Sha256("a backup"u8.ToArray())
            },
            stored.Metadata);
    }

    [Fact]
    public async Task AfterAVerifiedUpload_NoLocalFileIsLeft()
    {
        await WriteAsync(Storage(), _location, "a backup"u8.ToArray());

        Assert.Empty(LocalFiles());
    }

    [Fact]
    public async Task LocalCopy_IsStillThere_UntilTheUploadHasBeenVerified()
    {
        var storage = Storage();
        _store.Block();

        await using var staging = await storage.BeginAsync(_location, default);
        await File.WriteAllTextAsync(staging.FilePath, "a backup");
        var committing = staging.CommitAsync(Sha256("a backup"u8.ToArray()), default);
        await _store.WaitForUploadAsync();

        // The upload is under way: the only copy so far is the local one, and it is intact.
        Assert.Empty(_store.ObjectsIn());
        Assert.Equal("a backup", await File.ReadAllTextAsync(Assert.Single(LocalFiles())));

        _store.Release();
        await committing;
        Assert.Single(_store.ObjectsIn());
    }

    [Fact]
    public async Task Commit_OfAnEmptyStagingFile_IsRejected_AndNothingIsUploaded()
    {
        BackupOperationException exception;
        await using (var staging = await Storage().BeginAsync(_location, default))
        {
            exception = await Assert.ThrowsAsync<BackupOperationException>(() => staging.CommitAsync(Sha256([]), default));
        }

        Assert.Equal("BACKUP_ARTIFACT_INVALID", exception.Code);
        Assert.Equal(0, _store.UploadCount);
        Assert.Empty(LocalFiles());
    }

    [Fact]
    public async Task DisposeWithoutCommit_RemovesTheStagingFile_AndUploadsNothing()
    {
        await using (var staging = await Storage().BeginAsync(_location, default))
        {
            await File.WriteAllTextAsync(staging.FilePath, "half a backup");
        }

        Assert.Empty(LocalFiles());
        Assert.Equal(0, _store.UploadCount);
    }

    // --- Failures -----------------------------------------------------------------------------

    [Fact]
    public async Task UploadFails_TheErrorIsPassedOn_NoObjectExists_AndNoLocalFileIsLeftBehind()
    {
        _store.FailNextUploads(1);

        var exception = await Assert.ThrowsAsync<BackupOperationException>(
            () => WriteAsync(Storage(), _location, "a backup"u8.ToArray()));

        Assert.Equal("BACKUP_STORAGE_UPLOAD_FAILED", exception.Code);
        Assert.Empty(_store.ObjectsIn());
        Assert.Empty(LocalFiles());
        // One attempt: retrying is the job's business.
        Assert.Equal(1, _store.UploadCount);
    }

    [Fact]
    public async Task BucketDoesNotExist_FailsAsBucketNotFound()
    {
        _store.Buckets.Clear();

        var exception = await Assert.ThrowsAsync<BackupOperationException>(
            () => WriteAsync(Storage(), _location, "a backup"u8.ToArray()));

        Assert.Equal("BACKUP_STORAGE_BUCKET_NOT_FOUND", exception.Code);
    }

    [Fact]
    public async Task StoreUnreachable_FailsAsUnavailable_OnFindAndOnCommit()
    {
        var storage = Storage();
        await using var staging = await storage.BeginAsync(_location, default);
        await File.WriteAllTextAsync(staging.FilePath, "a backup");
        _store.Unavailable = true;

        Assert.Equal("BACKUP_STORAGE_UNAVAILABLE", (await Assert.ThrowsAsync<BackupOperationException>(() => storage.FindAsync(_location, default))).Code);
        Assert.Equal("BACKUP_STORAGE_UNAVAILABLE", (await Assert.ThrowsAsync<BackupOperationException>(() => staging.CommitAsync(Sha256("a backup"u8.ToArray()), default))).Code);
    }

    [Fact]
    public async Task StoredObjectIsSmallerThanTheFile_FailsVerification()
    {
        _store.TruncateUploadsTo = 4;

        var exception = await Assert.ThrowsAsync<BackupOperationException>(
            () => WriteAsync(Storage(), _location, "a complete backup"u8.ToArray()));

        Assert.Equal("BACKUP_STORAGE_VERIFICATION_FAILED", exception.Code);
        // The backup's own local files are gone; what stays is an empty marker.
        var marker = Assert.Single(LocalFiles());
        Assert.EndsWith(".rejected", marker);
        Assert.Equal(0, new FileInfo(marker).Length);
    }

    [Fact]
    public async Task ObjectThatFailedVerification_IsNeverAdopted_UntilAVerifiedUploadHasReplacedIt()
    {
        var storage = Storage();
        _store.TruncateUploadsTo = 4;
        await Assert.ThrowsAsync<BackupOperationException>(() => WriteAsync(storage, _location, "a complete backup"u8.ToArray()));
        // The bad object is still in the bucket; nothing deletes objects.
        Assert.Equal(4, _store.ObjectsIn()[KeyOf(_location)].Content.Length);

        // A later attempt, with no local copy left to compare against, must still not take it for a backup.
        Assert.Null(await storage.FindAsync(_location, default));
        Assert.Null(await Storage().FindAsync(_location, default));

        _store.TruncateUploadsTo = null;
        var artifact = await WriteAsync(storage, _location, "a complete backup"u8.ToArray());

        Assert.Equal(17, artifact.SizeBytes);
        Assert.Equal(artifact, await storage.FindAsync(_location, default));
        Assert.Equal(2, _store.ObjectsIn()[KeyOf(_location)].Version);
        Assert.Empty(LocalFiles());
    }

    [Fact]
    public async Task UploadReportsSuccessButNoObjectIsThere_FailsVerification()
    {
        _store.DropUploads = true;

        var exception = await Assert.ThrowsAsync<BackupOperationException>(
            () => WriteAsync(Storage(), _location, "a backup"u8.ToArray()));

        Assert.Equal("BACKUP_STORAGE_VERIFICATION_FAILED", exception.Code);
    }

    [Fact]
    public async Task UploadTakesLongerThanAllowed_IsStopped_AndFailsAsTimeout()
    {
        _store.Block();
        var started = DateTime.UtcNow;

        var exception = await Assert.ThrowsAsync<BackupOperationException>(
            () => WriteAsync(Storage(uploadTimeoutSeconds: 1), _location, "a backup"u8.ToArray()));

        Assert.Equal("BACKUP_STORAGE_TIMEOUT", exception.Code);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(10));
        Assert.Empty(_store.ObjectsIn());
        Assert.Empty(LocalFiles());
    }

    [Fact]
    public async Task Cancellation_StopsTheUpload_AndIsNotTurnedIntoAFailure()
    {
        _store.Block();
        using var cancellation = new CancellationTokenSource();

        await using (var staging = await Storage().BeginAsync(_location, default))
        {
            await File.WriteAllTextAsync(staging.FilePath, "a backup");
            var committing = staging.CommitAsync(Sha256("a backup"u8.ToArray()), cancellation.Token);
            await _store.WaitForUploadAsync();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => committing);
        }

        Assert.Empty(_store.ObjectsIn());
        Assert.Empty(LocalFiles());
    }

    // --- Finding and adopting -----------------------------------------------------------------

    [Fact]
    public async Task Find_NoObject_ReturnsNull()
    {
        Assert.Null(await Storage().FindAsync(_location, default));
    }

    [Fact]
    public async Task Find_ObjectOfThisBackup_IsReturnedAsItsArtifact_AndOnlyThisBackups()
    {
        var storage = Storage();
        await WriteAsync(storage, _location, new byte[777]);

        Assert.Equal(new BackupArtifact(BackupStorageType.S3, KeyOf(_location), 777, Sha256(new byte[777])), await storage.FindAsync(_location, default));
        Assert.Null(await storage.FindAsync(_location with { BackupId = Guid.NewGuid() }, default));
        // Finding uploads nothing and replaces nothing.
        Assert.Equal(1, _store.UploadCount);
        Assert.Equal(1, _store.ObjectsIn()[KeyOf(_location)].Version);
    }

    [Fact]
    public async Task Find_ObjectExistsAndTheUploadedFileIsStillHereWithTheSameSize_IsAdopted_AndTheFileIsRemoved()
    {
        // An attempt uploaded the backup and was killed before it could clean up or report.
        Directory.CreateDirectory(_staging);
        await File.WriteAllBytesAsync(Path.Combine(_staging, $"{_location.BackupId:D}.dump"), new byte[500]);
        _store.Put(KeyOf(_location), new byte[500]);

        var artifact = await Storage().FindAsync(_location, default);

        Assert.Equal(new BackupArtifact(BackupStorageType.S3, KeyOf(_location), 500, Sha256(new byte[500])), artifact);
        Assert.Empty(LocalFiles());
    }

    [Fact]
    public async Task Find_ObjectExistsButIsNotTheSizeOfTheFileThatWasUploaded_IsNotAccepted()
    {
        Directory.CreateDirectory(_staging);
        await File.WriteAllBytesAsync(Path.Combine(_staging, $"{_location.BackupId:D}.dump"), new byte[500]);
        _store.Put(KeyOf(_location), new byte[499]);
        var storage = Storage();

        Assert.Null(await storage.FindAsync(_location, default));

        // The next attempt starts over and replaces the object with a verified one.
        var artifact = await WriteAsync(storage, _location, new byte[640]);
        Assert.Equal(640, artifact.SizeBytes);
        Assert.Equal(640, _store.ObjectsIn()[KeyOf(_location)].Content.Length);
        Assert.Empty(LocalFiles());
    }

    [Fact]
    public async Task Find_EmptyObject_IsNotAccepted()
    {
        _store.Put(KeyOf(_location), []);

        Assert.Null(await Storage().FindAsync(_location, default));
    }

    [Fact]
    public async Task Begin_DiscardsWhatAnEarlierAttemptLeftBehind()
    {
        Directory.CreateDirectory(_staging);
        var verified = Path.Combine(_staging, $"{_location.BackupId:D}.dump");
        await File.WriteAllTextAsync(verified, "validated by an attempt whose upload failed");
        await File.WriteAllTextAsync(verified + ".partial", "left by a killed attempt");
        var other = Path.Combine(_staging, $"{Guid.NewGuid():D}.dump.partial");
        await File.WriteAllTextAsync(other, "another backup, being written right now");

        await using var staging = await Storage().BeginAsync(_location, default);

        Assert.Equal(new[] { other, verified + ".partial" }.Order(), LocalFiles().Order());
        Assert.Equal(0, new FileInfo(staging.FilePath).Length);
    }

    // --- Reading back -------------------------------------------------------------------------

    [Fact]
    public async Task Download_WritesTheObjectIntoTheFile_WithOneGet_AndChangesNothingInTheStore()
    {
        var storage = Storage();
        var content = new byte[4321];
        Random.Shared.NextBytes(content);
        var artifact = await WriteAsync(storage, _location, content);
        Directory.CreateDirectory(_staging);
        var destination = Path.Combine(_staging, "restore-copy");

        await storage.DownloadAsync(artifact, destination, default);

        Assert.Equal(content, await File.ReadAllBytesAsync(destination));
        Assert.Equal([(FakeS3ObjectStore.Bucket, KeyOf(_location))], _store.Downloads);
        Assert.Equal(1, _store.UploadCount);
        Assert.Equal(1, _store.ObjectsIn()[KeyOf(_location)].Version);
    }

    [Fact]
    public async Task Download_NoSuchObject_OrAnArtifactOfAnotherStorage_FailsAsArtifactNotFound()
    {
        var storage = Storage();
        Directory.CreateDirectory(_staging);
        var destination = Path.Combine(_staging, "restore-copy");

        var missing = await Assert.ThrowsAsync<BackupOperationException>(
            () => storage.DownloadAsync(new BackupArtifact(BackupStorageType.S3, KeyOf(_location), 10), destination, default));
        var foreign = await Assert.ThrowsAsync<BackupOperationException>(
            () => storage.DownloadAsync(new BackupArtifact(BackupStorageType.Local, "/var/lib/aurora/backups/x.dump", 10), destination, default));

        Assert.Equal("BACKUP_ARTIFACT_NOT_FOUND", missing.Code);
        Assert.Equal("BACKUP_ARTIFACT_NOT_FOUND", foreign.Code);
        Assert.DoesNotContain(KeyOf(_location), missing.Message);
        // Only the first reached the store at all.
        Assert.Single(_store.Downloads);
    }

    [Fact]
    public async Task Download_StoreUnreachable_FailsAsUnavailable()
    {
        var storage = Storage();
        var artifact = await WriteAsync(storage, _location, "a backup"u8.ToArray());
        _store.Unavailable = true;

        var exception = await Assert.ThrowsAsync<BackupOperationException>(
            () => storage.DownloadAsync(artifact, Path.Combine(_staging, "restore-copy"), default));

        Assert.Equal("BACKUP_STORAGE_UNAVAILABLE", exception.Code);
    }

    // --- Integrity ----------------------------------------------------------------------------

    [Fact]
    public async Task Commit_ReadsTheObjectBackFromTheStore_AndReturnsItsChecksum()
    {
        var content = new byte[70_000];
        Random.Shared.NextBytes(content);

        var artifact = await WriteAsync(Storage(), _location, content);

        Assert.Equal(Sha256(content), artifact.Checksum);
        // Verified against the bytes the store returns, not assumed from the upload.
        Assert.Equal(1, _store.Reads);
        Assert.Equal(Sha256(_store.ObjectsIn()[KeyOf(_location)].Content), artifact.Checksum);
    }

    [Fact]
    public async Task Commit_StoreHoldsOtherBytesOfTheSameSize_FailsAsChecksumMismatch_AndTheObjectIsNeverAdopted()
    {
        var storage = Storage();
        _store.CorruptUploads = true;

        var exception = await Assert.ThrowsAsync<BackupOperationException>(
            () => WriteAsync(storage, _location, "a complete backup"u8.ToArray()));

        // The size check alone would have passed.
        Assert.Equal(17, _store.ObjectsIn()[KeyOf(_location)].Content.Length);
        Assert.Equal("BACKUP_CHECKSUM_MISMATCH", exception.Code);
        Assert.DoesNotContain(Sha256("a complete backup"u8.ToArray()), exception.Message);
        // The object is still there, since nothing deletes objects, and is known not to be a backup.
        Assert.Null(await storage.FindAsync(_location, default));
        Assert.EndsWith(".rejected", Assert.Single(LocalFiles()));

        _store.CorruptUploads = false;
        var artifact = await WriteAsync(storage, _location, "a complete backup"u8.ToArray());
        Assert.Equal(artifact, await storage.FindAsync(_location, default));
    }

    [Fact]
    public async Task Commit_CallerChecksumIsNotThatOfTheUploadedFile_FailsAsChecksumMismatch()
    {
        await using var staging = await Storage().BeginAsync(_location, default);
        await File.WriteAllTextAsync(staging.FilePath, "what was uploaded");

        var exception = await Assert.ThrowsAsync<BackupOperationException>(
            () => staging.CommitAsync(Sha256("what the dump wrote"u8.ToArray()), default));

        Assert.Equal("BACKUP_CHECKSUM_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task Find_ObjectWhoseBytesWereChangedButNotItsSize_IsNotAdopted()
    {
        var storage = Storage();
        await WriteAsync(storage, _location, new byte[900]);
        Assert.NotNull(await storage.FindAsync(_location, default));

        // Same key, same size, same metadata; other content.
        _store.Corrupt(KeyOf(_location));

        Assert.Equal(900, _store.ObjectsIn()[KeyOf(_location)].Content.Length);
        Assert.Null(await storage.FindAsync(_location, default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-checksum")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task Find_ObjectThatStatesNoChecksum_OrAnotherOne_IsNotAdopted(string? statedChecksum)
    {
        _store.PutStating(KeyOf(_location), new byte[300], statedChecksum);

        Assert.Null(await Storage().FindAsync(_location, default));
    }

    [Fact]
    public async Task Find_NeverUsesTheStoresOwnIdeaOfAHash()
    {
        // Nothing but the object's bytes and its stated checksum decides; a store's ETag is not
        // part of what the storage is even given.
        Assert.DoesNotContain(
            typeof(S3ObjectInfo).GetProperties(), property => property.Name.Contains("ETag", StringComparison.OrdinalIgnoreCase));
        _store.Put(KeyOf(_location), new byte[300]);

        var artifact = await Storage().FindAsync(_location, default);

        Assert.Equal(Sha256(new byte[300]), artifact!.Checksum);
        Assert.Equal(1, _store.Reads);
    }
}
