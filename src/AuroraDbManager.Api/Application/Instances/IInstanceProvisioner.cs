using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Application.Instances;

/// <summary>Brings the real database server behind an instance into existence.</summary>
public interface IInstanceProvisioner
{
    /// <summary>
    /// Provisions <paramref name="instance"/> and returns once it is ready to use. May be called
    /// again for the same instance after a failed or interrupted attempt, so implementations must
    /// be idempotent. Throw <see cref="InstanceProvisioningException"/> to report a failure whose
    /// message is safe to show to API clients.
    /// </summary>
    Task ProvisionAsync(Instance instance, CancellationToken cancellationToken);
}

/// <summary>Provisioning failed. The message is returned to API clients.</summary>
public sealed class InstanceProvisioningException(string message, Exception? innerException = null)
    : Exception(message, innerException);
