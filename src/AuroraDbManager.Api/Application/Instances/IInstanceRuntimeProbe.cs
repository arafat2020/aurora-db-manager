using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Application.Instances;

/// <summary>
/// Looks at the real database server behind an instance, as it is right now. It only looks: it
/// starts nothing, repairs nothing and changes nothing, in the runtime or in the metadata.
/// </summary>
public interface IInstanceRuntimeProbe
{
    /// <summary>Never throws for what it finds, or cannot find out; that is what the result says.</summary>
    Task<InstanceRuntimeState> InspectAsync(Instance instance, CancellationToken cancellationToken);
}

/// <summary>What was found of an instance's database server.</summary>
public enum InstanceRuntimeState
{
    /// <summary>The instance's container is running and its database accepts connections.</summary>
    Ready,

    /// <summary>The container is running but its database does not accept connections.</summary>
    DatabaseNotReady,

    /// <summary>The container exists but is not running.</summary>
    ContainerNotRunning,

    /// <summary>There is no container that is the instance's own.</summary>
    ContainerMissing,

    /// <summary>The runtime could not be asked, so nothing is known about the instance.</summary>
    RuntimeUnavailable
}
