namespace AuroraDbManager.Api.Application.Credentials;

/// <summary>Error codes of a failed password rotation. They appear on failed <c>rotate_credential</c> jobs.</summary>
public static class CredentialErrorCodes
{
    /// <summary>The database server was reached and did not change the password. The stored password is still the one it accepts.</summary>
    public const string DatabaseFailed = "CREDENTIAL_ROTATION_DATABASE_FAILED";

    /// <summary>The password was changed or stored, and the server then did not accept it.</summary>
    public const string VerificationFailed = "CREDENTIAL_ROTATION_VERIFICATION_FAILED";

    /// <summary>The stored password or its replacement could not be read or written.</summary>
    public const string SecretStoreFailed = "CREDENTIAL_ROTATION_SECRET_STORE_FAILED";

    /// <summary>
    /// The server accepts neither the stored password nor its replacement, so its administrator
    /// password is one Aurora does not have. Nothing was changed.
    /// </summary>
    public const string RecoveryRequired = "CREDENTIAL_ROTATION_RECOVERY_REQUIRED";
}
