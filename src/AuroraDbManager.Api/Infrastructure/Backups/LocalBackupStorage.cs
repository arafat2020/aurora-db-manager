using System.Text.RegularExpressions;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Domain.Backups;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Backups;

/// <summary>
/// Keeps backup artifacts as files under one root directory:
/// <c>&lt;root&gt;/instances/&lt;instanceId&gt;/databases/&lt;databaseId&gt;/&lt;backupId&gt;.&lt;extension&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Paths.</b> A path is built from ids and a fixed extension only. No name chosen by a client,
/// and no path supplied by one, is ever part of it.
/// </para>
/// <para>
/// <b>Atomicity.</b> An artifact is written as <c>&lt;artifact&gt;.partial</c> next to its final
/// place, on the same filesystem, and becomes the artifact by a rename, which either happens
/// completely or not at all. A file at the final path is therefore always a finished artifact,
/// and a <c>.partial</c> file never is one.
/// </para>
/// <para>
/// <b>Permissions.</b> On Unix, directories are created accessible to the API's user only (0700)
/// and files readable and writable by that user only (0600). Nothing is enforced on Windows.
/// </para>
/// </remarks>
public sealed partial class LocalBackupStorage(IOptions<BackupOptions> options, ILogger<LocalBackupStorage> logger) : IBackupStorage
{
    private const string StagingSuffix = ".partial";
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public BackupStorageType Type => BackupStorageType.Local;

    private string Root => Path.GetFullPath(options.Value.Local.RootPath);

    /// <summary>The final path of the artifact at <paramref name="location"/>. Nothing is created.</summary>
    public string PathFor(BackupLocation location)
    {
        if (!ExtensionPattern().IsMatch(location.Extension))
        {
            throw new ArgumentException("The extension must consist of lowercase letters and digits.", nameof(location));
        }

        var root = Root;
        var path = Path.GetFullPath(Path.Combine(
            root,
            "instances",
            location.InstanceId.ToString("D"),
            "databases",
            location.DatabaseId.ToString("D"),
            $"{location.BackupId:D}.{location.Extension}"));

        // Cannot happen with ids and a checked extension; kept as the last line of defence.
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A backup path must lie under the backup root.");
        }

        return path;
    }

    public Task<BackupArtifact?> FindAsync(BackupLocation location, CancellationToken cancellationToken) =>
        StorageAsync(() =>
        {
            var file = new FileInfo(PathFor(location));
            return file.Exists ? new BackupArtifact(Type, file.FullName, file.Length) : null;
        });

    public Task<IBackupStaging> BeginAsync(BackupLocation location, CancellationToken cancellationToken) =>
        StorageAsync<IBackupStaging>(() =>
        {
            var finalPath = PathFor(location);
            var stagingPath = finalPath + StagingSuffix;
            CreateDirectory(Path.GetDirectoryName(finalPath)!);

            // Whatever an earlier attempt left unfinished is not a backup; start from nothing.
            File.Delete(stagingPath);

            // Created here, with its permissions, so the backup program only ever writes into an
            // existing private file instead of creating one with the default permissions.
            var create = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                create.UnixCreateMode = FileMode;
            }

            using (new FileStream(stagingPath, create))
            {
            }

            return new Staging(this, stagingPath, finalPath);
        });

    public Task DeleteAsync(BackupLocation location, CancellationToken cancellationToken) =>
        StorageAsync(() =>
        {
            var finalPath = PathFor(location);
            File.Delete(finalPath + StagingSuffix);
            File.Delete(finalPath);
            return true;
        });

    /// <summary>Creates the root and every directory between it and <paramref name="path"/> that does not exist yet.</summary>
    private void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        // One level at a time: creating a whole path at once gives only its last directory the
        // requested permissions and leaves the ones in between open to everyone.
        var root = Root;
        var levels = new Stack<string>();
        for (var level = path; level.Length > root.Length; level = Path.GetDirectoryName(level)!)
        {
            levels.Push(level);
        }

        Directory.CreateDirectory(root, DirectoryMode);
        foreach (var level in levels)
        {
            Directory.CreateDirectory(level, DirectoryMode);
        }
    }

    /// <summary>Runs a filesystem operation, reporting its failure as a client-safe storage error.</summary>
    private static Task<T> StorageAsync<T>(Func<T> operation)
    {
        try
        {
            return Task.FromResult(operation());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The exception names paths; it is kept for logs and never shown to clients.
            return Task.FromException<T>(new BackupOperationException(
                BackupErrorCodes.BackupStorageFailed, "The backup could not be stored.", exception));
        }
    }

    [GeneratedRegex("^[a-z0-9]{1,16}$")]
    private static partial Regex ExtensionPattern();

    private sealed class Staging(LocalBackupStorage storage, string stagingPath, string finalPath) : IBackupStaging
    {
        private bool _committed;

        public string FilePath => stagingPath;

        public Task<BackupArtifact> CommitAsync(CancellationToken cancellationToken) =>
            StorageAsync(() =>
            {
                // On disk before it is given its final name, so a crash cannot leave a finished-looking, empty file.
                using (var file = new FileStream(stagingPath, System.IO.FileMode.Open, FileAccess.ReadWrite))
                {
                    if (file.Length == 0)
                    {
                        throw new BackupOperationException(BackupErrorCodes.BackupArtifactInvalid, "The backup is empty.");
                    }

                    file.Flush(flushToDisk: true);
                }

                // Never replaces an artifact: a finished one would have been found before staging began.
                File.Move(stagingPath, finalPath, overwrite: false);
                _committed = true;

                // The size is that of the file as it now is, not of what was meant to be written.
                var artifact = new FileInfo(finalPath);
                if (!artifact.Exists)
                {
                    throw new FileNotFoundException("The backup artifact is missing after it was finalized.", finalPath);
                }

                return new BackupArtifact(storage.Type, artifact.FullName, artifact.Length);
            });

        public ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                try
                {
                    File.Delete(stagingPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Left behind; the next attempt for this backup removes it before it starts.
                    storage.Log.LogWarning(exception, "An unfinished backup file could not be removed");
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private ILogger Log => logger;
}
