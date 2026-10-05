using AuroraDbManager.Api.Errors;

namespace AuroraDbManager.Api.Application.Connectivity;

/// <summary>
/// What a refused change of external access is answered with: the status, the stable code and
/// the sentence. In one place, for the API and for the pages alike.
/// </summary>
public static class ExternalAccessErrors
{
    /// <summary>For every outcome but <see cref="ExternalAccessStatus.Changed"/> and <see cref="ExternalAccessStatus.NotFound"/>.</summary>
    public static (int Status, string Code, string Message) For(ExternalAccessResult result) => result.Status switch
    {
        ExternalAccessStatus.AlreadyEnabled => Conflict(
            ErrorCodes.ExternalAccessAlreadyEnabled, "External access is already enabled for this instance."),
        ExternalAccessStatus.AlreadyDisabled => Conflict(
            ErrorCodes.ExternalAccessAlreadyDisabled, "External access is already disabled for this instance."),
        ExternalAccessStatus.InstanceNotReady => Conflict(
            ErrorCodes.InstanceNotReady, "External access can only be changed while the instance is running."),
        ExternalAccessStatus.DatabaseOperationInProgress => Conflict(
            ErrorCodes.DatabaseOperationInProgress,
            "External access cannot be changed while one of the instance's databases is being created or deleted."),
        ExternalAccessStatus.BackupInProgress => Conflict(
            ErrorCodes.BackupOperationInProgress,
            "External access cannot be changed while one of the instance's databases is being backed up."),
        ExternalAccessStatus.RestoreInProgress => Conflict(
            ErrorCodes.RestoreOperationInProgress,
            "External access cannot be changed while one of the instance's databases is being restored."),
        ExternalAccessStatus.PortAllocationFailed => Conflict(
            result.ErrorCode ?? ErrorCodes.PortAllocationFailed,
            result.ErrorMessage ?? "No free host port could be allocated in the configured port range."),
        // Docker could not do it. The code and the message are the provisioner's, written for clients.
        _ => (
            StatusCodes.Status503ServiceUnavailable,
            result.ErrorCode ?? ErrorCodes.InternalError,
            result.ErrorMessage ?? "External access could not be changed.")
    };

    private static (int, string, string) Conflict(string code, string message) => (StatusCodes.Status409Conflict, code, message);
}
