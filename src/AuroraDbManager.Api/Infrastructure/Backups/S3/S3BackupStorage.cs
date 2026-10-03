using System.Text.RegularExpressions;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Domain.Backups;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Backups.S3;

/// <summary>
/// Keeps backup artifacts as objects in one bucket of an S3-compatible object store, under the key
/// <c>[&lt;prefix&gt;/]backups/instances/&lt;instanceId&gt;/databases/&lt;databaseId&gt;/&lt;backupId&gt;.&lt;extension&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keys.</b> A key is built from ids, a fixed extension and the server's configured prefix
/// only, so the same backup always has the same key. Neither the key nor the bucket is ever
/// something a client supplies.
/// </para>
/// <para>
/// <b>Order of events.</b> The dump is written to a <c>.partial</c> file in a local staging
/// directory. Once it has been validated, <see cref="IBackupStaging.CommitAsync"/> gives it its
/// final local name, uploads it, and asks the store for the object's size, which must be that of
/// the file. Only then is the artifact returned, and only then can the backup be marked completed.
/// A <c>.partial</c> file is never uploaded, and the local copy is removed only after the object
/// has been verified, or when the attempt has failed and its file is of no further use.
/// </para>
/// <para>
/// <b>Repeating an attempt.</b> An object store shows an object only once its upload has
/// completed, so an object at the backup's key is a finished upload of this backup. If the local
/// copy still exists, the two must have the same size or the object is not accepted. An attempt
/// whose upload failed keeps nothing: the next attempt dumps again and uploads to the same key.
/// An object that failed verification stays in the bucket, since nothing is deleted there; an
/// empty <c>.rejected</c> marker in the staging directory keeps later attempts from adopting it,
/// until a verified upload has replaced it.
/// </para>
/// <para>
/// One call is one attempt. Retries are the job's; the SDK's own handling of transient errors
/// within a request is left as it is.
/// </para>
/// </remarks>
public sealed partial class S3BackupStorage(
    IS3ObjectClient s3,
    IOptions<BackupOptions> options,
    ILogger<S3BackupStorage> logger) : IBackupStorage
{
    private const string StagingSuffix = ".partial";
    private const string RejectedSuffix = ".rejected";
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public BackupStorageType Type => BackupStorageType.S3;

    private S3BackupOptions Settings => options.Value.S3;

    private IS3ObjectClient Client => s3;

    private ILogger Log => logger;

    private string StagingDirectory => Path.GetFullPath(
        string.IsNullOrWhiteSpace(Settings.StagingPath)
            ? Path.Combine(Path.GetTempPath(), "aurora-backup-staging")
            : Settings.StagingPath);

    /// <summary>The key of the artifact at <paramref name="location"/>. Nothing is contacted.</summary>
    public string KeyFor(BackupLocation location)
    {
        EnsureValid(location);

        var key = $"backups/instances/{location.InstanceId:D}/databases/{location.DatabaseId:D}/{location.BackupId:D}.{location.Extension}";
        var prefix = Settings.NormalizedPrefix;
        return prefix.Length == 0 ? key : $"{prefix}/{key}";
    }

    public async Task<BackupArtifact?> FindAsync(BackupLocation location, CancellationToken cancellationToken)
    {
        var key = KeyFor(location);

        // An earlier attempt uploaded to this key and found the object wrong. Whatever is there
        // now is that object, not a backup.
        if (File.Exists(RejectedMarkerFor(location)))
        {
            return null;
        }

        var stored = await s3.FindObjectAsync(Settings.Bucket, key, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        if (stored.SizeBytes <= 0)
        {
            logger.LogWarning("The object of backup {BackupId} is empty and is not accepted as its artifact", location.BackupId);
            return null;
        }

        // The file an interrupted attempt uploaded may still be here. If it is, it says how large
        // the object has to be.
        var local = new FileInfo(VerifiedPathFor(location));
        if (local.Exists && local.Length != stored.SizeBytes)
        {
            logger.LogWarning(
                "The object of backup {BackupId} has {StoredSizeBytes} bytes but the backup that was uploaded has {LocalSizeBytes}; the object is not accepted as its artifact",
                location.BackupId, stored.SizeBytes, local.Length);
            return null;
        }

        // The object is the backup now; the local copy has done its part.
        RemoveLocalFiles(location);
        return new BackupArtifact(Type, key, stored.SizeBytes);
    }

    public Task<IBackupStaging> BeginAsync(BackupLocation location, CancellationToken cancellationToken)
    {
        EnsureValid(location);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(StagingDirectory);
            }
            else
            {
                Directory.CreateDirectory(StagingDirectory, DirectoryMode);
            }

            // Whatever an earlier attempt left here is not going to be used; start from nothing.
            RemoveLocalFiles(location);

            var stagingPath = VerifiedPathFor(location) + StagingSuffix;
            var create = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                create.UnixCreateMode = FileMode;
            }

            using (new FileStream(stagingPath, create))
            {
            }

            return Task.FromResult<IBackupStaging>(new Staging(this, location, stagingPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The exception names paths; it is kept for logs and never shown to clients.
            return Task.FromException<IBackupStaging>(new BackupOperationException(
                BackupErrorCodes.BackupStorageFailed, "The backup could not be staged for upload.", exception));
        }
    }

    public async Task DownloadAsync(BackupArtifact artifact, string destinationPath, CancellationToken cancellationToken)
    {
        if (artifact.StorageType != Type || string.IsNullOrWhiteSpace(artifact.Path))
        {
            throw NotFound();
        }

        // The caller's token stops the download; so does taking longer than allowed.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Settings.UploadTimeoutSeconds));

        bool found;
        try
        {
            // The key is the one recorded for the backup, in the server's own bucket.
            found = await s3.DownloadFileAsync(Settings.Bucket, artifact.Path, destinationPath, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BackupOperationException(
                BackupErrorCodes.BackupStorageTimeout, "The backup could not be downloaded in time.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BackupOperationException(
                BackupErrorCodes.BackupStorageFailed, "The backup could not be read from the backup storage.", exception);
        }

        if (!found)
        {
            throw NotFound();
        }

        static BackupOperationException NotFound() =>
            new(BackupErrorCodes.BackupArtifactNotFound, "The backup's artifact is not in the backup storage.");
    }

    /// <summary>The local file a validated backup is uploaded from. Determined by the backup's id, like its key.</summary>
    private string VerifiedPathFor(BackupLocation location) =>
        Path.Combine(StagingDirectory, $"{location.BackupId:D}.{location.Extension}");

    /// <summary>Empty file that marks the object at the backup's key as one that failed verification.</summary>
    private string RejectedMarkerFor(BackupLocation location) => VerifiedPathFor(location) + RejectedSuffix;

    private void SetRejected(BackupLocation location, bool rejected)
    {
        try
        {
            if (rejected)
            {
                File.WriteAllBytes(RejectedMarkerFor(location), []);
            }
            else
            {
                File.Delete(RejectedMarkerFor(location));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(exception, "The verification marker of backup {BackupId} could not be updated", location.BackupId);
        }
    }

    private void RemoveLocalFiles(BackupLocation location)
    {
        var verifiedPath = VerifiedPathFor(location);
        foreach (var path in new[] { verifiedPath + StagingSuffix, verifiedPath })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left behind; the next attempt for this backup removes it before it starts.
                logger.LogWarning(exception, "A local backup file of backup {BackupId} could not be removed", location.BackupId);
            }
        }
    }

    private static void EnsureValid(BackupLocation location)
    {
        if (!ExtensionPattern().IsMatch(location.Extension))
        {
            throw new ArgumentException("The extension must consist of lowercase letters and digits.", nameof(location));
        }
    }

    private static string ContentTypeOf(BackupLocation location) =>
        location.Extension == "sql" ? "application/sql" : "application/octet-stream";

    [GeneratedRegex("^[a-z0-9]{1,16}$")]
    private static partial Regex ExtensionPattern();

    private sealed class Staging(S3BackupStorage storage, BackupLocation location, string stagingPath) : IBackupStaging
    {
        public string FilePath => stagingPath;

        public async Task<BackupArtifact> CommitAsync(CancellationToken cancellationToken)
        {
            var settings = storage.Settings;
            var key = storage.KeyFor(location);
            var verifiedPath = storage.VerifiedPathFor(location);

            long sizeBytes;
            try
            {
                using (var file = new FileStream(stagingPath, System.IO.FileMode.Open, FileAccess.ReadWrite))
                {
                    sizeBytes = file.Length;
                    if (sizeBytes == 0)
                    {
                        throw new BackupOperationException(BackupErrorCodes.BackupArtifactInvalid, "The backup is empty.");
                    }

                    file.Flush(flushToDisk: true);
                }

                // From here on it is a validated backup, and is named as one. What is uploaded is
                // never a file that is still called .partial.
                File.Move(stagingPath, verifiedPath, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new BackupOperationException(
                    BackupErrorCodes.BackupStorageFailed, "The backup could not be staged for upload.", exception);
            }

            // The caller's token stops the upload; so does taking longer than allowed.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.UploadTimeoutSeconds));

            S3ObjectInfo? stored;
            try
            {
                await storage.Client.UploadFileAsync(
                    new S3Upload(
                        settings.Bucket,
                        key,
                        verifiedPath,
                        ContentTypeOf(location),
                        new Dictionary<string, string>
                        {
                            ["aurora-backup-id"] = location.BackupId.ToString("D"),
                            ["aurora-database-id"] = location.DatabaseId.ToString("D"),
                            ["aurora-instance-id"] = location.InstanceId.ToString("D")
                        }),
                    timeout.Token);

                stored = await storage.Client.FindObjectAsync(settings.Bucket, key, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BackupOperationException(
                    BackupErrorCodes.BackupStorageTimeout, "The backup could not be uploaded in time.");
            }

            // The store's word that the upload succeeded is not enough to call the object a backup.
            if (stored is null || stored.SizeBytes != sizeBytes)
            {
                storage.Log.LogWarning(
                    "After its upload the object of backup {BackupId} is {StoredSizeBytes} bytes, expected {SizeBytes}",
                    location.BackupId, stored?.SizeBytes, sizeBytes);
                storage.SetRejected(location, rejected: stored is not null);
                throw new BackupOperationException(
                    BackupErrorCodes.BackupStorageVerificationFailed,
                    "The uploaded backup could not be verified in the backup storage.");
            }

            storage.SetRejected(location, rejected: false);
            return new BackupArtifact(storage.Type, key, stored.SizeBytes);
        }

        // After a verified upload the local copy is no longer the backup; after a failed attempt
        // it is of no further use, because the next attempt dumps again.
        public ValueTask DisposeAsync()
        {
            storage.RemoveLocalFiles(location);
            return ValueTask.CompletedTask;
        }
    }
}
