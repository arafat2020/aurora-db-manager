using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Application.Backups;

/// <summary>
/// Produces the artifact of a backup. There is one implementation per <see cref="InstanceEngine"/>;
/// how an engine is dumped, and with which program, is its business alone.
/// </summary>
public interface IBackupManager
{
    /// <summary>The engine this implementation backs up databases of.</summary>
    InstanceEngine Engine { get; }

    /// <summary>
    /// Backs up <paramref name="database"/> of <paramref name="instance"/> as the artifact of
    /// <paramref name="backup"/> and returns once the artifact is complete and in its final place.
    /// One attempt, no retries. May be called again for the same backup after a failed or
    /// interrupted attempt: an artifact that an earlier attempt finished is returned as it is,
    /// and anything an earlier attempt left unfinished is discarded.
    /// </summary>
    /// <exception cref="BackupOperationException">The attempt failed. Nothing of it is left in the final place.</exception>
    Task<BackupArtifact> BackupAsync(Instance instance, Database database, Backup backup, CancellationToken cancellationToken);
}

/// <param name="StorageType">The storage the artifact is in.</param>
/// <param name="Path">Where it is, in terms of that storage.</param>
/// <param name="SizeBytes">Its actual size there.</param>
public sealed record BackupArtifact(BackupStorageType StorageType, string Path, long SizeBytes);
