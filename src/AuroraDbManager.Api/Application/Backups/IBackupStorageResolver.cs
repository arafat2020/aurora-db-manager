using AuroraDbManager.Api.Domain.Backups;

namespace AuroraDbManager.Api.Application.Backups;

/// <summary>
/// Finds the storage an existing backup is in. A backup records the storage it was made for, and
/// that record, not the server's current default, decides where its artifact is written, found
/// and read from. The default (<see cref="IBackupStorage"/> as injected) is only what a backup
/// that does not exist yet is created for.
/// </summary>
public interface IBackupStorageResolver
{
    /// <summary>The storage for backups of <paramref name="storageType"/>.</summary>
    /// <exception cref="BackupOperationException">
    /// With <see cref="BackupErrorCodes.BackupStorageNotConfigured"/> if the server has no usable
    /// settings for that storage.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The storage type is not one the server knows.</exception>
    IBackupStorage Resolve(BackupStorageType storageType);
}
