namespace AuroraDbManager.Api.Application.Restores;

/// <summary>
/// Error codes of failed restores. They appear on failed <c>restore_database</c> jobs. A failure
/// of the backup storage itself is reported under the storage's own <c>BACKUP_STORAGE_*</c> code.
/// </summary>
public static class RestoreErrorCodes
{
    /// <summary>The backup's artifact is not in the backup storage.</summary>
    public const string RestoreArtifactNotFound = "RESTORE_ARTIFACT_NOT_FOUND";

    /// <summary>What was fetched is not the backup that was stored: wrong size, or not a readable dump.</summary>
    public const string RestoreArtifactInvalid = "RESTORE_ARTIFACT_INVALID";

    /// <summary>The restore program is not installed, cannot be started, or is too old for the backup.</summary>
    public const string RestoreToolUnavailable = "RESTORE_TOOL_UNAVAILABLE";

    /// <summary>The database is not ready, its instance is not running, or its server cannot be located.</summary>
    public const string RestoreDatabaseUnavailable = "RESTORE_DATABASE_UNAVAILABLE";

    /// <summary>The database server was located but could not be connected to.</summary>
    public const string RestoreConnectionFailed = "RESTORE_CONNECTION_FAILED";
    public const string RestoreOperationTimeout = "RESTORE_OPERATION_TIMEOUT";

    /// <summary>Emptying the target database or the restore program itself failed.</summary>
    public const string RestoreProcessFailed = "RESTORE_PROCESS_FAILED";

    /// <summary>The backup could not be copied out of the backup storage, or staged locally.</summary>
    public const string RestoreStorageFailed = "RESTORE_STORAGE_FAILED";

    /// <summary>The restore program succeeded, but the database could not be used afterwards.</summary>
    public const string RestoreVerificationFailed = "RESTORE_VERIFICATION_FAILED";

    /// <summary>The backup or database is not in the state a restore starts from, or is not the job's.</summary>
    public const string RestoreInvalidState = "RESTORE_INVALID_STATE";
}
