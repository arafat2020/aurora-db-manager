using AuroraDbManager.Api.Domain.Backups;

namespace AuroraDbManager.Api.Application.Backups;

/// <summary>
/// Where backup artifacts are kept. An artifact is addressed by ids only; the storage alone
/// decides what that means as a path or an object key, so nothing a client supplies ever becomes
/// part of one.
/// </summary>
/// <remarks>
/// An artifact comes into existence in one step. It is written to a staging file, which is not an
/// artifact, and becomes one only through <see cref="IBackupStaging.CommitAsync"/>. Whatever
/// <see cref="FindAsync"/> returns is therefore complete.
/// </remarks>
public interface IBackupStorage
{
    BackupStorageType Type { get; }

    /// <summary>
    /// Returns the finished artifact at <paramref name="location"/>, with the checksum of its
    /// bytes as they are in the storage, or null if there is none that can be vouched for.
    /// </summary>
    Task<BackupArtifact?> FindAsync(BackupLocation location, CancellationToken cancellationToken);

    /// <summary>
    /// Starts writing the artifact for <paramref name="location"/>: discards what an earlier
    /// attempt left unfinished and provides an empty local file to write to.
    /// </summary>
    Task<IBackupStaging> BeginAsync(BackupLocation location, CancellationToken cancellationToken);

    /// <summary>
    /// Copies a finished artifact of this storage into the local file <paramref name="destinationPath"/>,
    /// replacing it. The artifact is only read, never changed or removed. The copy is streamed;
    /// an artifact is never held in memory as a whole.
    /// </summary>
    /// <param name="artifact">
    /// The artifact as a backup's metadata records it. Not anything a client supplied. Its
    /// checksum is not checked here; the caller checks what it received.
    /// </param>
    /// <param name="destinationPath">A local file of the caller's.</param>
    /// <param name="cancellationToken">Stops the copy; what was written so far is the caller's to remove.</param>
    /// <exception cref="BackupOperationException">
    /// With <see cref="BackupErrorCodes.BackupArtifactNotFound"/> if the artifact is not there.
    /// </exception>
    Task DownloadAsync(BackupArtifact artifact, string destinationPath, CancellationToken cancellationToken);
}

/// <summary>An artifact being written. Disposing it without committing discards what was written.</summary>
public interface IBackupStaging : IAsyncDisposable
{
    /// <summary>The local file the backup program writes to. Exists, is empty, and is private to this attempt.</summary>
    string FilePath { get; }

    /// <summary>
    /// Turns the staging file into the artifact, atomically, and returns the artifact as it then
    /// is in the storage. The staging file must be a complete, validated backup by now. The
    /// artifact is read back from the storage and must have the checksum of the staging file;
    /// what is returned has been verified that way.
    /// </summary>
    /// <param name="checksum">The checksum of the staging file, calculated by the caller.</param>
    /// <param name="cancellationToken">Stops the operation.</param>
    /// <exception cref="BackupOperationException">
    /// With <see cref="BackupErrorCodes.BackupChecksumMismatch"/> if what the storage holds is not
    /// what was written to it.
    /// </exception>
    Task<BackupArtifact> CommitAsync(string checksum, CancellationToken cancellationToken);
}

/// <summary>The identity of an artifact: whose it is, and what kind of file.</summary>
/// <param name="InstanceId">The instance of the backed-up database.</param>
/// <param name="DatabaseId">The backed-up database.</param>
/// <param name="BackupId">The backup the artifact belongs to.</param>
/// <param name="Extension">File extension without the dot, lowercase letters and digits only, e.g. <c>dump</c>.</param>
public sealed record BackupLocation(Guid InstanceId, Guid DatabaseId, Guid BackupId, string Extension);
