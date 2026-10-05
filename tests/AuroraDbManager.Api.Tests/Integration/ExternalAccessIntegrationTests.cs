using System.Net;
using System.Runtime.InteropServices;
using AuroraDbManager.Api.Infrastructure.Docker;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Integration;

/// <summary>
/// External access against real PostgreSQL and MySQL containers: the whole application with the
/// real provisioner and the real Docker Engine. Opt-in: see <see cref="DockerFactAttribute"/>.
/// Each test uses its own network and removes everything it created.
/// </summary>
/// <remarks>
/// A published port is on the Docker host, bound to 127.0.0.1, and the test is to connect to it
/// from outside the database container, the way a client on the host would. That is done from a
/// throw-away container on the <b>host</b> network: its 127.0.0.1 is the Docker host's, on a Linux
/// host and in Docker Desktop's machine alike, and it is on none of the Docker networks, so the
/// only way from it to the database is the published port. The engine's own client does the
/// connecting and reads back a row written before the port was published: the data has to be
/// what it was, across the restart that publishing takes.
/// </remarks>
[Trait("Category", "DockerIntegration")]
public sealed class ExternalAccessIntegrationTests : IAsyncLifetime
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable(DockerFactAttribute.EnableVariable) == "1";
    private static readonly bool InContainer = File.Exists("/.dockerenv");

    // A range of its own, away from the default one and from anything a developer's machine is likely to publish.
    private const int RangeStart = 25432;
    private const int RangeEnd = 25440;

    private readonly string _network = $"aurora-db-test-{Guid.NewGuid():N}";
    private readonly List<Guid> _instanceIds = [];
    private readonly List<string> _helperContainers = [];
    private DockerEngine _engine = null!;
    private DockerClient _docker = null!;
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        if (!InContainer && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new InvalidOperationException(
                "On Docker Desktop the instance network is not reachable from the host. "
                + "Run these tests with tests/run-docker-integration-tests.sh, which runs them in a container.");
        }

        var options = new DockerOptions { NetworkName = _network };
        _engine = new DockerEngine(Options.Create(options));
        _docker = DockerClientFactory.Create(options);

        await _engine.CreateNetworkAsync(_network, DockerResourceNaming.NetworkLabels(), default);
        if (InContainer)
        {
            await _docker.Networks.ConnectNetworkAsync(_network, new NetworkConnectParameters { Container = Environment.MachineName });
        }

        // No worker: each test runs the jobs itself, so nothing depends on timing.
        _factory = new ApiFactory
        {
            RealDockerNetwork = _network,
            ConfigureExternalAccess = settings => (settings.PortRangeStart, settings.PortRangeEnd) = (RangeStart, RangeEnd)
        };
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        if (!Enabled || _engine is null)
        {
            return;
        }

        _client?.Dispose();
        _factory?.Dispose();

        foreach (var helper in _helperContainers)
        {
            await _engine.RemoveContainerAsync(helper, default);
        }

        foreach (var instanceId in _instanceIds)
        {
            await _engine.RemoveContainerAsync(DockerResourceNaming.ContainerName(instanceId), default);
            await _engine.RemoveContainerAsync(DockerResourceNaming.ReplacedContainerName(instanceId), default);
            await _engine.RemoveVolumeAsync(DockerResourceNaming.VolumeName(instanceId), default);
        }

        if (InContainer)
        {
            await _docker.Networks.DisconnectNetworkAsync(
                _network, new NetworkDisconnectParameters { Container = Environment.MachineName, Force = true });
        }

        await _docker.Networks.DeleteNetworkAsync(_network);
        _docker.Dispose();
        _engine.Dispose();
    }

    [DockerFact]
    public Task Postgres_PublishedPort_IsReachableFromOutsideTheContainer_WithTheDataItHad_AndGoneAgainWhenDisabled() =>
        PublishConnectUnpublishAsync(
            "postgres", "17", enginePort: 5432,
            write: "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -d app -c \"create table notes (note text); insert into notes values ('kept across the restart')\"",
            readInside: "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -d app -Atc 'select note from notes' | grep -q 'kept across the restart'",
            readOutside: port => $"psql -h 127.0.0.1 -p {port} -U postgres -d app -Atc 'select note from notes'",
            passwordVariable: "PGPASSWORD",
            clientImage: "postgres:17");

    [DockerFact]
    public Task Mysql_PublishedPort_IsReachableFromOutsideTheContainer_WithTheDataItHad_AndGoneAgainWhenDisabled() =>
        PublishConnectUnpublishAsync(
            "mysql", "8.4", enginePort: 3306,
            write: "mysql -h 127.0.0.1 -uroot -p\"$MYSQL_ROOT_PASSWORD\" -e \"create table app.notes (note text); insert into app.notes values ('kept across the restart')\"",
            readInside: "mysql -h 127.0.0.1 -uroot -p\"$MYSQL_ROOT_PASSWORD\" -N -e 'select note from app.notes' | grep -q 'kept across the restart'",
            readOutside: port => $"mysql --protocol=TCP -h 127.0.0.1 -P {port} -uroot -N -e 'select note from app.notes'",
            passwordVariable: "MYSQL_PWD",
            clientImage: "mysql:8.4");

    private async Task PublishConnectUnpublishAsync(
        string engine, string version, int enginePort, string write, string readInside, Func<int, string> readOutside, string passwordVariable, string clientImage)
    {
        var instanceId = await CreateRunningInstanceAsync(engine, version);
        var containerName = DockerResourceNaming.ContainerName(instanceId);
        var volumeName = DockerResourceNaming.VolumeName(instanceId);
        await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        Assert.Equal(0, await ExecAsync(instanceId, write));
        var password = await _factory.AdminPasswordAsync(instanceId);
        var volumeCreated = (await _docker.Volumes.InspectAsync(volumeName)).CreatedAt;
        var privateContainerId = (await _docker.Containers.InspectContainerAsync(containerName)).ID;

        // Private as provisioned: Docker has no port binding for it, and nothing answers on the host.
        Assert.Empty((await _engine.FindContainerAsync(containerName, default))!.PortBindings!);
        Assert.NotEqual(0, (await RunOnHostNetworkAsync(clientImage, readOutside(RangeStart), passwordVariable, password)).ExitCode);

        // --- Enable ---
        var enabled = await (await _client.PostAsync($"{InstancesUrl}/{instanceId}/external-access", null)).ReadJsonAsync(HttpStatusCode.OK);
        var port = enabled.GetProperty("external").GetProperty("port").GetInt32();
        Assert.InRange(port, RangeStart, RangeEnd);

        var published = (await _engine.FindContainerAsync(containerName, default))!;
        Assert.Equal([new DockerPortBinding(enginePort, "127.0.0.1", port)], published.PortBindings);
        Assert.Equal(DockerContainerState.Running, published.State);
        Assert.True(DockerResourceNaming.IsOwnedBy(published.Labels, instanceId));
        Assert.Contains(published.Mounts, mount => mount.VolumeName == volumeName);
        Assert.Contains(port, await _engine.ListPublishedHostPortsAsync(default));
        // A new container on the same volume: the volume is the one that was created at provisioning.
        var publishedContainerId = (await _docker.Containers.InspectContainerAsync(containerName)).ID;
        Assert.NotEqual(privateContainerId, publishedContainerId);
        Assert.Equal(volumeCreated, (await _docker.Volumes.InspectAsync(volumeName)).CreatedAt);
        Assert.Null(await _engine.FindContainerAsync(DockerResourceNaming.ReplacedContainerName(instanceId), default));

        // The real thing: a client outside the container, through the host port, reads what was written before.
        var outside = await RunOnHostNetworkAsync(clientImage, readOutside(port), passwordVariable, password);
        Assert.True(outside.ExitCode == 0, $"Connecting through the published port failed: {outside.Output.Replace(password, "***", StringComparison.Ordinal)}");
        Assert.Contains("kept across the restart", outside.Output, StringComparison.Ordinal);
        // And without the password it gets nowhere: the port is open, the database is not.
        Assert.NotEqual(0, (await RunOnHostNetworkAsync(clientImage, readOutside(port), passwordVariable, "not-the-password")).ExitCode);

        // The instance still works from the inside, for the application as for its clients.
        Assert.Equal("healthy", (await (await _client.GetAsync(InstanceHealthUrl(instanceId))).ReadJsonAsync(HttpStatusCode.OK)).Status());
        await _factory.CreateReadyDatabaseAsync(_client, instanceId, "created_while_published");

        // --- A restart of the application leaves a container that matches its record alone ---
        var report = await _factory.ReconcileAsync();
        Assert.Empty(report.Failed);
        Assert.Equal(publishedContainerId, (await _docker.Containers.InspectContainerAsync(containerName)).ID);

        // --- Disable ---
        var disabled = await (await _client.DeleteAsync($"{InstancesUrl}/{instanceId}/external-access")).ReadJsonAsync(HttpStatusCode.OK);
        Assert.False(disabled.GetProperty("external").GetProperty("enabled").GetBoolean());

        var unpublished = (await _engine.FindContainerAsync(containerName, default))!;
        Assert.Empty(unpublished.PortBindings!);
        Assert.Equal(DockerContainerState.Running, unpublished.State);
        Assert.DoesNotContain(port, await _engine.ListPublishedHostPortsAsync(default));
        Assert.NotEqual(0, (await RunOnHostNetworkAsync(clientImage, readOutside(port), passwordVariable, password)).ExitCode);
        // The data is what it was, and so is the volume.
        Assert.Equal(0, await ExecAsync(instanceId, readInside));
        Assert.Equal(volumeCreated, (await _docker.Volumes.InspectAsync(volumeName)).CreatedAt);
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
    }

    [DockerFact]
    public async Task HostPortTakenByAnotherProcess_IsRefusedByDocker_AndAuroraMovesOnToTheNextPort()
    {
        var instanceId = await CreateRunningInstanceAsync("postgres", "17");
        var containerName = DockerResourceNaming.ContainerName(instanceId);
        // Something that is not a published container port sits on the first port of the range: a
        // server on the host network, which Docker does not list among published ports.
        var squatter = $"aurora-db-test-squatter-{Guid.NewGuid():N}";
        _helperContainers.Add(squatter);
        await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = squatter,
            Image = "postgres:17",
            Env = ["POSTGRES_PASSWORD=squatter"],
            Cmd = ["-p", RangeStart.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            HostConfig = new HostConfig { NetworkMode = "host" }
        });
        await _docker.Containers.StartContainerAsync(squatter, new ContainerStartParameters());
        await WaitUntilAsync(async () =>
            await _engine.ExecAsync(squatter, ["pg_isready", "-q", "-h", "127.0.0.1", "-p", RangeStart.ToString(System.Globalization.CultureInfo.InvariantCulture)], default) == 0);
        Assert.DoesNotContain(RangeStart, await _engine.ListPublishedHostPortsAsync(default));

        var enabled = await (await _client.PostAsync($"{InstancesUrl}/{instanceId}/external-access", null)).ReadJsonAsync(HttpStatusCode.OK);

        // Docker refused the first port, in whatever words this Docker uses; the next one was taken.
        var port = enabled.GetProperty("external").GetProperty("port").GetInt32();
        Assert.Equal(RangeStart + 1, port);
        var container = (await _engine.FindContainerAsync(containerName, default))!;
        Assert.Equal([new DockerPortBinding(5432, "127.0.0.1", port)], container.PortBindings);
        Assert.Equal(DockerContainerState.Running, container.State);
        Assert.Null(await _engine.FindContainerAsync(DockerResourceNaming.ReplacedContainerName(instanceId), default));
        Assert.Equal(0, await _engine.ExecAsync(containerName, ["pg_isready", "-q", "-h", "127.0.0.1", "-p", "5432"], default));
    }

    private async Task<Guid> CreateRunningInstanceAsync(string engine, string version)
    {
        var response = await _client.PostAsync(
            InstancesUrl,
            System.Net.Http.Json.JsonContent.Create(ValidInstanceRequest(engine: engine, version: version, memoryMb: engine == "mysql" ? 1024 : 512, storageGb: 1)));
        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        var instanceId = body.GetProperty("instance").GetProperty("id").GetGuid();
        _instanceIds.Add(instanceId);

        await _factory.ProcessJobAsync(body.GetProperty("job").GetProperty("id").GetGuid());
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
        return instanceId;
    }

    private Task<int> ExecAsync(Guid instanceId, string shellCommand) =>
        _engine.ExecAsync(DockerResourceNaming.ContainerName(instanceId), ["sh", "-c", shellCommand], default);

    /// <summary>
    /// Runs the engine's client in a container of its own on the host network and returns how it
    /// ended and what it printed. The password travels in the client's own environment variable.
    /// </summary>
    private async Task<(long ExitCode, string Output)> RunOnHostNetworkAsync(string image, string shellCommand, string passwordVariable, string password)
    {
        var name = $"aurora-db-test-client-{Guid.NewGuid():N}";
        _helperContainers.Add(name);
        await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Name = name,
            Image = image,
            Entrypoint = ["sh", "-c", shellCommand],
            Env = [$"{passwordVariable}={password}", "PGCONNECT_TIMEOUT=10"],
            HostConfig = new HostConfig { NetworkMode = "host" }
        });

        await _docker.Containers.StartContainerAsync(name, new ContainerStartParameters());
        var exit = await _docker.Containers.WaitContainerAsync(name);
        using var logs = await _docker.Containers.GetContainerLogsAsync(name, tty: false, new ContainerLogsParameters { ShowStdout = true, ShowStderr = true });
        var (stdout, stderr) = await logs.ReadOutputToEndAsync(default);
        await _engine.RemoveContainerAsync(name, default);
        return (exit.StatusCode, stdout + stderr);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (DockerEngineException)
            {
                // Not up yet.
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("The condition was not met in time.");
    }
}
