using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Infrastructure.Backups;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Tests.Backups;

public sealed class LocalBackupStorageTests : IDisposable
{
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyDirectory = OwnerOnlyFile | UnixFileMode.UserExecute;

    // Deliberately does not exist yet: the storage creates what it needs.
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aurora-storage-tests-{Guid.NewGuid():N}", "backups");
    private readonly LocalBackupStorage _storage;
    private readonly BackupLocation _location = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "dump");

    public LocalBackupStorageTests()
    {
        _storage = new LocalBackupStorage(
            Options.Create(new BackupOptions { Local = new LocalBackupOptions { RootPath = _root } }),
            NullLogger<LocalBackupStorage>.Instance);
    }

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(_root)!;
        if (Directory.Exists(parent))
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private string[] AllFiles() =>
        Directory.Exists(_root) ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories) : [];

    private async Task<BackupArtifact> WriteAsync(BackupLocation location, string content)
    {
        await using var staging = await _storage.BeginAsync(location, default);
        await File.WriteAllTextAsync(staging.FilePath, content);
        return await staging.CommitAsync(default);
    }

    [Fact]
    public void Type_IsLocal()
    {
        Assert.Equal(BackupStorageType.Local, _storage.Type);
    }

    // --- Paths --------------------------------------------------------------------------------

    [Fact]
    public void PathFor_IsBuiltFromIdsOnly_UnderTheRoot()
    {
        var path = _storage.PathFor(_location);

        Assert.Equal(
            Path.Combine(
                Path.GetFullPath(_root),
                "instances", _location.InstanceId.ToString("D"),
                "databases", _location.DatabaseId.ToString("D"),
                $"{_location.BackupId:D}.dump"),
            path);
    }

    [Fact]
    public void PathFor_IsDeterministic_AndDistinctPerBackup()
    {
        Assert.Equal(_storage.PathFor(_location), _storage.PathFor(_location with { }));
        Assert.NotEqual(_storage.PathFor(_location), _storage.PathFor(_location with { BackupId = Guid.NewGuid() }));
        Assert.NotEqual(_storage.PathFor(_location), _storage.PathFor(_location with { Extension = "sql" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../../etc/passwd")]
    [InlineData("dump/../../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("dump.sh")]
    [InlineData("DUMP")]
    [InlineData("du mp")]
    [InlineData("dump\0")]
    [InlineData("aaaaaaaaaaaaaaaaa")]
    public async Task ExtensionThatIsNotPlainLettersAndDigits_IsRejected_AndNothingIsCreated(string extension)
    {
        var location = _location with { Extension = extension };

        Assert.Throws<ArgumentException>(() => _storage.PathFor(location));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.BeginAsync(location, default));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.FindAsync(location, default));

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task RelativeRoot_IsResolvedOnce_AndStaysTheRoot()
    {
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), _root);
        var storage = new LocalBackupStorage(
            Options.Create(new BackupOptions { Local = new LocalBackupOptions { RootPath = relative } }),
            NullLogger<LocalBackupStorage>.Instance);

        await using var staging = await storage.BeginAsync(_location, default);

        Assert.True(Path.IsPathRooted(staging.FilePath));
        Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, staging.FilePath);
    }

    // --- Writing ------------------------------------------------------------------------------

    [Fact]
    public async Task Begin_CreatesTheDirectoriesAndAnEmptyPrivateStagingFile_ButNoArtifact()
    {
        Assert.False(Directory.Exists(_root));

        await using var staging = await _storage.BeginAsync(_location, default);

        Assert.True(File.Exists(staging.FilePath));
        Assert.Equal(0, new FileInfo(staging.FilePath).Length);
        Assert.Equal(_storage.PathFor(_location) + ".partial", staging.FilePath);
        Assert.False(File.Exists(_storage.PathFor(_location)));
        Assert.Null(await _storage.FindAsync(_location, default));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(staging.FilePath));
            for (var directory = Path.GetDirectoryName(staging.FilePath)!;
                 directory.Length >= Path.GetFullPath(_root).Length;
                 directory = Path.GetDirectoryName(directory)!)
            {
                Assert.Equal(OwnerOnlyDirectory, File.GetUnixFileMode(directory));
            }
        }
    }

    [Fact]
    public async Task Commit_MovesTheStagingFileToTheFinalPath_AndReportsItsActualSize()
    {
        await using var staging = await _storage.BeginAsync(_location, default);
        await File.WriteAllBytesAsync(staging.FilePath, new byte[12_345]);

        var artifact = await staging.CommitAsync(default);

        Assert.Equal(new BackupArtifact(BackupStorageType.Local, _storage.PathFor(_location), 12_345), artifact);
        Assert.Equal([_storage.PathFor(_location)], AllFiles());
        Assert.Equal(12_345, new FileInfo(artifact.Path).Length);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(artifact.Path));
        }
    }

    [Fact]
    public async Task Commit_ThenDispose_KeepsTheArtifact()
    {
        var artifact = await WriteAsync(_location, "a finished backup");

        Assert.Equal("a finished backup", await File.ReadAllTextAsync(artifact.Path));
        Assert.Equal(artifact, await _storage.FindAsync(_location, default));
    }

    [Fact]
    public async Task DisposeWithoutCommit_RemovesTheStagingFile_AndLeavesNoArtifact()
    {
        await using (var staging = await _storage.BeginAsync(_location, default))
        {
            await File.WriteAllTextAsync(staging.FilePath, "half a backup");
        }

        Assert.Empty(AllFiles());
        Assert.Null(await _storage.FindAsync(_location, default));
    }

    [Fact]
    public async Task Commit_OfAnEmptyStagingFile_IsRejected_AndLeavesNoArtifact()
    {
        BackupOperationException exception;
        await using (var staging = await _storage.BeginAsync(_location, default))
        {
            exception = await Assert.ThrowsAsync<BackupOperationException>(() => staging.CommitAsync(default));
        }

        Assert.Equal("BACKUP_ARTIFACT_INVALID", exception.Code);
        Assert.Empty(AllFiles());
    }

    [Fact]
    public async Task Commit_WhenTheStagingFileHasVanished_FailsAsStorageFailure_WithoutNamingPaths()
    {
        await using var staging = await _storage.BeginAsync(_location, default);
        File.Delete(staging.FilePath);

        var exception = await Assert.ThrowsAsync<BackupOperationException>(() => staging.CommitAsync(default));

        Assert.Equal("BACKUP_STORAGE_FAILED", exception.Code);
        Assert.DoesNotContain(_root, exception.Message);
        Assert.Null(await _storage.FindAsync(_location, default));
    }

    [Fact]
    public async Task Commit_NeverReplacesAnExistingArtifact()
    {
        await WriteAsync(_location, "the first, finished backup");

        await using var staging = await _storage.BeginAsync(_location, default);
        await File.WriteAllTextAsync(staging.FilePath, "an impostor");
        var exception = await Assert.ThrowsAsync<BackupOperationException>(() => staging.CommitAsync(default));

        Assert.Equal("BACKUP_STORAGE_FAILED", exception.Code);
        Assert.Equal("the first, finished backup", await File.ReadAllTextAsync(_storage.PathFor(_location)));
    }

    [Fact]
    public async Task Begin_DiscardsWhatAnEarlierAttemptLeftUnfinished()
    {
        var partial = _storage.PathFor(_location) + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        await File.WriteAllTextAsync(partial, "left behind by a killed attempt");

        // Unfinished work is never an artifact.
        Assert.Null(await _storage.FindAsync(_location, default));

        await using var staging = await _storage.BeginAsync(_location, default);

        Assert.Equal(partial, staging.FilePath);
        Assert.Equal(0, new FileInfo(partial).Length);
    }

    [Fact]
    public async Task RootThatCannotBeCreated_FailsAsStorageFailure()
    {
        // A file where the root directory should be.
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        await File.WriteAllTextAsync(_root, "not a directory");

        var exception = await Assert.ThrowsAsync<BackupOperationException>(() => _storage.BeginAsync(_location, default));

        Assert.Equal("BACKUP_STORAGE_FAILED", exception.Code);
        Assert.Equal("The backup could not be stored.", exception.Message);
        Assert.NotNull(exception.InnerException);
    }

    // --- Finding ------------------------------------------------------------------------------

    [Fact]
    public async Task Find_ReturnsOnlyTheArtifactOfThatBackup()
    {
        var other = _location with { BackupId = Guid.NewGuid() };
        await WriteAsync(_location, "12345");

        Assert.Equal(5, (await _storage.FindAsync(_location, default))!.SizeBytes);
        Assert.Null(await _storage.FindAsync(other, default));
    }

    // --- Reading back -------------------------------------------------------------------------

    [Fact]
    public async Task Download_CopiesTheArtifact_AndLeavesItExactlyAsItWas()
    {
        var artifact = await WriteAsync(_location, "a finished backup");
        var writtenAt = File.GetLastWriteTimeUtc(artifact.Path);
        var destination = Path.Combine(Path.GetDirectoryName(_root)!, "restore-copy");

        await _storage.DownloadAsync(artifact, destination, default);

        Assert.Equal("a finished backup", await File.ReadAllTextAsync(destination));
        Assert.Equal("a finished backup", await File.ReadAllTextAsync(artifact.Path));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(artifact.Path));
        Assert.Equal([artifact.Path], AllFiles());
    }

    [Fact]
    public async Task Download_ReplacesWhatIsAtTheDestination()
    {
        var artifact = await WriteAsync(_location, "short");
        var destination = Path.Combine(Path.GetDirectoryName(_root)!, "restore-copy");
        await File.WriteAllTextAsync(destination, "something much longer left by an earlier attempt");

        await _storage.DownloadAsync(artifact, destination, default);

        Assert.Equal("short", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task Download_ArtifactNoLongerThere_FailsAsArtifactNotFound()
    {
        var artifact = await WriteAsync(_location, "a finished backup");
        File.Delete(artifact.Path);
        var destination = Path.Combine(Path.GetDirectoryName(_root)!, "restore-copy");

        var exception = await Assert.ThrowsAsync<BackupOperationException>(() => _storage.DownloadAsync(artifact, destination, default));

        Assert.Equal("BACKUP_ARTIFACT_NOT_FOUND", exception.Code);
        Assert.DoesNotContain(_root, exception.Message);
        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData("/etc/hosts")]
    [InlineData("../../../../../../etc/hosts")]
    [InlineData("instances/../../outside.dump")]
    public async Task Download_PathThatIsNotUnderTheRoot_IsNeverRead_EvenIfTheFileExists(string path)
    {
        // A file that exists, just outside the root.
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "outside.dump");
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        await File.WriteAllTextAsync(outside, "not a backup of this storage");
        var candidate = path.StartsWith('/') ? path : Path.Combine(_root, path);
        var destination = Path.Combine(Path.GetDirectoryName(_root)!, "restore-copy");

        var exception = await Assert.ThrowsAsync<BackupOperationException>(
            () => _storage.DownloadAsync(new BackupArtifact(BackupStorageType.Local, candidate, 10), destination, default));

        Assert.Equal("BACKUP_ARTIFACT_NOT_FOUND", exception.Code);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Download_UnfinishedStagingFileOrAnArtifactOfAnotherStorage_IsNotAnArtifact()
    {
        var destination = Path.Combine(Path.GetDirectoryName(_root)!, "restore-copy");
        await using var staging = await _storage.BeginAsync(_location, default);
        await File.WriteAllTextAsync(staging.FilePath, "half a backup");

        var partial = await Assert.ThrowsAsync<BackupOperationException>(
            () => _storage.DownloadAsync(new BackupArtifact(BackupStorageType.Local, staging.FilePath, 13), destination, default));
        var foreign = await Assert.ThrowsAsync<BackupOperationException>(
            () => _storage.DownloadAsync(new BackupArtifact(BackupStorageType.S3, _storage.PathFor(_location), 13), destination, default));

        Assert.Equal("BACKUP_ARTIFACT_NOT_FOUND", partial.Code);
        Assert.Equal("BACKUP_ARTIFACT_NOT_FOUND", foreign.Code);
        Assert.False(File.Exists(destination));
    }
}
