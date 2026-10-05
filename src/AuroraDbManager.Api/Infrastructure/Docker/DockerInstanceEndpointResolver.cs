using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Docker;

/// <summary>
/// Resolves an instance to the address of its container on the Aurora network and the engine's
/// standard port.
/// </summary>
/// <remarks>
/// Instance containers publish no ports. Their databases are reachable only on the Aurora Docker
/// network, so this process must be able to reach that network: either it runs in a container
/// attached to it, or it runs on a Linux Docker host, where bridge networks are routable from the
/// host. On Docker Desktop (macOS, Windows) container addresses are not reachable from the host,
/// and the API has to run in a container on the network. The address is read from Docker rather
/// than taken from the container's DNS name, because the name only resolves inside the network
/// while the address works in both setups.
/// </remarks>
public sealed class DockerInstanceEndpointResolver(IDockerEngine docker, IOptions<DockerOptions> options) : IInstanceEndpointResolver
{
    public async Task<InstanceEndpoint> ResolveAsync(Instance instance, CancellationToken cancellationToken)
    {
        var containerName = DockerResourceNaming.ContainerName(instance.Id);

        DockerContainer? container;
        try
        {
            container = await docker.FindContainerAsync(containerName, cancellationToken);
        }
        catch (DockerEngineException exception)
        {
            throw Unavailable("The instance's database server could not be located.", exception);
        }

        if (container is null)
        {
            throw Unavailable("The instance's database container no longer exists.");
        }

        // A container that merely has the instance's name is not the instance's database server.
        if (!DockerResourceNaming.IsOwnedBy(container.Labels, instance.Id))
        {
            throw Unavailable("The instance's database container does not belong to this instance.");
        }

        if (container.State != DockerContainerState.Running)
        {
            throw Unavailable("The instance's database server is not running.");
        }

        if (container.NetworkAddresses is null
            || !container.NetworkAddresses.TryGetValue(options.Value.NetworkName, out var address)
            || string.IsNullOrEmpty(address))
        {
            throw Unavailable("The instance's database server is not attached to the instance network.");
        }

        return new InstanceEndpoint(address, EngineDefaults.Port(instance.Engine));
    }

    private static DatabaseOperationException Unavailable(string message, Exception? innerException = null) =>
        new(DatabaseErrorCodes.DatabaseEngineUnavailable, message, innerException);
}
