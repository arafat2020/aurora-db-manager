using AuroraDbManager.Api.Application.Connectivity;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Docker;
using AuroraDbManager.Api.Tests.Docker;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Tests.Connectivity;

/// <summary>
/// What the real provisioner does about published ports, over the in-memory Docker Engine: what
/// a container publishes, how it is replaced when that has to change, what happens when the
/// replacement fails or is interrupted, and what recovery does with a container that publishes
/// something else than its instance's record says.
/// </summary>
public sealed class DockerPortPublishingTests
{
    private const string Network = "aurora-db";
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeDockerEngine _docker = new();
    private readonly RecordingLogger<DockerInstanceProvisioner> _logger = new();
    private readonly ExternalAccessOptions _externalAccess = new();
    private readonly DockerInstanceProvisioner _provisioner;

    public DockerPortPublishingTests()
    {
        _provisioner = Provisioner(_externalAccess);
    }

    private DockerInstanceProvisioner Provisioner(ExternalAccessOptions externalAccess) => new(
        _docker,
        new DockerImageResolver(),
        new InMemoryInstanceSecretStore(),
        Options.Create(new DockerOptions { NetworkName = Network, ReadinessTimeoutSeconds = 10, ReadinessPollIntervalMilliseconds = 0 }),
        Options.Create(externalAccess),
        new SteppingTimeProvider(),
        _logger);

    private static string Name(Instance instance) => DockerResourceNaming.ContainerName(instance.Id);

    private static string SetAside(Instance instance) => DockerResourceNaming.ReplacedContainerName(instance.Id);

    private static string Volume(Instance instance) => DockerResourceNaming.VolumeName(instance.Id);

    /// <summary>An instance provisioned the ordinary way: running, private, with its container and volume.</summary>
    private async Task<Instance> ProvisionedAsync(InstanceEngine engine = InstanceEngine.Postgres, string version = "16")
    {
        var instance = Instance.Create("orders", engine, version, 2, 1024, 20, Now);
        await _provisioner.ProvisionAsync(instance, default);
        instance.MarkRunning(Now);
        _docker.Calls.Clear();
        return instance;
    }

    private async Task<Instance> ExposedAsync(int port = 15432, InstanceEngine engine = InstanceEngine.Postgres, string version = "16")
    {
        var instance = await ProvisionedAsync(engine, version);
        instance.EnableExternalAccess(port, Now);
        await _provisioner.ApplyExternalAccessAsync(instance, default);
        _docker.Calls.Clear();
        return instance;
    }

    private IReadOnlyList<DockerPortBinding> Published(Instance instance) => _docker.Containers[Name(instance)].PortBindings ?? [];

    private void AssertVolumeUntouched(Instance instance)
    {
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.RemoveVolumeAsync)));
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.CreateVolumeAsync)));
        Assert.True(DockerResourceNaming.IsOwnedBy(_docker.Volumes[Volume(instance)].Labels, instance.Id));
        Assert.Contains(_docker.Containers[Name(instance)].Mounts, mount => mount.VolumeName == Volume(instance));
    }

    private void AssertNoContainerChanged()
    {
        foreach (var operation in new[]
                 {
                     nameof(IDockerEngine.CreateContainerAsync), nameof(IDockerEngine.RemoveContainerAsync),
                     nameof(IDockerEngine.StopContainerAsync), nameof(IDockerEngine.RenameContainerAsync),
                     nameof(IDockerEngine.RemoveVolumeAsync), nameof(IDockerEngine.CreateVolumeAsync)
                 })
        {
            Assert.Equal(0, _docker.CountCalls(operation));
        }
    }

    private async Task<InstanceProvisioningException> FailsAsync(Func<Task> action, string code)
    {
        var exception = await Assert.ThrowsAsync<InstanceProvisioningException>(action);
        Assert.Equal(code, exception.Code);
        return exception;
    }

    // --- What a container publishes -------------------------------------------------------------

    [Theory]
    [InlineData(InstanceEngine.Postgres, "16")]
    [InlineData(InstanceEngine.Mysql, "8.4")]
    public async Task InstanceWithoutExternalAccess_PublishesNoHostPort(InstanceEngine engine, string version)
    {
        var instance = await ProvisionedAsync(engine, version);

        Assert.Null(_docker.LastCreatedSpec!.PortBinding);
        Assert.Empty(Published(instance));
        Assert.Empty(await _docker.ListPublishedHostPortsAsync(default));
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres, "16", 5432)]
    [InlineData(InstanceEngine.Postgres, "17", 5432)]
    [InlineData(InstanceEngine.Mysql, "8.0", 3306)]
    [InlineData(InstanceEngine.Mysql, "8.4", 3306)]
    public async Task EnablingExternalAccess_PublishesTheEnginesPort_OnTheRecordedHostPort_OnTheConfiguredAddress(
        InstanceEngine engine, string version, int enginePort)
    {
        var instance = await ProvisionedAsync(engine, version);
        instance.EnableExternalAccess(15440, Now);

        await _provisioner.ApplyExternalAccessAsync(instance, default);

        // Exactly one binding, and nothing else of the container is published.
        Assert.Equal([new DockerPortBinding(enginePort, "127.0.0.1", 15440)], Published(instance));
        Assert.Equal(DockerContainerState.Running, _docker.Containers[Name(instance)].State);
        Assert.Equal([15440], await _docker.ListPublishedHostPortsAsync(default));
        // The same instance, on the same data: its labels, its image, its volume, which nothing touched.
        Assert.True(DockerResourceNaming.IsOwnedBy(_docker.Containers[Name(instance)].Labels, instance.Id));
        Assert.Equal(_docker.LastCreatedSpec!.Image, _docker.Containers[Name(instance)].Image);
        AssertVolumeUntouched(instance);
        // The former container is gone, and only after the new one was up and looked at.
        Assert.DoesNotContain(SetAside(instance), _docker.Containers.Keys);
        var calls = _docker.Calls.Select(call => call.Split(' ')[0]).ToList();
        Assert.True(calls.IndexOf(nameof(IDockerEngine.StopContainerAsync)) < calls.IndexOf(nameof(IDockerEngine.RenameContainerAsync)));
        Assert.True(calls.IndexOf(nameof(IDockerEngine.RenameContainerAsync)) < calls.IndexOf(nameof(IDockerEngine.CreateContainerAsync)));
        Assert.True(calls.IndexOf(nameof(IDockerEngine.ExecAsync)) < calls.LastIndexOf(nameof(IDockerEngine.RemoveContainerAsync)));
        Assert.Equal(1, _docker.CountCalls(nameof(IDockerEngine.RemoveContainerAsync)));
        Assert.EndsWith(SetAside(instance), _docker.Calls.Last(call => call.StartsWith(nameof(IDockerEngine.RemoveContainerAsync), StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisablingExternalAccess_RemovesThePublishedPort_AndKeepsTheData()
    {
        var instance = await ExposedAsync();
        instance.DisableExternalAccess(Now);

        await _provisioner.ApplyExternalAccessAsync(instance, default);

        Assert.Empty(Published(instance));
        Assert.Empty(await _docker.ListPublishedHostPortsAsync(default));
        Assert.Equal(DockerContainerState.Running, _docker.Containers[Name(instance)].State);
        AssertVolumeUntouched(instance);
    }

    [Fact]
    public async Task Applying_WhatTheContainerAlreadyPublishes_ChangesNothing()
    {
        var exposed = await ExposedAsync();
        await _provisioner.ApplyExternalAccessAsync(exposed, default);
        AssertNoContainerChanged();

        var privateInstance = await ProvisionedAsync();
        await _provisioner.ApplyExternalAccessAsync(privateInstance, default);
        AssertNoContainerChanged();
    }

    [Fact]
    public async Task BindAddress_IsTheServersSetting_AndNothingElse()
    {
        var provisioner = Provisioner(new ExternalAccessOptions { BindAddress = "10.0.0.5" });
        var instance = await ProvisionedAsync();
        instance.EnableExternalAccess(15432, Now);

        await provisioner.ApplyExternalAccessAsync(instance, default);

        Assert.Equal([new DockerPortBinding(5432, "10.0.0.5", 15432)], Published(instance));
    }

    [Fact]
    public async Task ThePassword_IsInNothingThatIsLogged_WhenAContainerIsReplaced()
    {
        var instance = await ProvisionedAsync();
        var password = _docker.LastCreatedSpec!.Environment["POSTGRES_PASSWORD"];
        instance.EnableExternalAccess(15432, Now);

        await _provisioner.ApplyExternalAccessAsync(instance, default);

        // The same password as before: the data directory was initialized with it.
        Assert.Equal(password, _docker.LastCreatedSpec!.Environment["POSTGRES_PASSWORD"]);
        Assert.DoesNotContain(_logger.Entries, entry => entry.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(_docker.Calls, call => call.Contains(password, StringComparison.Ordinal));
        Assert.Contains(_logger.Entries, entry => entry.Contains("the database is interrupted", StringComparison.Ordinal));
    }

    // --- A replacement that does not work out ---------------------------------------------------

    [Fact]
    public async Task HostPortTakenBySomethingElse_IsReportedAsSuch_AndTheFormerContainerIsPutBack()
    {
        var instance = await ProvisionedAsync();
        _docker.HostPortsTakenOutsideDocker.Add(15432);
        instance.EnableExternalAccess(15432, Now);

        var exception = await FailsAsync(() => _provisioner.ApplyExternalAccessAsync(instance, default), "PORT_ALREADY_IN_USE");

        Assert.Equal("The host port is already in use.", exception.Message);
        Assert.DoesNotContain("raw-daemon-detail", exception.Message, StringComparison.Ordinal);
        // As it was: the instance's own container, running, publishing nothing, on its volume.
        Assert.Empty(Published(instance));
        Assert.Equal(DockerContainerState.Running, _docker.Containers[Name(instance)].State);
        Assert.DoesNotContain(SetAside(instance), _docker.Containers.Keys);
        AssertVolumeUntouched(instance);
    }

    [Fact]
    public async Task NewContainerNeverBecomesReady_FailsWithAStableError_AndTheFormerContainerRunsAgain()
    {
        var instance = await ProvisionedAsync();
        // The database comes up in the container that publishes nothing, and not in the one that publishes.
        _docker.Exec = () => Published(instance).Count == 0 ? 0 : 1;
        instance.EnableExternalAccess(15432, Now);

        var exception = await FailsAsync(() => _provisioner.ApplyExternalAccessAsync(instance, default), "DOCKER_PORT_CONFIGURATION_FAILED");

        Assert.Equal("The database server could not be restarted with the new port configuration. It is running as it was before.", exception.Message);
        Assert.Empty(Published(instance));
        Assert.Equal(DockerContainerState.Running, _docker.Containers[Name(instance)].State);
        Assert.DoesNotContain(SetAside(instance), _docker.Containers.Keys);
        AssertVolumeUntouched(instance);
    }

    [Theory]
    [InlineData(nameof(IDockerEngine.StopContainerAsync))]
    [InlineData(nameof(IDockerEngine.RenameContainerAsync))]
    [InlineData(nameof(IDockerEngine.CreateContainerAsync))]
    public async Task DockerFailsPartWayThrough_FailsWithAStableError_AndNeverLosesTheContainerOrTheVolume(string failingOperation)
    {
        var instance = await ExposedAsync();
        instance.DisableExternalAccess(Now);
        _docker.FailOn(failingOperation);

        var exception = await FailsAsync(() => _provisioner.ApplyExternalAccessAsync(instance, default), "DOCKER_PORT_CONFIGURATION_FAILED");

        Assert.DoesNotContain("raw-daemon-detail", exception.Message, StringComparison.Ordinal);
        // Still published, as before the attempt: the record, which the caller rolls back, and Docker agree.
        Assert.True(_docker.Containers.ContainsKey(Name(instance)) || _docker.Containers.ContainsKey(SetAside(instance)));
        Assert.Contains(_docker.Volumes.Keys, name => name == Volume(instance));
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.RemoveVolumeAsync)));
        if (failingOperation == nameof(IDockerEngine.CreateContainerAsync))
        {
            Assert.Equal([new DockerPortBinding(5432, "127.0.0.1", 15432)], Published(instance));
            Assert.Equal(DockerContainerState.Running, _docker.Containers[Name(instance)].State);
            Assert.DoesNotContain(SetAside(instance), _docker.Containers.Keys);
        }
    }

    [Fact]
    public async Task DockerUnreachable_IsReportedAsUnavailable_AndNothingIsChanged()
    {
        var instance = await ProvisionedAsync();
        instance.EnableExternalAccess(15432, Now);
        _docker.Unavailable = true;

        var exception = await FailsAsync(() => _provisioner.ApplyExternalAccessAsync(instance, default), "DOCKER_UNAVAILABLE");

        Assert.True(exception.ProvisionerUnavailable);
        Assert.Empty(Published(instance));
    }

    [Fact]
    public async Task ContainerThatIsNotTheInstancesOwn_IsNeverTouched()
    {
        var instance = await ProvisionedAsync();
        var foreign = _docker.Containers[Name(instance)] with { Labels = DockerResourceNaming.InstanceLabels(Guid.NewGuid()) };
        _docker.Containers[Name(instance)] = foreign;
        instance.EnableExternalAccess(15432, Now);

        await FailsAsync(() => _provisioner.ApplyExternalAccessAsync(instance, default), "DOCKER_RESOURCE_CONFLICT");
        await FailsAsync(() => _provisioner.EnsureRunningAsync(instance, default), "DOCKER_RESOURCE_CONFLICT");

        Assert.Same(foreign, _docker.Containers[Name(instance)]);
        AssertNoContainerChanged();
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.StartContainerAsync)));
    }

    [Fact]
    public async Task ContainerOnAnotherVolume_OrOfAnotherImage_IsNeverReplaced()
    {
        var instance = await ProvisionedAsync();
        instance.EnableExternalAccess(15432, Now);
        var own = _docker.Containers[Name(instance)];

        _docker.Containers[Name(instance)] = own with { Mounts = [new DockerMount("somebody-elses-data", "/var/lib/postgresql/data")] };
        await FailsAsync(() => _provisioner.ApplyExternalAccessAsync(instance, default), "DOCKER_RESOURCE_CONFLICT");
        _docker.Containers[Name(instance)] = own with { Image = "postgres:15" };
        await FailsAsync(() => _provisioner.ApplyExternalAccessAsync(instance, default), "DOCKER_RESOURCE_CONFLICT");

        AssertNoContainerChanged();
    }

    [Fact]
    public async Task InstanceWithoutAContainer_IsNotGivenOne()
    {
        var instance = await ProvisionedAsync();
        _docker.Containers.Remove(Name(instance));
        instance.EnableExternalAccess(15432, Now);

        await FailsAsync(() => _provisioner.ApplyExternalAccessAsync(instance, default), "DATABASE_CONTAINER_MISSING");

        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync)));
    }

    // --- Provisioning adopts only what matches --------------------------------------------------

    [Fact]
    public async Task Provisioning_DoesNotAdoptAContainerThatPublishesAPort_WhenTheRecordSaysNone()
    {
        var instance = Instance.Create("orders", InstanceEngine.Postgres, "16", 2, 1024, 20, Now);
        await _provisioner.ProvisionAsync(instance, default);
        var published = _docker.Containers[Name(instance)] with { PortBindings = [new DockerPortBinding(5432, "0.0.0.0", 5432)] };
        _docker.Containers[Name(instance)] = published;
        _docker.Calls.Clear();

        var exception = await FailsAsync(() => _provisioner.ProvisionAsync(instance, default), "DOCKER_RESOURCE_CONFLICT");

        Assert.Contains("does not publish the ports the instance's record says", exception.Message, StringComparison.Ordinal);
        Assert.Same(published, _docker.Containers[Name(instance)]);
        AssertNoContainerChanged();
    }

    [Theory]
    [InlineData(5432, "127.0.0.1", 15433)]
    [InlineData(5432, "0.0.0.0", 15432)]
    [InlineData(3306, "127.0.0.1", 15432)]
    public async Task Provisioning_DoesNotAdoptAContainer_WithAnotherHostPort_AnotherBindAddress_OrAnotherContainerPort(
        int containerPort, string address, int hostPort)
    {
        var instance = await ExposedAsync(15432);
        var wrong = _docker.Containers[Name(instance)] with { PortBindings = [new DockerPortBinding(containerPort, address, hostPort)] };
        _docker.Containers[Name(instance)] = wrong;

        await FailsAsync(() => _provisioner.ProvisionAsync(instance, default), "DOCKER_RESOURCE_CONFLICT");

        Assert.Same(wrong, _docker.Containers[Name(instance)]);
        AssertNoContainerChanged();
    }

    [Fact]
    public async Task Provisioning_AdoptsAContainerThatPublishesExactlyWhatTheRecordSays()
    {
        var instance = await ExposedAsync(15432);

        await _provisioner.ProvisionAsync(instance, default);

        AssertNoContainerChanged();
        Assert.Equal([new DockerPortBinding(5432, "127.0.0.1", 15432)], Published(instance));
    }

    // --- Recovery after a restart ---------------------------------------------------------------

    [Fact]
    public async Task Recovery_LeavesAValidContainerAlone_PublishedOrNot()
    {
        var exposed = await ExposedAsync(15432);
        var privateInstance = await ProvisionedAsync();

        await _provisioner.EnsureRunningAsync(exposed, default);
        await _provisioner.EnsureRunningAsync(privateInstance, default);

        AssertNoContainerChanged();
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.StartContainerAsync)));
        Assert.Equal([new DockerPortBinding(5432, "127.0.0.1", 15432)], Published(exposed));
        Assert.Empty(Published(privateInstance));
    }

    [Fact]
    public async Task Recovery_StartsAStoppedContainer_ThatPublishesWhatTheRecordSays_WithoutReplacingIt()
    {
        var instance = await ExposedAsync(15432);
        _docker.Containers[Name(instance)] = _docker.Containers[Name(instance)] with { State = DockerContainerState.Exited };

        await _provisioner.EnsureRunningAsync(instance, default);

        AssertNoContainerChanged();
        Assert.Equal(DockerContainerState.Running, _docker.Containers[Name(instance)].State);
        Assert.Equal([15432], await _docker.ListPublishedHostPortsAsync(default));
    }

    [Fact]
    public async Task Recovery_UnpublishesAContainer_ThatPublishesWhatTheRecordDoesNotSay()
    {
        // The record says private; Docker has a published port, left by a change that was interrupted.
        var instance = await ExposedAsync(15432);
        instance.DisableExternalAccess(Now);

        await _provisioner.EnsureRunningAsync(instance, default);

        Assert.Empty(Published(instance));
        Assert.Equal(DockerContainerState.Running, _docker.Containers[Name(instance)].State);
        AssertVolumeUntouched(instance);
        Assert.Contains(_logger.Entries, entry => entry.Contains("does not publish what the instance's record says", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DockerContainerState.Running)]
    [InlineData(DockerContainerState.Exited)]
    public async Task Recovery_PublishesThePortOnRecord_WhenTheContainerDoesNot(DockerContainerState state)
    {
        var instance = await ProvisionedAsync();
        _docker.Containers[Name(instance)] = _docker.Containers[Name(instance)] with { State = state };
        instance.EnableExternalAccess(15432, Now);

        await _provisioner.EnsureRunningAsync(instance, default);

        Assert.Equal([new DockerPortBinding(5432, "127.0.0.1", 15432)], Published(instance));
        Assert.Equal(DockerContainerState.Running, _docker.Containers[Name(instance)].State);
        AssertVolumeUntouched(instance);
    }

    [Fact]
    public async Task Recovery_MovesAPublishedPort_WhenTheServersBindAddressWasReconfigured()
    {
        var instance = await ExposedAsync(15432);
        var reconfigured = Provisioner(new ExternalAccessOptions { BindAddress = "10.0.0.5" });

        await reconfigured.EnsureRunningAsync(instance, default);

        Assert.Equal([new DockerPortBinding(5432, "10.0.0.5", 15432)], Published(instance));
        AssertVolumeUntouched(instance);
    }

    [Fact]
    public async Task Recovery_PutsBackAContainer_ThatAnInterruptedReplacementSetAside()
    {
        // The process died after the container was set aside and before a new one existed.
        var instance = await ProvisionedAsync();
        await _docker.StopContainerAsync(Name(instance), default);
        await _docker.RenameContainerAsync(Name(instance), SetAside(instance), default);
        _docker.Calls.Clear();

        await _provisioner.EnsureRunningAsync(instance, default);

        Assert.Equal(DockerContainerState.Running, _docker.Containers[Name(instance)].State);
        Assert.DoesNotContain(SetAside(instance), _docker.Containers.Keys);
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync)));
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.RemoveContainerAsync)));
        AssertVolumeUntouched(instance);
    }

    [Fact]
    public async Task Recovery_DropsAHalfMadeReplacement_ThatDoesNotMatchTheRecord_AndPutsTheFormerContainerBack()
    {
        // The process died with the new, published container created and the transaction not committed:
        // the record still says private.
        var instance = await ProvisionedAsync();
        var former = _docker.Containers[Name(instance)];
        await _docker.StopContainerAsync(Name(instance), default);
        await _docker.RenameContainerAsync(Name(instance), SetAside(instance), default);
        _docker.Containers[Name(instance)] = former with { State = DockerContainerState.Running, PortBindings = [new DockerPortBinding(5432, "127.0.0.1", 15432)] };
        _docker.Calls.Clear();

        await _provisioner.EnsureRunningAsync(instance, default);

        Assert.Empty(Published(instance));
        Assert.Equal(DockerContainerState.Running, _docker.Containers[Name(instance)].State);
        Assert.DoesNotContain(SetAside(instance), _docker.Containers.Keys);
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync)));
        AssertVolumeUntouched(instance);
    }

    [Fact]
    public async Task Recovery_FinishesAReplacement_ThatWasCompleteButForRemovingTheFormerContainer()
    {
        var instance = await ExposedAsync(15432);
        var current = _docker.Containers[Name(instance)];
        _docker.Containers[SetAside(instance)] = current with { Name = SetAside(instance), State = DockerContainerState.Exited, PortBindings = null };
        _docker.Calls.Clear();

        await _provisioner.EnsureRunningAsync(instance, default);

        Assert.Same(current, _docker.Containers[Name(instance)]);
        Assert.DoesNotContain(SetAside(instance), _docker.Containers.Keys);
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync)));
        AssertVolumeUntouched(instance);
    }

    [Fact]
    public async Task Recovery_NeverTouchesASetAsideContainer_ThatIsNotTheInstancesOwn()
    {
        var instance = await ProvisionedAsync();
        var foreign = _docker.Containers[Name(instance)] with { Name = SetAside(instance), Labels = DockerResourceNaming.InstanceLabels(Guid.NewGuid()) };
        _docker.Containers[SetAside(instance)] = foreign;

        await _provisioner.EnsureRunningAsync(instance, default);
        await _provisioner.DeprovisionAsync(instance, default);

        Assert.Same(foreign, _docker.Containers[SetAside(instance)]);
    }

    [Fact]
    public async Task DeletingAnInstance_AlsoRemovesAContainerThatAReplacementSetAside()
    {
        var instance = await ProvisionedAsync();
        _docker.Containers[SetAside(instance)] = _docker.Containers[Name(instance)] with { Name = SetAside(instance), State = DockerContainerState.Exited };

        await _provisioner.DeprovisionAsync(instance, default);

        Assert.Empty(_docker.Containers);
        Assert.Empty(_docker.Volumes);
    }
}
