namespace AuroraDbManager.Api.Domain.Backups;

/// <summary>Where a backup's artifact is kept.</summary>
public enum BackupStorageType
{
    /// <summary>A file on the filesystem of the machine running the API.</summary>
    Local,

    /// <summary>An object in a bucket of an S3-compatible object store.</summary>
    S3
}
