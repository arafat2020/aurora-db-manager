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

    /// <summary>The backup is in a storage the server has no usable settings for.</summary>
    public const string BackupStorageNotConfigured = "BACKUP_STORAGE_NOT_CONFIGURED";

    /// <summary>The object storage could not be reached, or answered with a server error.</summary>
    public const string BackupStorageUnavailable = "BACKUP_STORAGE_UNAVAILABLE";

    /// <summary>The object storage rejected the server's credentials, or none could be found.</summary>
    public const string BackupStorageAuthFailed = "BACKUP_STORAGE_AUTH_FAILED";
    public const string BackupStorageBucketNotFound = "BACKUP_STORAGE_BUCKET_NOT_FOUND";

    /// <summary>The object storage was reached and refused or failed the upload.</summary>
    public const string BackupStorageUploadFailed = "BACKUP_STORAGE_UPLOAD_FAILED";
    public const string BackupStorageTimeout = "BACKUP_STORAGE_TIMEOUT";

    /// <summary>The upload was reported as done, but the stored object is not what was uploaded.</summary>
    public const string BackupStorageVerificationFailed = "BACKUP_STORAGE_VERIFICATION_FAILED";

    /// <summary>What the storage holds after the backup was stored does not have the checksum of what was written.</summary>
    public const string BackupChecksumMismatch = "BACKUP_CHECKSUM_MISMATCH";

    /// <summary>The backup's artifact is not in the storage, at the place its metadata names.</summary>
    public const string BackupArtifactNotFound = "BACKUP_ARTIFACT_NOT_FOUND";

    /// <summary>The backup is not in the status the job starts from, or is not the job's.</summary>
    public const string BackupInvalidState = "BACKUP_INVALID_STATE";

    /// <summary>The backup program reported success but what it wrote is not a complete backup.</summary>
    public const string BackupArtifactInvalid = "BACKUP_ARTIFACT_INVALID";
}
