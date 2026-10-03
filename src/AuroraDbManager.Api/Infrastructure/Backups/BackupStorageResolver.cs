using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Infrastructure.Backups.S3;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Backups;

/// <summary>
/// <see cref="IBackupStorageResolver"/> over the two storages there are. Both always exist; one
/// that is not the default need not be configured for the application to start, and is only
/// found wanting here, when a backup that is in it is actually asked for.
/// </summary>
public sealed class BackupStorageResolver(
    LocalBackupStorage local,
    S3BackupStorage s3,
    IOptions<BackupOptions> options) : IBackupStorageResolver
{
    public IBackupStorage Resolve(BackupStorageType storageType)
    {
        // Never a fallback: a type this server does not know is an error, not another storage.
        IBackupStorage storage = storageType switch
        {
            BackupStorageType.Local => local,
            BackupStorageType.S3 => s3,
            _ => throw new ArgumentOutOfRangeException(nameof(storageType), storageType, "Unknown backup storage type.")
        };

        if (options.Value.StorageProblem(storageType) is { } problem)
        {
            // Which setting is missing is for the operator, in the log; the client is told only
            // that the storage is not available on this server.
            throw new BackupOperationException(
                BackupErrorCodes.BackupStorageNotConfigured,
                "The backup storage the backup belongs to is not configured on the server.",
                new InvalidOperationException(problem));
        }

        return storage;
    }
}
