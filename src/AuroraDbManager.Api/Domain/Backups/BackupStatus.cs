namespace AuroraDbManager.Api.Domain.Backups;

/// <summary>State of a backup's artifact.</summary>
public enum BackupStatus
{
    Pending,
    Running,
    Completed,
    Failed
}
