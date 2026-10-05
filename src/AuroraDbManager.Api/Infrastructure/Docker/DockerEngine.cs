using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Docker;

/// <summary>
/// <see cref="IDockerEngine"/> over the Docker Engine API. Holds one client for the lifetime of
/// the application; the client is created on first use and reused for every operation.
/// </summary>
public sealed class DockerEngine(IOptions<DockerOptions> options) : IDockerEngine, IDisposable
{
    private readonly Lazy<DockerClient> _client = new(() => DockerClientFactory.Create(options.Value));

    private DockerClient Client => _client.Value;

    public Task PingAsync(CancellationToken cancellationToken) =>
        InvokeAsync("ping", cancellationToken, () => Client.System.PingAsync(cancellationToken));

    public Task<bool> NetworkExistsAsync(string name, CancellationToken cancellationToken) =>
        InvokeAsync($"inspect network {name}", cancellationToken, async () =>
        {
            // Listing by name matches substrings, so the exact name is checked here.
            var networks = await Client.Networks.ListNetworksAsync(
                new NetworksListParameters
                {
                    Filters = new Dictionary<string, IDictionary<string, bool>> { ["name"] = new Dictionary<string, bool> { [name] = true } }
                },
                cancellationToken);
            return networks.Any(network => network.Name == name);
        });

    public Task CreateNetworkAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken) =>
        InvokeAsync($"create network {name}", cancellationToken, () =>
            Client.Networks.CreateNetworkAsync(
                new NetworksCreateParameters { Name = name, Driver = "bridge", Labels = labels.ToDictionary() },
                cancellationToken));

    public Task<DockerVolume?> FindVolumeAsync(string name, CancellationToken cancellationToken) =>
        FindAsync($"inspect volume {name}", cancellationToken, async () =>
        {
            var volume = await Client.Volumes.InspectAsync(name, cancellationToken);
            return new DockerVolume(volume.Name, AsReadOnly(volume.Labels));
        });

    public Task CreateVolumeAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken) =>
        InvokeAsync($"create volume {name}", cancellationToken, () =>
            Client.Volumes.CreateAsync(
                new VolumesCreateParameters { Name = name, Driver = "local", Labels = labels.ToDictionary() },
                cancellationToken));

    public Task RemoveVolumeAsync(string name, CancellationToken cancellationToken) =>
        IgnoreNotFoundAsync($"remove volume {name}", cancellationToken, () =>
            Client.Volumes.RemoveAsync(name, force: false, cancellationToken));

    public Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken) =>
        InvokeAsync($"inspect image {image}", cancellationToken, async () =>
        {
            try
            {
                await Client.Images.InspectImageAsync(image, cancellationToken);
                return true;
            }
            catch (DockerImageNotFoundException)
            {
                return false;
            }
        });

    public Task PullImageAsync(string image, CancellationToken cancellationToken) =>
        InvokeAsync($"pull image {image}", cancellationToken, async () =>
        {
            var separator = image.LastIndexOf(':');
            var progress = new PullProgress();

            await Client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image[..separator], Tag = image[(separator + 1)..] },
                authConfig: null,
                progress,
                cancellationToken);

            // A failed pull is reported inside the progress stream rather than as a failed request.
            if (progress.Error is not null)
            {
                throw new DockerEngineException(DockerFailure.Failed, $"Docker failed to pull image {image}: {progress.Error}");
            }

            return true;
        });

    public Task<DockerContainer?> FindContainerAsync(string name, CancellationToken cancellationToken) =>
        FindAsync($"inspect container {name}", cancellationToken, async () =>
        {
            var container = await Client.Containers.InspectContainerAsync(name, cancellationToken);
            return new DockerContainer(
                container.Name.TrimStart('/'),
                container.Config.Image,
                ToState(container.State),
                AsReadOnly(container.Config.Labels),
                (container.Mounts ?? [])
                    .Where(mount => mount.Type == "volume")
                    .Select(mount => new DockerMount(mount.Name, mount.Destination))
                    .ToList(),
                (container.NetworkSettings?.Networks ?? new Dictionary<string, EndpointSettings>())
                    .ToDictionary(network => network.Key, network => network.Value.IPAddress ?? string.Empty),
                PortBindingsOf(container.HostConfig));
        });

    public Task CreateContainerAsync(DockerContainerSpec spec, CancellationToken cancellationToken) =>
        InvokeAsync($"create container {spec.Name}", cancellationToken, () =>
            Client.Containers.CreateContainerAsync(
                new CreateContainerParameters
                {
                    Name = spec.Name,
                    Image = spec.Image,
                    Env = spec.Environment.Select(variable => $"{variable.Key}={variable.Value}").ToList(),
                    Labels = spec.Labels.ToDictionary(),
                    ExposedPorts = spec.PortBinding is null
                        ? null
                        : new Dictionary<string, EmptyStruct> { [TcpPort(spec.PortBinding.ContainerPort)] = default },
                    HostConfig = new HostConfig
                    {
                        NanoCPUs = spec.NanoCpus,
                        Memory = spec.MemoryBytes,
                        NetworkMode = spec.NetworkName,
                        // Stated, not left to defaults: never privileged, no way to gain privileges
                        // after start, and no port of the host but the one the spec names, if it names
                        // one, on the address it names. The only mount is the named volume below.
                        Privileged = false,
                        SecurityOpt = ["no-new-privileges:true"],
                        PublishAllPorts = false,
                        PortBindings = spec.PortBinding is null
                            ? null
                            : new Dictionary<string, IList<PortBinding>>
                            {
                                [TcpPort(spec.PortBinding.ContainerPort)] =
                                [
                                    new PortBinding
                                    {
                                        HostIP = spec.PortBinding.HostAddress,
                                        HostPort = spec.PortBinding.HostPort.ToString(CultureInfo.InvariantCulture)
                                    }
                                ]
                            },
                        Mounts =
                        [
                            new Mount { Type = "volume", Source = spec.VolumeName, Target = spec.VolumeTarget }
                        ]
                    }
                },
                cancellationToken));

    public Task<IReadOnlyList<DockerLabelledResource>> ListContainersAsync(string label, string value, CancellationToken cancellationToken) =>
        InvokeAsync<IReadOnlyList<DockerLabelledResource>>($"list containers labelled {label}", cancellationToken, async () =>
        {
            var containers = await Client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true, Filters = LabelFilter(label, value) },
                cancellationToken);
            return containers
                .Select(container => new DockerLabelledResource(
                    container.Names.FirstOrDefault()?.TrimStart('/') ?? container.ID,
                    AsReadOnly(container.Labels)))
                .ToList();
        });

    public Task<IReadOnlyList<DockerLabelledResource>> ListVolumesAsync(string label, string value, CancellationToken cancellationToken) =>
        InvokeAsync<IReadOnlyList<DockerLabelledResource>>($"list volumes labelled {label}", cancellationToken, async () =>
        {
            var response = await Client.Volumes.ListAsync(
                new VolumesListParameters { Filters = LabelFilter(label, value) },
                cancellationToken);
            return (response.Volumes ?? [])
                .Select(volume => new DockerLabelledResource(volume.Name, AsReadOnly(volume.Labels)))
                .ToList();
        });

    public Task StartContainerAsync(string name, CancellationToken cancellationToken) =>
        InvokeAsync($"start container {name}", cancellationToken, () =>
            Client.Containers.StartContainerAsync(name, new ContainerStartParameters(), cancellationToken));

    public Task StopContainerAsync(string name, CancellationToken cancellationToken) =>
        InvokeAsync($"stop container {name}", cancellationToken, () =>
            // A database server needs its time to write out what it holds before it is killed.
            Client.Containers.StopContainerAsync(
                name,
                new ContainerStopParameters { WaitBeforeKillSeconds = StopTimeoutSeconds },
                cancellationToken));

    public Task RenameContainerAsync(string name, string newName, CancellationToken cancellationToken) =>
        InvokeAsync($"rename container {name}", cancellationToken, () =>
            Client.Containers.RenameContainerAsync(name, new ContainerRenameParameters { NewName = newName }, cancellationToken));

    public Task<IReadOnlySet<int>> ListPublishedHostPortsAsync(CancellationToken cancellationToken) =>
        InvokeAsync<IReadOnlySet<int>>("list published ports", cancellationToken, async () =>
        {
            var containers = await Client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, cancellationToken);
            return containers
                .SelectMany(container => container.Ports ?? [])
                .Where(port => port.PublicPort != 0)
                .Select(port => (int)port.PublicPort)
                .ToHashSet();
        });

    public Task RemoveContainerAsync(string name, CancellationToken cancellationToken) =>
        IgnoreNotFoundAsync($"remove container {name}", cancellationToken, () =>
            // RemoveVolumes only covers anonymous volumes; the instance's named volume is kept.
            Client.Containers.RemoveContainerAsync(
                name,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = true },
                cancellationToken));

    public Task<int> ExecAsync(string containerName, IReadOnlyList<string> command, CancellationToken cancellationToken) =>
        InvokeAsync($"exec in container {containerName}", cancellationToken, async () =>
        {
            var exec = await Client.Exec.ExecCreateContainerAsync(
                containerName,
                new ContainerExecCreateParameters { Cmd = command.ToList(), AttachStdout = true, AttachStderr = true },
                cancellationToken);

            // The output stream ends when the command exits; its content is not needed.
            using (var output = await Client.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, cancellationToken))
            {
                await output.CopyOutputToAsync(Stream.Null, Stream.Null, Stream.Null, cancellationToken);
            }

            var result = await Client.Exec.InspectContainerExecAsync(exec.ID, cancellationToken);
            return (int)result.ExitCode;
        });

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    private const uint StopTimeoutSeconds = 60;

    private static string TcpPort(int port) => $"{port.ToString(CultureInfo.InvariantCulture)}/tcp";

    // What the container is configured to publish, which is there whether or not it is running.
    private static List<DockerPortBinding> PortBindingsOf(HostConfig? hostConfig) =>
        (hostConfig?.PortBindings ?? new Dictionary<string, IList<PortBinding>>())
            .SelectMany(entry => (entry.Value ?? []).Select(binding => new DockerPortBinding(
                int.TryParse(entry.Key.Split('/')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var containerPort) ? containerPort : 0,
                // Docker leaves the address out when a port is bound on every interface.
                string.IsNullOrEmpty(binding.HostIP) ? "0.0.0.0" : binding.HostIP,
                int.TryParse(binding.HostPort, NumberStyles.None, CultureInfo.InvariantCulture, out var hostPort) ? hostPort : 0)))
            .ToList();

    // How the Docker Engine, on Linux and in Docker Desktop, says that a host port is taken.
    private static bool SaysPortIsTaken(DockerApiException exception) =>
        new[] { "port is already allocated", "address already in use", "ports are not available" }
            .Any(phrase => (exception.ResponseBody ?? exception.Message).Contains(phrase, StringComparison.OrdinalIgnoreCase));

    private static DockerContainerState ToState(ContainerState state) => state.Status switch
    {
        "created" => DockerContainerState.Created,
        "running" => DockerContainerState.Running,
        "exited" => DockerContainerState.Exited,
        _ => DockerContainerState.Other
    };

    private static Dictionary<string, IDictionary<string, bool>> LabelFilter(string label, string value) =>
        new() { ["label"] = new Dictionary<string, bool> { [$"{label}={value}"] = true } };

    private static IReadOnlyDictionary<string, string> AsReadOnly(IDictionary<string, string>? labels) =>
        labels is null ? new Dictionary<string, string>() : new Dictionary<string, string>(labels);

    private static async Task<T?> FindAsync<T>(string operation, CancellationToken cancellationToken, Func<Task<T>> action)
        where T : class
    {
        try
        {
            return await InvokeAsync(operation, cancellationToken, action);
        }
        catch (DockerEngineException exception) when (exception.Kind == DockerFailure.NotFound)
        {
            return null;
        }
    }

    private static async Task IgnoreNotFoundAsync(string operation, CancellationToken cancellationToken, Func<Task> action)
    {
        try
        {
            await InvokeAsync(operation, cancellationToken, action);
        }
        catch (DockerEngineException exception) when (exception.Kind == DockerFailure.NotFound)
        {
        }
    }

    private static Task InvokeAsync(string operation, CancellationToken cancellationToken, Func<Task> action) =>
        InvokeAsync(operation, cancellationToken, async () =>
        {
            await action();
            return true;
        });

    /// <summary>Runs a Docker call and turns whatever it throws into a <see cref="DockerEngineException"/>.</summary>
    private static async Task<T> InvokeAsync<T>(string operation, CancellationToken cancellationToken, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DockerEngineException)
        {
            throw;
        }
        catch (DockerApiException exception) when (SaysPortIsTaken(exception))
        {
            throw new DockerEngineException(DockerFailure.PortUnavailable, $"Docker could not {operation}: a host port is taken", exception);
        }
        catch (DockerApiException exception)
        {
            var kind = exception.StatusCode switch
            {
                HttpStatusCode.NotFound => DockerFailure.NotFound,
                HttpStatusCode.Conflict => DockerFailure.Conflict,
                HttpStatusCode.ServiceUnavailable => DockerFailure.Unavailable,
                _ => DockerFailure.Failed
            };
            throw new DockerEngineException(kind, $"Docker could not {operation}: {exception.StatusCode}", exception);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or SocketException or IOException or TimeoutException or OperationCanceledException)
        {
            // Connection refused, missing socket, or a request that timed out.
            throw new DockerEngineException(DockerFailure.Unavailable, $"Docker could not be reached to {operation}", exception);
        }
    }

    /// <summary>Synchronous progress sink; <see cref="Progress{T}"/> would report after the pull has returned.</summary>
    private sealed class PullProgress : IProgress<JSONMessage>
    {
        public string? Error { get; private set; }

        public void Report(JSONMessage value)
        {
            if (!string.IsNullOrEmpty(value.ErrorMessage))
            {
                Error ??= value.ErrorMessage;
            }
        }
    }
}
