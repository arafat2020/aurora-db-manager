using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Application.Instances;

/// <summary>Creates and removes the real database server behind an instance.</summary>
public interface IInstanceProvisioner
{
    /// <summary>
    /// Provisions <paramref name="instance"/> and returns once its database accepts connections.
    /// May be called again for the same instance after a failed or interrupted attempt, so
    /// implementations must be idempotent. Throw <see cref="InstanceProvisioningException"/> to
    /// report a failure with a code and message that are safe to show to API clients.
    /// </summary>
    Task ProvisionAsync(Instance instance, CancellationToken cancellationToken);

    /// <summary>
    /// Removes everything <see cref="ProvisionAsync"/> created for <paramref name="instance"/>,
    /// including its data. Succeeds if there is nothing left to remove.
    /// </summary>
    Task DeprovisionAsync(Instance instance, CancellationToken cancellationToken);
}

/// <summary>
/// A provisioning operation failed. <see cref="Code"/> and <see cref="Exception.Message"/> are
/// returned to API clients, so they must not contain internal details or secrets.
/// </summary>
public sealed class InstanceProvisioningException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
