using AuroraDbManager.Api.Application.Connectivity;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Docker;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Tests.Docker;

/// <summary>
/// Recovery of already provisioned instances by the Docker provisioner, against an in-memory
/// Docker Engine; no Docker daemon is involved.
/// </summary>
public sealed class DockerRecoveryTests
{
    private readonly FakeDockerEngine _docker = new();
    private readonly RecordingLogger<DockerInstanceProvisioner> _logger = new();
    private readonly DockerInstanceProvisioner _provisioner;
    private readonly Instance _instance = Instance.Create("orders", InstanceEngine.Postgres, "16", 2, 1024, 20, DateTime.UtcNow);

    private readonly ExternalAccessOptions _externalAccess = new();

    public DockerRecoveryTests()
    {
        var options = Options.Create(new DockerOptions { ReadinessTimeoutSeconds = 10, ReadinessPollIntervalMilliseconds = 0 });
        _provisioner = new DockerInstanceProvisioner(
            _docker, new DockerImageResolver(), new InMemoryInstanceSecretStore(), options, Options.Create(_externalAccess), new SteppingTimeProvider(), _logger);
    }

    private string ContainerName => DockerResourceNaming.ContainerName(_instance.Id);

    private string VolumeName => DockerResourceNaming.VolumeName(_instance.Id);

    private void StopContainer() =>
        _docker.Containers[ContainerName] = _docker.Containers[ContainerName] with { State = DockerContainerState.Exited };

    private async Task ProvisionThenForgetCallsAsync()
    {
        await _provisioner.ProvisionAsync(_instance, default);
        _docker.Calls.Clear();
    }

    private async Task<InstanceProvisioningException> EnsureRunningExpectingFailureAsync(string expectedCode)
    {
        var exception = await Assert.ThrowsAsync<InstanceProvisioningException>(
            () => _provisioner.EnsureRunningAsync(_instance, default));
        Assert.Equal(expectedCode, exception.Code);
        return exception;
    }

    private void AssertNothingCreatedOrRemoved()
    {
        foreach (var operation in new[]
                 {
                     nameof(IDockerEngine.CreateContainerAsync), nameof(IDockerEngine.CreateVolumeAsync),
                     nameof(IDockerEngine.CreateNetworkAsync), nameof(IDockerEngine.PullImageAsync),
                     nameof(IDockerEngine.RemoveContainerAsync), nameof(IDockerEngine.RemoveVolumeAsync)
                 })
        {
            Assert.Equal(0, _docker.CountCalls(operation));
        }
    }

    [Fact]
    public async Task EnsureRunning_ContainerRunning_ChangesNothing()
    {
        await ProvisionThenForgetCallsAsync();

        await _provisioner.EnsureRunningAsync(_instance, default);

        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.StartContainerAsync)));
        AssertNothingCreatedOrRemoved();
    }

    [Fact]
    public async Task EnsureRunning_ContainerStopped_StartsItAndWaitsForReadiness()
    {
        await ProvisionThenForgetCallsAsync();
        StopContainer();
        var checks = 0;
        _docker.Exec = () => ++checks < 3 ? 1 : 0;

        await _provisioner.EnsureRunningAsync(_instance, default);

        Assert.Equal(1, _docker.CountCalls(nameof(IDockerEngine.StartContainerAsync)));
        Assert.Equal(3, _docker.CountCalls(nameof(IDockerEngine.ExecAsync)));
        Assert.Equal(DockerContainerState.Running, _docker.Containers[ContainerName].State);
        AssertNothingCreatedOrRemoved();
        Assert.Contains(_logger.Entries, entry => entry.Contains("Restarting stopped database container"));
    }

    [Fact]
    public async Task EnsureRunning_StartedContainerNeverBecomesReady_Fails_AndKeepsContainerAndVolume()
    {
        await ProvisionThenForgetCallsAsync();
        StopContainer();
        _docker.Exec = () => 1;

        await EnsureRunningExpectingFailureAsync("DATABASE_READINESS_TIMEOUT");

        Assert.True(_docker.Containers.ContainsKey(ContainerName));
        Assert.True(_docker.Volumes.ContainsKey(VolumeName));
        AssertNothingCreatedOrRemoved();
    }

    [Fact]
    public async Task EnsureRunning_ContainerStopsAgainRightAfterStart_FailsAsExited()
    {
        await ProvisionThenForgetCallsAsync();
        StopContainer();
        _docker.ContainersExitAfterStart = true;

        await EnsureRunningExpectingFailureAsync("DATABASE_CONTAINER_EXITED");

        AssertNothingCreatedOrRemoved();
    }

    [Fact]
    public async Task EnsureRunning_ContainerMissing_Fails_AndDoesNotCreateAReplacement()
    {
        await ProvisionThenForgetCallsAsync();
        _docker.Containers.Remove(ContainerName);

        await EnsureRunningExpectingFailureAsync("DATABASE_CONTAINER_MISSING");

        Assert.Empty(_docker.Containers);
        Assert.True(_docker.Volumes.ContainsKey(VolumeName));
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.StartContainerAsync)));
        AssertNothingCreatedOrRemoved();
    }

    [Theory]
    [InlineData(DockerContainerState.Running)]
    [InlineData(DockerContainerState.Exited)]
    public async Task EnsureRunning_ContainerBelongsToSomethingElse_FailsWithConflict_AndLeavesItUntouched(DockerContainerState state)
    {
        var foreign = new DockerContainer(
            ContainerName, "postgres:16", state, new Dictionary<string, string>(),
            [new DockerMount(VolumeName, "/var/lib/postgresql/data")]);
        _docker.AddContainer(foreign);

        await EnsureRunningExpectingFailureAsync("DOCKER_RESOURCE_CONFLICT");

        Assert.Equal(foreign, _docker.Containers[ContainerName]);
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.StartContainerAsync)));
        AssertNothingCreatedOrRemoved();
    }

    [Fact]
    public async Task EnsureRunning_StoppedContainerWithDifferentImage_FailsWithConflict_AndIsNotStarted()
    {
        await ProvisionThenForgetCallsAsync();
        _docker.Containers[ContainerName] = _docker.Containers[ContainerName] with
        {
            State = DockerContainerState.Exited,
            Image = "postgres:15"
        };

        await EnsureRunningExpectingFailureAsync("DOCKER_RESOURCE_CONFLICT");

        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.StartContainerAsync)));
    }

    [Fact]
    public async Task EnsureRunning_DockerUnavailable_ReportsUnavailable_NotAFailedInstance()
    {
        await ProvisionThenForgetCallsAsync();
        _docker.Unavailable = true;

        var exception = await EnsureRunningExpectingFailureAsync("DOCKER_UNAVAILABLE");

        Assert.True(exception.ProvisionerUnavailable);
    }

    [Fact]
    public async Task EnsureRunning_DefiniteFailures_AreNotReportedAsUnavailable()
    {
        var exception = await EnsureRunningExpectingFailureAsync("DATABASE_CONTAINER_MISSING");

        Assert.False(exception.ProvisionerUnavailable);
    }

    [Fact]
    public async Task EnsureRunning_Cancelled_Stops()
    {
        await ProvisionThenForgetCallsAsync();
        StopContainer();
        using var cancellation = new CancellationTokenSource();
        _docker.Exec = () =>
        {
            cancellation.Cancel();
            return 1;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _provisioner.EnsureRunningAsync(_instance, cancellation.Token));

        AssertNothingCreatedOrRemoved();
    }

    [Fact]
    public async Task ListResources_ReturnsOnlyManagedInstanceResources_WithTheirOwners()
    {
        await _provisioner.ProvisionAsync(_instance, default);
        var orphanId = Guid.NewGuid();
        _docker.Volumes["aurora-instance-orphan-data"] =
            new DockerVolume("aurora-instance-orphan-data", DockerResourceNaming.InstanceLabels(orphanId));
        _docker.Volumes["broken"] = new DockerVolume("broken", new Dictionary<string, string>
        {
            [DockerResourceNaming.ManagedLabel] = "true",
            [DockerResourceNaming.InstanceIdLabel] = "not-a-guid"
        });
        // Not Aurora's: no managed label.
        _docker.Volumes["someone-elses"] = new DockerVolume("someone-elses", new Dictionary<string, string>());
        _docker.AddContainer(new DockerContainer(
            "someone-elses-db", "postgres:16", DockerContainerState.Running, new Dictionary<string, string>(), []));

        var resources = await _provisioner.ListResourcesAsync(default);

        Assert.Equal(
            new[]
            {
                new ProvisionedResource("container", ContainerName, _instance.Id),
                new ProvisionedResource("volume", VolumeName, _instance.Id),
                new ProvisionedResource("volume", "aurora-instance-orphan-data", orphanId),
                new ProvisionedResource("volume", "broken", null)
            }.OrderBy(resource => resource.Name),
            resources.OrderBy(resource => resource.Name));
    }

    [Fact]
    public async Task ListResources_DockerUnavailable_ReportsUnavailable()
    {
        _docker.Unavailable = true;

        var exception = await Assert.ThrowsAsync<InstanceProvisioningException>(() => _provisioner.ListResourcesAsync(default));

        Assert.True(exception.ProvisionerUnavailable);
    }
}
