namespace AuroraDbManager.Api.Application.Backups;

/// <summary>Error codes of failed backups. They appear on failed jobs and failed backups.</summary>
public static class BackupErrorCodes
{
    /// <summary>The backup program is not installed, cannot be started, or is too old for the instance.</summary>
    public const string BackupToolUnavailable = "BACKUP_TOOL_UNAVAILABLE";

    /// <summary>The database is not ready, its instance is not running, or its server cannot be located.</summary>
    public const string BackupDatabaseUnavailable = "BACKUP_DATABASE_UNAVAILABLE";

    /// <summary>The database server was located but the backup program could not connect to it.</summary>
    public const string BackupConnectionFailed = "BACKUP_CONNECTION_FAILED";
    public const string BackupOperationTimeout = "BACKUP_OPERATION_TIMEOUT";

    /// <summary>The backup program ran and reported a failure.</summary>
    public const string BackupProcessFailed = "BACKUP_PROCESS_FAILED";

    /// <summary>The artifact could not be written to, or finalized in, the backup storage.</summary>
    public const string BackupStorageFailed = "BACKUP_STORAGE_FAILED";

    /// <summary>The backup is not in the status the job starts from, or is not the job's.</summary>
    public const string BackupInvalidState = "BACKUP_INVALID_STATE";

    /// <summary>The backup program reported success but what it wrote is not a complete backup.</summary>
    public const string BackupArtifactInvalid = "BACKUP_ARTIFACT_INVALID";
}
