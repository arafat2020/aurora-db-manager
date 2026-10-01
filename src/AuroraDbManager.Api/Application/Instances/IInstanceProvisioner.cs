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
    /// Makes sure the database of an already provisioned <paramref name="instance"/> is up: does
    /// nothing if it is, and starts it and waits for it to accept connections if it is stopped.
    /// Never creates or replaces anything; if the instance's resources are missing or are not its
    /// own, it throws <see cref="InstanceProvisioningException"/>.
    /// </summary>
    Task EnsureRunningAsync(Instance instance, CancellationToken cancellationToken);

    /// <summary>Lists every resource the provisioner manages, whether or not its instance still exists.</summary>
    Task<IReadOnlyList<ProvisionedResource>> ListResourcesAsync(CancellationToken cancellationToken);

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

    /// <summary>
    /// True when the provisioner could not be reached at all. The operation then says nothing about
    /// the state of the instance's resources.
    /// </summary>
    public bool ProvisionerUnavailable { get; init; }
}

/// <param name="Kind">What the resource is, e.g. <c>container</c> or <c>volume</c>.</param>
/// <param name="Name">The resource's name in the provisioner.</param>
/// <param name="InstanceId">The instance it was created for; null if that cannot be told.</param>
public sealed record ProvisionedResource(string Kind, string Name, Guid? InstanceId);
