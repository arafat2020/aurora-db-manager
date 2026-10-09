using AuroraDbManager.Api.Errors;

namespace AuroraDbManager.Api.Application.Credentials;

/// <summary>
/// What a refused rotation is answered with: the status, the stable code and the sentence. In one
/// place, for the API and for the pages alike.
/// </summary>
public static class CredentialRotationErrors
{
    /// <summary>What other operations are refused with while a rotation is unfinished.</summary>
    public const string InProgressMessage = "The instance's database password is being rotated. Try again when that has finished.";

    /// <summary>For every outcome but <see cref="RotateCredentialStatus.Accepted"/> and <see cref="RotateCredentialStatus.NotFound"/>.</summary>
    public static (int Status, string Code, string Message) For(RotateCredentialStatus status) => status switch
    {
        RotateCredentialStatus.InstanceNotReady => Conflict(
            ErrorCodes.InstanceNotReady, "The password can only be rotated while the instance is running."),
        RotateCredentialStatus.NotManaged => Conflict(
            ErrorCodes.CredentialNotManaged, "Aurora holds no password for this instance."),
        RotateCredentialStatus.RotationInProgress => Conflict(
            ErrorCodes.CredentialRotationInProgress, "The password of this instance is already being rotated."),
        RotateCredentialStatus.DatabaseOperationInProgress => Conflict(
            ErrorCodes.DatabaseOperationInProgress,
            "The password cannot be rotated while one of the instance's databases is being created or deleted."),
        RotateCredentialStatus.BackupInProgress => Conflict(
            ErrorCodes.BackupOperationInProgress,
            "The password cannot be rotated while one of the instance's databases is being backed up."),
        _ => Conflict(
            ErrorCodes.RestoreOperationInProgress,
            "The password cannot be rotated while one of the instance's databases is being restored.")
    };

    /// <summary>For every outcome but <see cref="RetrieveCredentialStatus.Retrieved"/> and <see cref="RetrieveCredentialStatus.NotFound"/>.</summary>
    public static (int Status, string Code, string Message) For(RetrieveCredentialStatus status) => status switch
    {
        RetrieveCredentialStatus.AlreadyRetrieved => Conflict(
            ErrorCodes.CredentialResultAlreadyRetrieved, "Credential already retrieved. It cannot be displayed again."),
        RetrieveCredentialStatus.Expired => Conflict(
            ErrorCodes.CredentialResultExpired,
            "The time in which the new password could be retrieved has passed. The password still works; rotate it again to get one you can retrieve."),
        _ => Conflict(
            ErrorCodes.CredentialResultNotAvailable,
            "This rotation has no password to retrieve: it did not complete, or the password has been rotated again since.")
    };

    private static (int, string, string) Conflict(string code, string message) => (StatusCodes.Status409Conflict, code, message);
}
