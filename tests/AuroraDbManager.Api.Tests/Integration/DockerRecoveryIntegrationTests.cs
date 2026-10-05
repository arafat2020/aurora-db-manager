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
/// Recovery of real containers through the real <see cref="DockerEngine"/>. Opt-in: see
/// <see cref="DockerFactAttribute"/>. Each test uses its own network and removes everything it created.
/// </summary>
[Trait("Category", "DockerIntegration")]
public sealed class DockerRecoveryIntegrationTests : IAsyncLifetime
{
    private const string Psql = "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -v ON_ERROR_STOP=1 -c ";

    private readonly DockerOptions _options = new()
    {
        NetworkName = $"aurora-db-test-{Guid.NewGuid():N}",
        ReadinessTimeoutSeconds = 180,
        ReadinessPollIntervalMilliseconds = 500
    };

    private readonly List<Guid> _instanceIds = [];
    private readonly DockerEngine _engine;
    private readonly DockerClient _client;
    private readonly DockerInstanceProvisioner _provisioner;

    public DockerRecoveryIntegrationTests()
    {
        _engine = new DockerEngine(Options.Create(_options));
        _client = DockerClientFactory.Create(_options);
        _provisioner = new DockerInstanceProvisioner(
            _engine,
            new DockerImageResolver(),
            new InMemoryInstanceSecretStore(),
            Options.Create(_options),
            Options.Create(new ExternalAccessOptions()),
            TimeProvider.System,
            NullLogger<DockerInstanceProvisioner>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable(DockerFactAttribute.EnableVariable) == "1")
        {
            foreach (var instanceId in _instanceIds)
            {
                await _engine.RemoveContainerAsync(DockerResourceNaming.ContainerName(instanceId), default);
                await _engine.RemoveVolumeAsync(DockerResourceNaming.VolumeName(instanceId), default);
            }

            if (await _engine.NetworkExistsAsync(_options.NetworkName, default))
            {
                await _client.Networks.DeleteNetworkAsync(_options.NetworkName);
            }
        }

        _client.Dispose();
        _engine.Dispose();
    }

    private Instance NewPostgresInstance()
    {
        var instance = Instance.Create("recovery", InstanceEngine.Postgres, "16", 1, 512, 1, DateTime.UtcNow);
        _instanceIds.Add(instance.Id);
        return instance;
    }

    private Task<int> RunSqlAsync(Instance instance, string sql) =>
        _engine.ExecAsync(DockerResourceNaming.ContainerName(instance.Id), ["sh", "-c", $"{Psql}'{sql}'"], default);

    private Task StopContainerAsync(Instance instance) =>
        _client.Containers.StopContainerAsync(
            DockerResourceNaming.ContainerName(instance.Id), new ContainerStopParameters { WaitBeforeKillSeconds = 30 });

    [DockerFact]
    public async Task StoppedContainer_IsStartedAgain_WithTheSameContainerAndItsData()
    {
        var instance = NewPostgresInstance();
        var name = DockerResourceNaming.ContainerName(instance.Id);
        await _provisioner.ProvisionAsync(instance, default);
        Assert.Equal(0, await RunSqlAsync(instance, "create table kept as select 42 as answer"));
        var containerId = (await _client.Containers.InspectContainerAsync(name)).ID;

        // What a Docker or host restart leaves behind.
        await StopContainerAsync(instance);
        Assert.False((await _client.Containers.InspectContainerAsync(name)).State.Running);

        await _provisioner.EnsureRunningAsync(instance, default);

        var container = await _client.Containers.InspectContainerAsync(name);
        Assert.True(container.State.Running);
        Assert.Equal(containerId, container.ID);
        Assert.Equal(0, await RunSqlAsync(instance, "select answer from kept"));

        // Already running: nothing happens, however often it is called.
        await _provisioner.EnsureRunningAsync(instance, default);
        Assert.Equal(containerId, (await _client.Containers.InspectContainerAsync(name)).ID);
    }

    [DockerFact]
    public async Task InterruptedProvisioning_ExistingContainerIsAdopted_AndDataSurvives()
    {
        var instance = NewPostgresInstance();
        var name = DockerResourceNaming.ContainerName(instance.Id);
        await _provisioner.ProvisionAsync(instance, default);
        Assert.Equal(0, await RunSqlAsync(instance, "create table kept as select 42 as answer"));
        var containerId = (await _client.Containers.InspectContainerAsync(name)).ID;
        await StopContainerAsync(instance);

        // The recovered provisioning job calls the provisioner again for the same instance.
        await _provisioner.ProvisionAsync(instance, default);

        var containers = await _engine.ListContainersAsync(DockerResourceNaming.InstanceIdLabel, instance.Id.ToString(), default);
        Assert.Single(containers);
        Assert.Equal(containerId, (await _client.Containers.InspectContainerAsync(name)).ID);
        Assert.Equal(0, await RunSqlAsync(instance, "select answer from kept"));
    }

    [DockerFact]
    public async Task MissingContainer_IsNotReplaced_AndTheVolumeIsKept()
    {
        var instance = NewPostgresInstance();
        await _provisioner.ProvisionAsync(instance, default);
        await _engine.RemoveContainerAsync(DockerResourceNaming.ContainerName(instance.Id), default);

        var exception = await Assert.ThrowsAsync<InstanceProvisioningException>(
            () => _provisioner.EnsureRunningAsync(instance, default));

        Assert.Equal("DATABASE_CONTAINER_MISSING", exception.Code);
        Assert.Null(await _engine.FindContainerAsync(DockerResourceNaming.ContainerName(instance.Id), default));
        Assert.NotNull(await _engine.FindVolumeAsync(DockerResourceNaming.VolumeName(instance.Id), default));
    }

    [DockerFact]
    public async Task ForeignContainerAndVolume_WithTheInstancesNames_AreNeverTouched()
    {
        var instance = NewPostgresInstance();
        var containerName = DockerResourceNaming.ContainerName(instance.Id);
        var volumeName = DockerResourceNaming.VolumeName(instance.Id);

        // Someone else's resources that happen to have Aurora's names but not its labels.
        await _engine.PullImageAsync("postgres:16", default);
        await _client.Volumes.CreateAsync(new VolumesCreateParameters { Name = volumeName });
        var foreign = await _client.Containers.CreateContainerAsync(
            new CreateContainerParameters { Name = containerName, Image = "postgres:16", Cmd = ["sleep", "300"] });
        await _client.Containers.StartContainerAsync(foreign.ID, new ContainerStartParameters());

        var provision = await Assert.ThrowsAsync<InstanceProvisioningException>(() => _provisioner.ProvisionAsync(instance, default));
        var ensure = await Assert.ThrowsAsync<InstanceProvisioningException>(() => _provisioner.EnsureRunningAsync(instance, default));
        await _provisioner.DeprovisionAsync(instance, default);
        var listed = await _provisioner.ListResourcesAsync(default);

        Assert.Equal("DOCKER_RESOURCE_CONFLICT", provision.Code);
        Assert.Equal("DOCKER_RESOURCE_CONFLICT", ensure.Code);
        Assert.DoesNotContain(listed, resource => resource.Name == containerName || resource.Name == volumeName);
        var container = await _client.Containers.InspectContainerAsync(containerName);
        Assert.Equal(foreign.ID, container.ID);
        Assert.True(container.State.Running);
        Assert.NotNull(await _engine.FindVolumeAsync(volumeName, default));
    }

    [DockerFact]
    public async Task ListResources_ReportsManagedContainersAndVolumesWithTheirInstance()
    {
        var instance = NewPostgresInstance();
        await _provisioner.ProvisionAsync(instance, default);

        var resources = await _provisioner.ListResourcesAsync(default);

        Assert.Contains(new ProvisionedResource("container", DockerResourceNaming.ContainerName(instance.Id), instance.Id), resources);
        Assert.Contains(new ProvisionedResource("volume", DockerResourceNaming.VolumeName(instance.Id), instance.Id), resources);
    }
}
