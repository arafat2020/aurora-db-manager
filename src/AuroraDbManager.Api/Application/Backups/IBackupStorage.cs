using AuroraDbManager.Api.Domain.Backups;

namespace AuroraDbManager.Api.Application.Backups;

/// <summary>
/// Where backup artifacts are kept. An artifact is addressed by ids only; the storage alone
/// decides what that means as a path, so nothing a client supplies ever becomes part of one.
/// </summary>
/// <remarks>
/// An artifact comes into existence in one step. It is written to a staging file, which is not an
/// artifact, and becomes one only through <see cref="IBackupStaging.CommitAsync"/>. Whatever
/// <see cref="FindAsync"/> returns is therefore complete.
/// </remarks>
public interface IBackupStorage
{
    BackupStorageType Type { get; }

    /// <summary>Returns the finished artifact at <paramref name="location"/>, or null if there is none.</summary>
    Task<BackupArtifact?> FindAsync(BackupLocation location, CancellationToken cancellationToken);

    /// <summary>
    /// Starts writing the artifact for <paramref name="location"/>: discards what an earlier
    /// attempt left unfinished and provides an empty local file to write to.
    /// </summary>
    Task<IBackupStaging> BeginAsync(BackupLocation location, CancellationToken cancellationToken);

    /// <summary>Removes the artifact at <paramref name="location"/> and anything unfinished for it. Succeeds if there is nothing.</summary>
    Task DeleteAsync(BackupLocation location, CancellationToken cancellationToken);
}

/// <summary>An artifact being written. Disposing it without committing discards what was written.</summary>
public interface IBackupStaging : IAsyncDisposable
{
    /// <summary>The local file the backup program writes to. Exists, is empty, and is private to this attempt.</summary>
    string FilePath { get; }

    /// <summary>Turns the staging file into the artifact, atomically, and returns the artifact as it then is.</summary>
    Task<BackupArtifact> CommitAsync(CancellationToken cancellationToken);
}

/// <summary>The identity of an artifact: whose it is, and what kind of file.</summary>
/// <param name="InstanceId">The instance of the backed-up database.</param>
/// <param name="DatabaseId">The backed-up database.</param>
/// <param name="BackupId">The backup the artifact belongs to.</param>
/// <param name="Extension">File extension without the dot, lowercase letters and digits only, e.g. <c>dump</c>.</param>
public sealed record BackupLocation(Guid InstanceId, Guid DatabaseId, Guid BackupId, string Extension);
