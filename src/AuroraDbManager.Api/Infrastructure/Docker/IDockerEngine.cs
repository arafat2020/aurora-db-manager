namespace AuroraDbManager.Api.Infrastructure.Docker;

/// <summary>
/// The Docker operations provisioning needs, and nothing else. All Docker SDK calls live behind
/// this interface. Every method throws <see cref="DockerEngineException"/> on failure.
/// </summary>
public interface IDockerEngine
{
    Task<bool> NetworkExistsAsync(string name, CancellationToken cancellationToken);

    Task CreateNetworkAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken);

    /// <summary>Returns the volume, or null if there is none with that name.</summary>
    Task<DockerVolume?> FindVolumeAsync(string name, CancellationToken cancellationToken);

    Task CreateVolumeAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken);

    /// <summary>Removes the volume and its data. Does nothing if it does not exist.</summary>
    Task RemoveVolumeAsync(string name, CancellationToken cancellationToken);

    Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken);

    Task PullImageAsync(string image, CancellationToken cancellationToken);

    /// <summary>Returns the container, or null if there is none with that name.</summary>
    Task<DockerContainer?> FindContainerAsync(string name, CancellationToken cancellationToken);

    Task CreateContainerAsync(DockerContainerSpec spec, CancellationToken cancellationToken);

    Task StartContainerAsync(string name, CancellationToken cancellationToken);

    /// <summary>Stops and removes the container; named volumes are kept. Does nothing if it does not exist.</summary>
    Task RemoveContainerAsync(string name, CancellationToken cancellationToken);

    /// <summary>Runs a command inside a running container and returns its exit code.</summary>
    Task<int> ExecAsync(string containerName, IReadOnlyList<string> command, CancellationToken cancellationToken);
}

public sealed record DockerVolume(string Name, IReadOnlyDictionary<string, string> Labels);

public sealed record DockerContainer(
    string Name,
    string Image,
    DockerContainerState State,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<DockerMount> Mounts);

/// <param name="VolumeName">Name of the mounted volume.</param>
/// <param name="Target">Path inside the container.</param>
public sealed record DockerMount(string VolumeName, string Target);

public enum DockerContainerState
{
    /// <summary>Created but never started.</summary>
    Created,
    Running,

    /// <summary>Was running and has stopped.</summary>
    Exited,

    /// <summary>Paused, restarting, being removed or dead.</summary>
    Other
}

public enum DockerFailure
{
    /// <summary>The Docker Engine could not be reached.</summary>
    Unavailable,
    NotFound,
    Conflict,

    /// <summary>The Docker Engine rejected or failed the operation.</summary>
    Failed
}

/// <summary>
/// A Docker operation failed. The message and inner exception are for logs only; they are never
/// shown to API clients.
/// </summary>
public sealed class DockerEngineException(DockerFailure kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public DockerFailure Kind { get; } = kind;
}
