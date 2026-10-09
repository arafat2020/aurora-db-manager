namespace AuroraDbManager.Api.Application.Instances;

/// <summary>
/// Keeps instance credentials out of the <c>Instance</c> entity and therefore out of every
/// instance API response.
/// </summary>
/// <remarks>
/// An instance has one administrator password that everything of Aurora connects with, and, while
/// that password is being rotated, the one that is to replace it. The replacement is kept here,
/// encrypted like the password itself, from before the database server is told about it until it
/// has taken the password's place: whatever interrupts a rotation, what the server may have been
/// changed to is never lost.
/// </remarks>
public interface IInstanceSecretStore
{
    /// <summary>
    /// Returns the password of the instance's database administrator, generating and durably
    /// storing one on first use. Later calls return the same password.
    /// </summary>
    Task<string> GetOrCreateAdminPasswordAsync(Guid instanceId, CancellationToken cancellationToken);

    /// <summary>What is stored for the instance, without reading any of it.</summary>
    Task<AdminCredentialState> GetAdminCredentialStateAsync(Guid instanceId, CancellationToken cancellationToken);

    /// <summary>
    /// Generates the password that is to replace the administrator password and stores it next to
    /// it. If one is stored already it is kept: the database server may have been changed to it.
    /// A password that was waiting to be handed out is no longer offered: it is about to stop working.
    /// Takes part in the caller's transaction, if there is one.
    /// </summary>
    /// <returns>False if the instance has no stored administrator password to replace.</returns>
    Task<bool> StageAdminPasswordReplacementAsync(Guid instanceId, CancellationToken cancellationToken);

    /// <summary>The stored replacement, or null if there is none.</summary>
    Task<string?> GetAdminPasswordReplacementAsync(Guid instanceId, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the stored replacement the administrator password, in one step. Does nothing if there
    /// is no replacement, so it can be repeated.
    /// </summary>
    Task PromoteAdminPasswordReplacementAsync(Guid instanceId, CancellationToken cancellationToken);

    /// <summary>
    /// Offers the administrator password, as it is stored now, for handing out once, as the result
    /// of the rotation <paramref name="jobId"/>, until <paramref name="expiresAt"/>. Does nothing if
    /// it is already offered, or was already handed out, for that job, so it can be repeated.
    /// </summary>
    Task OfferAdminPasswordAsync(Guid instanceId, Guid jobId, DateTime expiresAt, CancellationToken cancellationToken);

    /// <summary>What is on record about handing out the administrator password; null if it was never offered or no longer is.</summary>
    Task<AdminPasswordDelivery?> GetAdminPasswordDeliveryAsync(Guid instanceId, CancellationToken cancellationToken);

    /// <summary>
    /// Hands out the administrator password offered for <paramref name="jobId"/>: records that it
    /// was handed out and returns it, or returns null if it is not on offer for that job, has
    /// expired, or was handed out before. Of any number of concurrent calls one gets the password.
    /// </summary>
    /// <remarks>
    /// The record is written before the password is read. A caller therefore wraps the call, and
    /// whatever it does with the password before letting go of it, in a transaction: if anything
    /// fails before that transaction is committed, the password was not handed out and is still on offer.
    /// </remarks>
    Task<string?> ClaimAdminPasswordAsync(Guid instanceId, Guid jobId, DateTime utcNow, CancellationToken cancellationToken);
}

/// <param name="JobId">The completed rotation the password is the result of.</param>
/// <param name="ExpiresAt">Until when it may be handed out.</param>
/// <param name="ConsumedAt">When it was handed out; null if it has not been.</param>
public sealed record AdminPasswordDelivery(Guid JobId, DateTime ExpiresAt, DateTime? ConsumedAt);

/// <summary>What the secret store holds for an instance's database administrator.</summary>
public enum AdminCredentialState
{
    /// <summary>Nothing: the instance has not been given a password, or does not exist.</summary>
    None,

    /// <summary>The administrator password.</summary>
    Stored,

    /// <summary>The administrator password and the one that is to replace it.</summary>
    ReplacementStaged
}
