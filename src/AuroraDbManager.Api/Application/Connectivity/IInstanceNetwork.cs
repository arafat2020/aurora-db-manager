using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Application.Connectivity;

/// <summary>
/// What the application needs to know about where instances are on the network, without knowing
/// how they are hosted.
/// </summary>
public interface IInstanceNetwork
{
    /// <summary>The name of the network instances are attached to, the one other containers join to reach them.</summary>
    string NetworkName { get; }

    /// <summary>The name an instance's database server answers to on that network.</summary>
    string InternalHost(Instance instance);

    /// <summary>
    /// Every host port something is published on right now, whoever published it. A port can be
    /// taken without being listed, by a process that is not a container; publishing on it then
    /// fails, and that is reported when it happens.
    /// </summary>
    /// <exception cref="Instances.InstanceProvisioningException">The runtime could not be asked.</exception>
    Task<IReadOnlySet<int>> PublishedHostPortsAsync(CancellationToken cancellationToken);
}
