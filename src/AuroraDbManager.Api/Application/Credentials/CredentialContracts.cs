using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Application.Credentials;

/// <summary>
/// What there is to know about the credential Aurora manages for an instance: the account, and
/// where its password stands. Never the password: that is in one response only, <see cref="CredentialRotationResultResponse"/>.
/// </summary>
/// <param name="InstanceId">The instance.</param>
/// <param name="Engine">The instance's engine.</param>
/// <param name="Username">The database administrator the credential belongs to: <c>postgres</c> or <c>root</c>.</param>
/// <param name="Managed">Whether Aurora holds a password for that account. False until the instance has been provisioned.</param>
/// <param name="Rotation">
/// <c>idle</c>; <c>in_progress</c> while a rotation is pending or running; <c>incomplete</c> when
/// the last rotation failed before the new password took effect everywhere. Rotating again finishes it.
/// </param>
/// <param name="LastRotatedAt">UTC time the password was last rotated; null if it never was.</param>
/// <param name="LatestJob">The most recent <c>rotate_credential</c> job of the instance, whatever became of it; null if there never was one.</param>
/// <param name="Result">Whether the password of the last completed rotation can still be retrieved; null if no rotation's result is on record.</param>
public sealed record InstanceCredentialResponse(
    Guid InstanceId,
    InstanceEngine Engine,
    string Username,
    bool Managed,
    CredentialRotationState Rotation,
    DateTime? LastRotatedAt,
    JobResponse? LatestJob,
    CredentialResultStatusResponse? Result);

/// <summary>Where the one-time result of a completed rotation stands. Never the password.</summary>
/// <param name="JobId">The rotation it is the result of.</param>
/// <param name="State"><c>available</c>, <c>retrieved</c> or <c>expired</c>.</param>
/// <param name="ExpiresAt">UTC time until which it can be retrieved, if it has not been.</param>
public sealed record CredentialResultStatusResponse(Guid JobId, CredentialResultState State, DateTime ExpiresAt);

public enum CredentialResultState
{
    /// <summary>The new password can be retrieved, once.</summary>
    Available,

    /// <summary>It was retrieved. It cannot be retrieved again.</summary>
    Retrieved,

    /// <summary>Nobody retrieved it in time. The password works; Aurora no longer hands it out.</summary>
    Expired,

    /// <summary>There is nothing to retrieve for this job: it did not complete, or a newer rotation has replaced its password.</summary>
    Unavailable
}

/// <summary>
/// The result of a completed rotation: the one response that carries an instance's password. It
/// is given once.
/// </summary>
/// <param name="InstanceId">The instance.</param>
/// <param name="JobId">The rotation this is the result of.</param>
/// <param name="Engine">The instance's engine.</param>
/// <param name="Username">The database administrator: <c>postgres</c> or <c>root</c>.</param>
/// <param name="Password">The administrator's password as of that rotation. It cannot be retrieved again.</param>
public sealed record CredentialRotationResultResponse(
    Guid InstanceId,
    Guid JobId,
    InstanceEngine Engine,
    string Username,
    string Password)
{
    // A record prints its members. This one must not: it would put the password wherever it is logged.
    public override string ToString() => $"{nameof(CredentialRotationResultResponse)} {{ InstanceId = {InstanceId}, JobId = {JobId} }}";
}

public enum CredentialRotationState
{
    Idle,
    InProgress,
    Incomplete
}

/// <param name="Credential">The credential as it stands now that a rotation has been accepted.</param>
/// <param name="Job">The job that rotates the password. Follow it with <c>GET /api/v1/jobs/{id}</c>.</param>
public sealed record CredentialRotationResponse(InstanceCredentialResponse Credential, JobResponse Job);
