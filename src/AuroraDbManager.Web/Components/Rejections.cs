using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.BackupSchedules;
using AuroraDbManager.Api.Application.Connectivity;
using AuroraDbManager.Api.Application.Credentials;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Restores;
using AuroraDbManager.Api.Errors;

namespace AuroraDbManager.Web.Components;

/// <summary>Why an application service did not do what it was asked, as a page tells it.</summary>
/// <param name="Status">The HTTP status the page is answered with.</param>
/// <param name="Code">The stable error code, the one the API answers the same refusal with.</param>
/// <param name="Message">The refusal in a sentence.</param>
public sealed record Rejection(int Status, string Code, string Message);

/// <summary>
/// The words for what the services refuse. The services decide; this only says what they
/// decided, with the codes and the sentences the API uses for the same outcomes.
/// </summary>
public static class Rejections
{
    public static Rejection? For(DeleteInstanceResult result) => result switch
    {
        DeleteInstanceResult.Provisioning => Conflict(
            ErrorCodes.InstanceProvisioning, "Instance cannot be deleted while provisioning is in progress."),
        DeleteInstanceResult.DatabaseOperationInProgress => Conflict(
            ErrorCodes.DatabaseOperationInProgress, "Instance cannot be deleted while one of its databases is being created or deleted."),
        DeleteInstanceResult.BackupInProgress => Conflict(
            ErrorCodes.BackupOperationInProgress, "Instance cannot be deleted while one of its databases is being backed up."),
        DeleteInstanceResult.RestoreInProgress => Conflict(
            ErrorCodes.RestoreOperationInProgress, "Instance cannot be deleted while one of its databases is being restored."),
        DeleteInstanceResult.CredentialRotationInProgress => Conflict(
            ErrorCodes.CredentialRotationInProgress, "Instance cannot be deleted while its database password is being rotated."),
        _ => null
    };

    public static Rejection? For(CreateDatabaseStatus status) => status switch
    {
        CreateDatabaseStatus.InstanceNotReady => InstanceNotReady,
        CreateDatabaseStatus.AlreadyExists => Conflict(
            ErrorCodes.DatabaseAlreadyExists, "The instance already has a database with this name."),
        CreateDatabaseStatus.CredentialRotationInProgress => Conflict(ErrorCodes.CredentialRotationInProgress, CredentialRotationErrors.InProgressMessage),
        _ => null
    };

    public static Rejection? For(DeleteDatabaseStatus status) => status switch
    {
        DeleteDatabaseStatus.Creating => Conflict(ErrorCodes.DatabaseCreating, "Database cannot be deleted while it is being created."),
        DeleteDatabaseStatus.Deleting => Conflict(ErrorCodes.DatabaseDeleting, "Database is already being deleted."),
        DeleteDatabaseStatus.Failed => Conflict(ErrorCodes.DatabaseFailed, "Database is in a failed state and cannot be deleted."),
        DeleteDatabaseStatus.InstanceNotReady => InstanceNotReady,
        DeleteDatabaseStatus.BackupInProgress => Conflict(
            ErrorCodes.BackupOperationInProgress, "Database cannot be deleted while a backup of it is in progress."),
        DeleteDatabaseStatus.RestoreInProgress => Conflict(
            ErrorCodes.RestoreOperationInProgress, "Database cannot be deleted while it is being restored."),
        DeleteDatabaseStatus.CredentialRotationInProgress => Conflict(ErrorCodes.CredentialRotationInProgress, CredentialRotationErrors.InProgressMessage),
        _ => null
    };

    public static Rejection? For(CreateBackupStatus status) => status switch
    {
        CreateBackupStatus.DatabaseNotReady => Conflict(ErrorCodes.DatabaseNotReady, "Only a ready database can be backed up."),
        CreateBackupStatus.InstanceNotReady => Conflict(ErrorCodes.InstanceNotReady, "Backups need a running instance."),
        CreateBackupStatus.BackupInProgress => Conflict(
            ErrorCodes.BackupOperationInProgress, "The database already has a backup in progress."),
        CreateBackupStatus.RestoreInProgress => Conflict(
            ErrorCodes.RestoreOperationInProgress, "The database cannot be backed up while it is being restored."),
        CreateBackupStatus.CredentialRotationInProgress => Conflict(ErrorCodes.CredentialRotationInProgress, CredentialRotationErrors.InProgressMessage),
        _ => null
    };

    public static Rejection? For(CreateRestoreStatus status) => status switch
    {
        CreateRestoreStatus.BackupNotCompleted => Conflict(ErrorCodes.BackupNotCompleted, "Only a completed backup can be restored."),
        CreateRestoreStatus.StorageNotConfigured => Conflict(
            ErrorCodes.BackupStorageNotConfigured, "The backup storage the backup belongs to is not configured on the server."),
        CreateRestoreStatus.DatabaseNotReady => Conflict(ErrorCodes.DatabaseNotReady, "A backup can only be restored into a ready database."),
        CreateRestoreStatus.InstanceNotReady => Conflict(ErrorCodes.InstanceNotReady, "Restores need a running instance."),
        CreateRestoreStatus.RestoreInProgress => Conflict(
            ErrorCodes.RestoreOperationInProgress, "The database is already being restored."),
        CreateRestoreStatus.BackupInProgress => Conflict(
            ErrorCodes.BackupOperationInProgress, "The database cannot be restored while it is being backed up."),
        CreateRestoreStatus.CredentialRotationInProgress => Conflict(ErrorCodes.CredentialRotationInProgress, CredentialRotationErrors.InProgressMessage),
        _ => null
    };

    /// <summary>
    /// Why a schedule was not created or changed, for every outcome that is the state of things
    /// rather than of what was typed. What was typed, a cron expression or a time zone the
    /// calculator does not accept, is said at its field: see <see cref="ScheduleFieldError"/>.
    /// </summary>
    public static Rejection? For(BackupScheduleStatus status) => status switch
    {
        BackupScheduleStatus.AlreadyExists => Conflict(
            ErrorCodes.BackupScheduleAlreadyExists, "The database already has a backup schedule."),
        BackupScheduleStatus.DatabaseNotReady => Conflict(
            ErrorCodes.DatabaseNotReady, "Only a ready database can be given a backup schedule."),
        BackupScheduleStatus.InstanceNotReady => Conflict(ErrorCodes.InstanceNotReady, "Backup schedules need a running instance."),
        _ => null
    };

    /// <summary>The field a schedule request was refused for, and the sentence the API answers the same refusal with.</summary>
    public static (string Field, string Message)? ScheduleFieldError(BackupScheduleStatus status) => status switch
    {
        BackupScheduleStatus.InvalidCronExpression => (
            nameof(BackupScheduleRequest.CronExpression),
            "cronExpression must be a cron expression of five fields: minute, hour, day of month, month, day of week."),
        BackupScheduleStatus.InvalidTimeZone => (
            nameof(BackupScheduleRequest.TimeZoneId),
            "timeZoneId must be the IANA name of a time zone, such as 'Asia/Dhaka' or 'UTC'."),
        _ => null
    };

    /// <summary>External access was not changed. The status, code and sentence are the ones the API answers with.</summary>
    public static Rejection For(ExternalAccessResult result)
    {
        var (status, code, message) = ExternalAccessErrors.For(result);
        return new Rejection(status, code, message);
    }

    /// <summary>The password was not queued for rotation. The status, code and sentence are the ones the API answers with.</summary>
    public static Rejection For(RotateCredentialStatus status)
    {
        var (httpStatus, code, message) = CredentialRotationErrors.For(status);
        return new Rejection(httpStatus, code, message);
    }

    /// <summary>The new password was not handed out. The status, code and sentence are the ones the API answers with.</summary>
    public static Rejection For(RetrieveCredentialStatus status)
    {
        var (httpStatus, code, message) = CredentialRotationErrors.For(status);
        return new Rejection(httpStatus, code, message);
    }

    /// <summary>The instance's resources could not be removed; nothing was deleted. The exception's code and message are written for clients.</summary>
    public static Rejection For(InstanceProvisioningException exception) =>
        new(StatusCodes.Status503ServiceUnavailable, exception.Code, exception.Message);

    private static Rejection InstanceNotReady =>
        Conflict(ErrorCodes.InstanceNotReady, "Database operations need a running instance.");

    private static Rejection Conflict(string code, string message) => new(StatusCodes.Status409Conflict, code, message);
}
