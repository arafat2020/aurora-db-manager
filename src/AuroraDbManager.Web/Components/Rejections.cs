using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
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
        _ => null
    };

    public static Rejection? For(CreateDatabaseStatus status) => status switch
    {
        CreateDatabaseStatus.InstanceNotReady => InstanceNotReady,
        CreateDatabaseStatus.AlreadyExists => Conflict(
            ErrorCodes.DatabaseAlreadyExists, "The instance already has a database with this name."),
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
        _ => null
    };

    /// <summary>The instance's resources could not be removed; nothing was deleted. The exception's code and message are written for clients.</summary>
    public static Rejection For(InstanceProvisioningException exception) =>
        new(StatusCodes.Status503ServiceUnavailable, exception.Code, exception.Message);

    private static Rejection InstanceNotReady =>
        Conflict(ErrorCodes.InstanceNotReady, "Database operations need a running instance.");

    private static Rejection Conflict(string code, string message) => new(StatusCodes.Status409Conflict, code, message);
}
