namespace AuroraDbManager.Api.Application.Instances;

/// <summary>
/// Keeps instance credentials out of the <c>Instance</c> entity and therefore out of every
/// instance API response.
/// </summary>
public interface IInstanceSecretStore
{
    /// <summary>
    /// Returns the password of the instance's database administrator, generating and durably
    /// storing one on first use. Later calls return the same password.
    /// </summary>
    Task<string> GetOrCreateAdminPasswordAsync(Guid instanceId, CancellationToken cancellationToken);
}
