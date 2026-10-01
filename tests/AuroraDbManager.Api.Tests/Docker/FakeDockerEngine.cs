using AuroraDbManager.Api.Infrastructure.Docker;

namespace AuroraDbManager.Api.Tests.Docker;

/// <summary>In-memory Docker Engine: keeps networks, volumes, images and containers in collections.</summary>
public sealed class FakeDockerEngine : IDockerEngine
{
    private readonly Dictionary<string, DockerFailure> _failures = [];

    public HashSet<string> Networks { get; } = [];

    public HashSet<string> Images { get; } = [];

    public Dictionary<string, DockerVolume> Volumes { get; } = [];

    public Dictionary<string, DockerContainer> Containers { get; } = [];

    /// <summary>Every operation in call order, as "Operation argument".</summary>
    public List<string> Calls { get; } = [];

    public DockerContainerSpec? LastCreatedSpec { get; private set; }

    /// <summary>When true every operation fails as if the daemon could not be reached.</summary>
    public bool Unavailable { get; set; }

    /// <summary>When true a started container exits immediately, like a database that crashes on startup.</summary>
    public bool ContainersExitAfterStart { get; set; }

    /// <summary>Exit code of commands run in containers; 0 means the database is ready.</summary>
    public Func<int> Exec { get; set; } = () => 0;

    public int CountCalls(string operation) => Calls.Count(call => call.StartsWith(operation + " ", StringComparison.Ordinal));

    /// <summary>Makes every call of <paramref name="operation"/> (a method name) fail.</summary>
    public void FailOn(string operation, DockerFailure kind = DockerFailure.Failed) => _failures[operation] = kind;

    public void AddContainer(DockerContainer container) => Containers[container.Name] = container;

    public Task<bool> NetworkExistsAsync(string name, CancellationToken cancellationToken)
    {
        Record(nameof(NetworkExistsAsync), name, cancellationToken);
        return Task.FromResult(Networks.Contains(name));
    }

    public Task CreateNetworkAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken)
    {
        Record(nameof(CreateNetworkAsync), name, cancellationToken);
        Networks.Add(name);
        return Task.CompletedTask;
    }

    public Task<DockerVolume?> FindVolumeAsync(string name, CancellationToken cancellationToken)
    {
        Record(nameof(FindVolumeAsync), name, cancellationToken);
        return Task.FromResult(Volumes.GetValueOrDefault(name));
    }

    public Task CreateVolumeAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken)
    {
        Record(nameof(CreateVolumeAsync), name, cancellationToken);
        Volumes[name] = new DockerVolume(name, labels);
        return Task.CompletedTask;
    }

    public Task RemoveVolumeAsync(string name, CancellationToken cancellationToken)
    {
        Record(nameof(RemoveVolumeAsync), name, cancellationToken);
        Volumes.Remove(name);
        return Task.CompletedTask;
    }

    public Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken)
    {
        Record(nameof(ImageExistsAsync), image, cancellationToken);
        return Task.FromResult(Images.Contains(image));
    }

    public Task PullImageAsync(string image, CancellationToken cancellationToken)
    {
        Record(nameof(PullImageAsync), image, cancellationToken);
        Images.Add(image);
        return Task.CompletedTask;
    }

    public Task<DockerContainer?> FindContainerAsync(string name, CancellationToken cancellationToken)
    {
        Record(nameof(FindContainerAsync), name, cancellationToken);
        return Task.FromResult(Containers.GetValueOrDefault(name));
    }

    public Task CreateContainerAsync(DockerContainerSpec spec, CancellationToken cancellationToken)
    {
        Record(nameof(CreateContainerAsync), spec.Name, cancellationToken);
        if (Containers.ContainsKey(spec.Name))
        {
            throw new DockerEngineException(DockerFailure.Conflict, "name already in use");
        }

        LastCreatedSpec = spec;
        Containers[spec.Name] = new DockerContainer(
            spec.Name,
            spec.Image,
            DockerContainerState.Created,
            spec.Labels,
            [new DockerMount(spec.VolumeName, spec.VolumeTarget)]);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DockerLabelledResource>> ListContainersAsync(string label, string value, CancellationToken cancellationToken)
    {
        Record(nameof(ListContainersAsync), label, cancellationToken);
        return Task.FromResult<IReadOnlyList<DockerLabelledResource>>(Containers.Values
            .Where(container => container.Labels.GetValueOrDefault(label) == value)
            .Select(container => new DockerLabelledResource(container.Name, container.Labels))
            .ToList());
    }

    public Task<IReadOnlyList<DockerLabelledResource>> ListVolumesAsync(string label, string value, CancellationToken cancellationToken)
    {
        Record(nameof(ListVolumesAsync), label, cancellationToken);
        return Task.FromResult<IReadOnlyList<DockerLabelledResource>>(Volumes.Values
            .Where(volume => volume.Labels.GetValueOrDefault(label) == value)
            .Select(volume => new DockerLabelledResource(volume.Name, volume.Labels))
            .ToList());
    }

    public Task StartContainerAsync(string name, CancellationToken cancellationToken)
    {
        Record(nameof(StartContainerAsync), name, cancellationToken);
        Containers[name] = Containers[name] with
        {
            State = ContainersExitAfterStart ? DockerContainerState.Exited : DockerContainerState.Running
        };
        return Task.CompletedTask;
    }

    public Task RemoveContainerAsync(string name, CancellationToken cancellationToken)
    {
        Record(nameof(RemoveContainerAsync), name, cancellationToken);
        Containers.Remove(name);
        return Task.CompletedTask;
    }

    public Task<int> ExecAsync(string containerName, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        Record(nameof(ExecAsync), $"{containerName}: {string.Join(' ', command)}", cancellationToken);
        return Task.FromResult(Exec());
    }

    private void Record(string operation, string argument, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add($"{operation} {argument}");

        if (Unavailable)
        {
            throw new DockerEngineException(
                DockerFailure.Unavailable,
                "Docker could not be reached",
                new IOException("connect ENOENT /var/run/docker.sock raw-daemon-detail"));
        }

        if (_failures.TryGetValue(operation, out var kind))
        {
            throw new DockerEngineException(kind, $"{operation} failed", new InvalidOperationException("raw-daemon-detail"));
        }
    }
}
