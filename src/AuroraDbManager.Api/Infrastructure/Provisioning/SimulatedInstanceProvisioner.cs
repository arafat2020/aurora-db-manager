using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Provisioning;

/// <summary>
/// Stand-in provisioner: waits briefly and always succeeds. It creates nothing — no container,
/// no database server.
/// </summary>
public sealed class SimulatedInstanceProvisioner(
    IOptions<JobOptions> options,
    TimeProvider timeProvider,
    ILogger<SimulatedInstanceProvisioner> logger) : IInstanceProvisioner
{
    public async Task ProvisionAsync(Instance instance, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Simulating provisioning of {Engine} {Version} instance {InstanceId}",
            instance.Engine, instance.Version, instance.Id);

        var delay = TimeSpan.FromMilliseconds(options.Value.SimulatedProvisioningDelayMilliseconds);
        await Task.Delay(delay, timeProvider, cancellationToken);

        logger.LogInformation("Simulated provisioning of instance {InstanceId} finished", instance.Id);
    }
}
