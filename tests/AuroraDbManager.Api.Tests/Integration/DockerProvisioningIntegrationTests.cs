using AuroraDbManager.Api.Application.Connectivity;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Docker;
using AuroraDbManager.Api.Tests.Docker;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Tests.Integration;

/// <summary>
/// Provisions real PostgreSQL and MySQL containers through the real <see cref="DockerEngine"/>.
/// Opt-in: see <see cref="DockerFactAttribute"/>. Each test uses its own network and removes
/// everything it created.
/// </summary>
[Trait("Category", "DockerIntegration")]
public sealed class DockerProvisioningIntegrationTests : IAsyncLifetime
{
    private readonly DockerOptions _options = new()
    {
        NetworkName = $"aurora-db-test-{Guid.NewGuid():N}",
        ReadinessTimeoutSeconds = 180,
        ReadinessPollIntervalMilliseconds = 500
    };

    private readonly List<Instance> _instances = [];
    private readonly InMemoryInstanceSecretStore _secrets = new();
    private readonly DockerEngine _engine;
    private readonly DockerClient _client;

    public DockerProvisioningIntegrationTests()
    {
        _engine = new DockerEngine(Options.Create(_options));
        _client = DockerClientFactory.Create(_options);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable(DockerFactAttribute.EnableVariable) == "1")
        {
            foreach (var instance in _instances)
            {
                await _engine.RemoveContainerAsync(DockerResourceNaming.ContainerName(instance.Id), default);
                await _engine.RemoveVolumeAsync(DockerResourceNaming.VolumeName(instance.Id), default);
            }

            if (await _engine.NetworkExistsAsync(_options.NetworkName, default))
            {
                await _client.Networks.DeleteNetworkAsync(_options.NetworkName);
            }
        }

        _client.Dispose();
        _engine.Dispose();
    }

    private DockerInstanceProvisioner Provisioner(int? readinessTimeoutSeconds = null) => new(
        _engine,
        new DockerImageResolver(),
        _secrets,
        Options.Create(new DockerOptions
        {
            NetworkName = _options.NetworkName,
            ReadinessTimeoutSeconds = readinessTimeoutSeconds ?? _options.ReadinessTimeoutSeconds,
            ReadinessPollIntervalMilliseconds = _options.ReadinessPollIntervalMilliseconds
        }),
        Options.Create(new ExternalAccessOptions()),
        TimeProvider.System,
        NullLogger<DockerInstanceProvisioner>.Instance);

    private Instance NewInstance(InstanceEngine engine, string version, int memoryMb)
    {
        var instance = Instance.Create("integration", engine, version, 1, memoryMb, 1, DateTime.UtcNow);
        _instances.Add(instance);
        return instance;
    }

    private Task<int> RunInContainerAsync(Instance instance, string shellCommand) =>
        _engine.ExecAsync(DockerResourceNaming.ContainerName(instance.Id), ["sh", "-c", shellCommand], default);

    // Logs in over TCP with the password the container was initialized with, which is in its environment.
    private const string PostgresLogin = "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -tAc 'select 1'";
    private const string MysqlLogin = "mysql -h 127.0.0.1 -uroot -p\"$MYSQL_ROOT_PASSWORD\" -e 'select 1'";

    [DockerFact]
    public async Task Postgres_ProvisionsAReadyContainerWithVolumeNetworkLimitsAndLabels()
    {
        var instance = NewInstance(InstanceEngine.Postgres, "16", memoryMb: 512);

        await Provisioner().ProvisionAsync(instance, default);

        var container = await _client.Containers.InspectContainerAsync(DockerResourceNaming.ContainerName(instance.Id));
        Assert.True(container.State.Running);
        Assert.Equal("postgres:16", container.Config.Image);
        Assert.Equal(1_000_000_000L, container.HostConfig.NanoCPUs);
        Assert.Equal(512L * 1024 * 1024, container.HostConfig.Memory);
        Assert.Equal(instance.Id.ToString(), container.Config.Labels[DockerResourceNaming.InstanceIdLabel]);
        Assert.Contains(_options.NetworkName, container.NetworkSettings.Networks.Keys);
        var mount = Assert.Single(container.Mounts, m => m.Destination == "/var/lib/postgresql/data");
        Assert.Equal("volume", mount.Type);
        Assert.Equal(DockerResourceNaming.VolumeName(instance.Id), mount.Name);

        Assert.Equal(0, await RunInContainerAsync(instance, PostgresLogin));
    }

    [DockerFact]
    public async Task InstanceContainers_GetNoHostPrivileges_NoHostPaths_AndNoHostPorts()
    {
        foreach (var (engine, version) in new[] { (InstanceEngine.Postgres, "16"), (InstanceEngine.Mysql, "8.4") })
        {
            var instance = NewInstance(engine, version, memoryMb: engine == InstanceEngine.Mysql ? 1024 : 512);

            // The database still initializes and accepts connections under these restrictions.
            await Provisioner().ProvisionAsync(instance, default);

            var container = await _client.Containers.InspectContainerAsync(DockerResourceNaming.ContainerName(instance.Id));
            Assert.True(container.State.Running);
            Assert.False(container.HostConfig.Privileged);
            Assert.Contains("no-new-privileges:true", container.HostConfig.SecurityOpt);
            Assert.True(container.HostConfig.CapAdd is null or { Count: 0 });
            Assert.True(container.HostConfig.Devices is null or { Count: 0 });
            // Nothing of the host's filesystem, the Docker socket least of all: one named volume.
            Assert.True(container.HostConfig.Binds is null or { Count: 0 });
            Assert.Equal("volume", Assert.Single(container.Mounts).Type);
            // Nothing published on the host, and none of the host's namespaces.
            Assert.False(container.HostConfig.PublishAllPorts);
            Assert.True(container.HostConfig.PortBindings is null or { Count: 0 });
            Assert.Equal(_options.NetworkName, container.HostConfig.NetworkMode);
            Assert.NotEqual("host", container.HostConfig.PidMode);
            Assert.NotEqual("host", container.HostConfig.IpcMode);
        }
    }

    [DockerFact]
    public async Task Postgres_RepeatedProvisioning_AdoptsRunningStoppedAndVolumeOnlyStates()
    {
        var instance = NewInstance(InstanceEngine.Postgres, "16", memoryMb: 512);
        var name = DockerResourceNaming.ContainerName(instance.Id);
        var provisioner = Provisioner();

        await provisioner.ProvisionAsync(instance, default);
        var firstId = (await _client.Containers.InspectContainerAsync(name)).ID;
        Assert.Equal(0, await RunInContainerAsync(instance, "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -c 'create table kept (id int)'"));

        // Already running: nothing is recreated.
        await provisioner.ProvisionAsync(instance, default);
        Assert.Equal(firstId, (await _client.Containers.InspectContainerAsync(name)).ID);

        // Stopped: the same container is started again.
        await _client.Containers.StopContainerAsync(name, new ContainerStopParameters { WaitBeforeKillSeconds = 30 });
        await provisioner.ProvisionAsync(instance, default);
        var restarted = await _client.Containers.InspectContainerAsync(name);
        Assert.Equal(firstId, restarted.ID);
        Assert.True(restarted.State.Running);

        // Container gone, volume left: a new container reuses the volume and its data.
        await _engine.RemoveContainerAsync(name, default);
        await provisioner.ProvisionAsync(instance, default);
        Assert.NotEqual(firstId, (await _client.Containers.InspectContainerAsync(name)).ID);
        Assert.Equal(0, await RunInContainerAsync(instance, "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -c 'select * from kept'"));
    }

    [DockerFact]
    public async Task Mysql_ProvisionsAReadyContainerWithVolumeAndLimits()
    {
        var instance = NewInstance(InstanceEngine.Mysql, "8.4", memoryMb: 1024);

        await Provisioner().ProvisionAsync(instance, default);

        var container = await _client.Containers.InspectContainerAsync(DockerResourceNaming.ContainerName(instance.Id));
        Assert.True(container.State.Running);
        Assert.Equal("mysql:8.4", container.Config.Image);
        Assert.Equal(1_000_000_000L, container.HostConfig.NanoCPUs);
        Assert.Equal(1024L * 1024 * 1024, container.HostConfig.Memory);
        Assert.Contains(_options.NetworkName, container.NetworkSettings.Networks.Keys);
        var mount = Assert.Single(container.Mounts, m => m.Destination == "/var/lib/mysql");
        Assert.Equal(DockerResourceNaming.VolumeName(instance.Id), mount.Name);

        // Ready means the real server with the generated root password, not the image's init-time server.
        Assert.Equal(0, await RunInContainerAsync(instance, MysqlLogin));
    }

    [DockerFact]
    public async Task ReadinessTimeout_RemovesTheContainerAndVolumeOfTheFailedAttempt()
    {
        var instance = NewInstance(InstanceEngine.Mysql, "8.4", memoryMb: 1024);

        // MySQL cannot initialize a data directory within a second.
        var exception = await Assert.ThrowsAsync<InstanceProvisioningException>(
            () => Provisioner(readinessTimeoutSeconds: 1).ProvisionAsync(instance, default));

        Assert.Equal("DATABASE_READINESS_TIMEOUT", exception.Code);
        Assert.Null(await _engine.FindContainerAsync(DockerResourceNaming.ContainerName(instance.Id), default));
        Assert.Null(await _engine.FindVolumeAsync(DockerResourceNaming.VolumeName(instance.Id), default));
        Assert.True(await _engine.NetworkExistsAsync(_options.NetworkName, default));
    }

    [DockerFact]
    public async Task Deprovision_RemovesContainerAndVolume()
    {
        var instance = NewInstance(InstanceEngine.Postgres, "16", memoryMb: 512);
        var provisioner = Provisioner();
        await provisioner.ProvisionAsync(instance, default);

        await provisioner.DeprovisionAsync(instance, default);
        await provisioner.DeprovisionAsync(instance, default);

        Assert.Null(await _engine.FindContainerAsync(DockerResourceNaming.ContainerName(instance.Id), default));
        Assert.Null(await _engine.FindVolumeAsync(DockerResourceNaming.VolumeName(instance.Id), default));
    }
}
