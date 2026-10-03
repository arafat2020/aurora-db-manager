namespace AuroraDbManager.Api.Domain.Backups;

/// <summary>Where a backup's artifact is kept.</summary>
public enum BackupStorageType
{
    /// <summary>A file on the filesystem of the machine running the API.</summary>
    Local
}
