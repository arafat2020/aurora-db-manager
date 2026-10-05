using AuroraDbManager.Api.Application.Connectivity;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Docker;

/// <summary>
/// <see cref="IInstanceNetwork"/> for instances that are Docker containers on one network: a
/// container answers to its name there, and the published ports are the Docker Engine's to tell.
/// </summary>
public sealed class DockerInstanceNetwork(IDockerEngine docker, IOptions<DockerOptions> options) : IInstanceNetwork
{
    public string NetworkName => options.Value.NetworkName;

    public string InternalHost(Instance instance) => DockerResourceNaming.ContainerName(instance.Id);

    public async Task<IReadOnlySet<int>> PublishedHostPortsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await docker.ListPublishedHostPortsAsync(cancellationToken);
        }
        catch (DockerEngineException exception)
        {
            throw exception.Kind == DockerFailure.Unavailable
                ? new InstanceProvisioningException(DockerProvisioningErrors.DockerUnavailable, "Docker is not available.", exception)
                {
                    ProvisionerUnavailable = true
                }
                : new InstanceProvisioningException(
                    DockerProvisioningErrors.DockerOperationFailed, "Docker could not list the published ports.", exception);
        }
    }
}
