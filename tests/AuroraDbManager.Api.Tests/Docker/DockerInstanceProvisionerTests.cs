using AuroraDbManager.Api.Application.Connectivity;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Docker;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Tests.Docker;

/// <summary>The provisioner against an in-memory Docker Engine; no Docker daemon is involved.</summary>
public sealed class DockerInstanceProvisionerTests
{
    private const string Network = "aurora-db";

    private readonly FakeDockerEngine _docker = new();
    private readonly InMemoryInstanceSecretStore _secrets = new();
    private readonly SteppingTimeProvider _time = new();
    private readonly RecordingLogger<DockerInstanceProvisioner> _logger = new();
    private readonly DockerInstanceProvisioner _provisioner;

    private readonly Instance _instance = Instance.Create("orders", InstanceEngine.Postgres, "16", 2, 1024, 20, DateTime.UtcNow);

    private readonly ExternalAccessOptions _externalAccess = new();

    public DockerInstanceProvisionerTests()
    {
        // No waiting between readiness checks; the stepping clock makes the 10s timeout pass after
        // a handful of checks.
        var options = Options.Create(new DockerOptions
        {
            NetworkName = Network,
            ReadinessTimeoutSeconds = 10,
            ReadinessPollIntervalMilliseconds = 0
        });
        _provisioner = new DockerInstanceProvisioner(_docker, new DockerImageResolver(), _secrets, options, Options.Create(_externalAccess), _time, _logger);
    }

    private string ContainerName => DockerResourceNaming.ContainerName(_instance.Id);

    private string VolumeName => DockerResourceNaming.VolumeName(_instance.Id);

    private Task ProvisionAsync(CancellationToken cancellationToken = default) =>
        _provisioner.ProvisionAsync(_instance, cancellationToken);

    private async Task<InstanceProvisioningException> ProvisionExpectingFailureAsync(string expectedCode)
    {
        var exception = await Assert.ThrowsAsync<InstanceProvisioningException>(() => ProvisionAsync());
        Assert.Equal(expectedCode, exception.Code);
        return exception;
    }

    private DockerContainer OwnContainer(DockerContainerState state, string image = "postgres:16") => new(
        ContainerName,
        image,
        state,
        DockerResourceNaming.InstanceLabels(_instance.Id),
        [new DockerMount(VolumeName, "/var/lib/postgresql/data")]);

    private void AddOwnVolume() =>
        _docker.Volumes[VolumeName] = new DockerVolume(VolumeName, DockerResourceNaming.InstanceLabels(_instance.Id));

    // --- Case A: nothing exists -------------------------------------------------------------

    [Fact]
    public async Task Provision_NothingExists_CreatesNetworkVolumeAndRunningContainer()
    {
        await ProvisionAsync();

        Assert.Contains(Network, _docker.Networks);
        Assert.True(DockerResourceNaming.IsOwnedBy(_docker.Volumes[VolumeName].Labels, _instance.Id));
        Assert.Contains("postgres:16", _docker.Images);
        Assert.Equal(DockerContainerState.Running, _docker.Containers[ContainerName].State);

        var spec = _docker.LastCreatedSpec!;
        Assert.Equal("postgres:16", spec.Image);
        Assert.Equal(Network, spec.NetworkName);
        Assert.Equal(VolumeName, spec.VolumeName);
        Assert.Equal(2_000_000_000L, spec.NanoCpus);
        Assert.Equal(1024L * 1024 * 1024, spec.MemoryBytes);
        Assert.Equal(await _secrets.GetOrCreateAdminPasswordAsync(_instance.Id, default), spec.Environment["POSTGRES_PASSWORD"]);
    }

    [Fact]
    public async Task Provision_ReturnsOnlyAfterReadinessCheckSucceeds()
    {
        var checks = 0;
        _docker.Exec = () => ++checks < 4 ? 1 : 0;

        await ProvisionAsync();

        Assert.Equal(4, checks);
        Assert.Equal(4, _docker.CountCalls(nameof(IDockerEngine.ExecAsync)));
        Assert.Contains($"{nameof(IDockerEngine.ExecAsync)} {ContainerName}: pg_isready -q -h 127.0.0.1 -p 5432", _docker.Calls);
    }

    [Fact]
    public async Task Provision_NetworkAndImageAlreadyPresent_DoesNotCreateOrPullThem()
    {
        _docker.Networks.Add(Network);
        _docker.Images.Add("postgres:16");

        await ProvisionAsync();

        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.CreateNetworkAsync)));
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.PullImageAsync)));
    }

    [Fact]
    public async Task Provision_NetworkCreatedConcurrentlyByAnotherProvisioning_Continues()
    {
        _docker.FailOn(nameof(IDockerEngine.CreateNetworkAsync), DockerFailure.Conflict);

        await ProvisionAsync();

        Assert.Equal(DockerContainerState.Running, _docker.Containers[ContainerName].State);
    }

    [Fact]
    public async Task Provision_MysqlInstance_UsesMysqlImageDataDirectoryAndReadinessCheck()
    {
        var mysql = Instance.Create("shop", InstanceEngine.Mysql, "8.4", 1, 2048, 20, DateTime.UtcNow);

        await _provisioner.ProvisionAsync(mysql, default);

        var spec = _docker.LastCreatedSpec!;
        Assert.Equal("mysql:8.4", spec.Image);
        Assert.Equal("/var/lib/mysql", spec.VolumeTarget);
        Assert.True(spec.Environment.ContainsKey("MYSQL_ROOT_PASSWORD"));
        Assert.Contains(_docker.Calls, call => call.Contains("mysqladmin ping"));
    }

    // --- Unsupported versions ---------------------------------------------------------------

    [Fact]
    public async Task Provision_UnsupportedVersion_FailsBeforeTouchingDocker()
    {
        var instance = Instance.Create("old", InstanceEngine.Postgres, "9.6", 1, 1024, 20, DateTime.UtcNow);

        var exception = await Assert.ThrowsAsync<InstanceProvisioningException>(
            () => _provisioner.ProvisionAsync(instance, default));

        Assert.Equal("UNSUPPORTED_DATABASE_VERSION", exception.Code);
        Assert.Empty(_docker.Calls);
    }

    // --- Case B: container already running --------------------------------------------------

    [Fact]
    public async Task Provision_OwnContainerAlreadyRunning_VerifiesReadinessAndCreatesNothing()
    {
        AddOwnVolume();
        _docker.AddContainer(OwnContainer(DockerContainerState.Running));

        await ProvisionAsync();

        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync)));
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.CreateVolumeAsync)));
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.StartContainerAsync)));
        Assert.Equal(1, _docker.CountCalls(nameof(IDockerEngine.ExecAsync)));
        Assert.Equal(0, _secrets.CreatedCount);
    }

    [Fact]
    public async Task Provision_CalledTwice_SecondCallReusesEverything()
    {
        await ProvisionAsync();
        var callsAfterFirst = _docker.Calls.Count;

        await ProvisionAsync();

        Assert.Equal(1, _docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync)));
        Assert.Equal(1, _docker.CountCalls(nameof(IDockerEngine.CreateVolumeAsync)));
        Assert.Equal(1, _docker.CountCalls(nameof(IDockerEngine.CreateNetworkAsync)));
        Assert.Equal(1, _docker.CountCalls(nameof(IDockerEngine.StartContainerAsync)));
        Assert.Single(_docker.Containers);
        Assert.Single(_docker.Volumes);
        Assert.True(_docker.Calls.Count > callsAfterFirst);
    }

    [Fact]
    public async Task Provision_OwnRunningContainerNeverBecomesReady_FailsAndLeavesItInPlace()
    {
        AddOwnVolume();
        _docker.AddContainer(OwnContainer(DockerContainerState.Running));
        _docker.Exec = () => 1;

        await ProvisionExpectingFailureAsync("DATABASE_READINESS_TIMEOUT");

        Assert.True(_docker.Containers.ContainsKey(ContainerName));
        Assert.True(_docker.Volumes.ContainsKey(VolumeName));
    }

    // --- Case C: container exists but is stopped --------------------------------------------

    [Theory]
    [InlineData(DockerContainerState.Exited)]
    [InlineData(DockerContainerState.Created)]
    public async Task Provision_OwnContainerStopped_StartsItAndVerifiesReadiness(DockerContainerState state)
    {
        AddOwnVolume();
        _docker.AddContainer(OwnContainer(state));

        await ProvisionAsync();

        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync)));
        Assert.Equal(1, _docker.CountCalls(nameof(IDockerEngine.StartContainerAsync)));
        Assert.Equal(1, _docker.CountCalls(nameof(IDockerEngine.ExecAsync)));
        Assert.Equal(DockerContainerState.Running, _docker.Containers[ContainerName].State);
    }

    // --- Case D: incompatible container -----------------------------------------------------

    [Fact]
    public async Task Provision_ContainerWithTheNameBelongsToSomethingElse_FailsWithConflictAndTouchesNothing()
    {
        _docker.AddContainer(OwnContainer(DockerContainerState.Running) with { Labels = new Dictionary<string, string>() });

        var exception = await ProvisionExpectingFailureAsync("DOCKER_RESOURCE_CONFLICT");

        Assert.Contains("does not belong to this instance", exception.Message);
        AssertNothingCreatedOrRemoved();
    }

    [Fact]
    public async Task Provision_ContainerOfAnotherInstance_FailsWithConflict()
    {
        _docker.AddContainer(OwnContainer(DockerContainerState.Running) with
        {
            Labels = DockerResourceNaming.InstanceLabels(Guid.NewGuid())
        });

        await ProvisionExpectingFailureAsync("DOCKER_RESOURCE_CONFLICT");

        AssertNothingCreatedOrRemoved();
    }

    [Fact]
    public async Task Provision_OwnContainerRunsDifferentImage_FailsWithConflict()
    {
        _docker.AddContainer(OwnContainer(DockerContainerState.Running, image: "postgres:15"));

        await ProvisionExpectingFailureAsync("DOCKER_RESOURCE_CONFLICT");

        AssertNothingCreatedOrRemoved();
    }

    [Fact]
    public async Task Provision_OwnContainerDoesNotMountTheDataVolume_FailsWithConflict()
    {
        _docker.AddContainer(OwnContainer(DockerContainerState.Running) with { Mounts = [] });

        await ProvisionExpectingFailureAsync("DOCKER_RESOURCE_CONFLICT");

        AssertNothingCreatedOrRemoved();
    }

    [Fact]
    public async Task Provision_OwnContainerInUnusableState_FailsWithConflict()
    {
        _docker.AddContainer(OwnContainer(DockerContainerState.Other));

        await ProvisionExpectingFailureAsync("DOCKER_RESOURCE_CONFLICT");

        AssertNothingCreatedOrRemoved();
    }

    // --- Case E: volume exists, container does not ------------------------------------------

    [Fact]
    public async Task Provision_OwnVolumeExistsWithoutContainer_ReusesTheVolume()
    {
        AddOwnVolume();

        await ProvisionAsync();

        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.CreateVolumeAsync)));
        Assert.Equal(VolumeName, _docker.LastCreatedSpec!.VolumeName);
        Assert.Equal(DockerContainerState.Running, _docker.Containers[ContainerName].State);
    }

    [Fact]
    public async Task Provision_VolumeWithTheNameBelongsToSomethingElse_FailsWithConflictAndKeepsIt()
    {
        _docker.Volumes[VolumeName] = new DockerVolume(VolumeName, new Dictionary<string, string>());

        await ProvisionExpectingFailureAsync("DOCKER_RESOURCE_CONFLICT");

        Assert.True(_docker.Volumes.ContainsKey(VolumeName));
        AssertNothingCreatedOrRemoved();
    }

    // --- Readiness --------------------------------------------------------------------------

    [Fact]
    public async Task Provision_DatabaseNeverBecomesReady_FailsWithTimeoutAfterPolling()
    {
        _docker.Exec = () => 1;

        var exception = await ProvisionExpectingFailureAsync("DATABASE_READINESS_TIMEOUT");

        Assert.Equal("The database did not become ready within 10 seconds.", exception.Message);
        Assert.True(_docker.CountCalls(nameof(IDockerEngine.ExecAsync)) > 1);
    }

    [Fact]
    public async Task Provision_ContainerExitsWhileWaiting_FailsWithoutWaitingForTheTimeout()
    {
        _docker.ContainersExitAfterStart = true;

        await ProvisionExpectingFailureAsync("DATABASE_CONTAINER_EXITED");

        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.ExecAsync)));
    }

    [Fact]
    public async Task Provision_ContainerStopsBetweenStateCheckAndReadinessCommand_IsReportedAsExited()
    {
        _docker.Exec = () =>
        {
            _docker.Containers[ContainerName] = _docker.Containers[ContainerName] with { State = DockerContainerState.Exited };
            throw new DockerEngineException(DockerFailure.Conflict, "container is not running");
        };

        await ProvisionExpectingFailureAsync("DATABASE_CONTAINER_EXITED");
    }

    // --- Partial failure cleanup ------------------------------------------------------------

    [Fact]
    public async Task Provision_ReadinessFails_RemovesContainerAndVolumeCreatedByThisAttempt_ButKeepsNetwork()
    {
        _docker.Exec = () => 1;

        await ProvisionExpectingFailureAsync("DATABASE_READINESS_TIMEOUT");

        Assert.Empty(_docker.Containers);
        Assert.Empty(_docker.Volumes);
        Assert.Contains(Network, _docker.Networks);
    }

    [Fact]
    public async Task Provision_ReadinessFails_KeepsVolumeThatExistedBeforeTheAttempt()
    {
        AddOwnVolume();
        _docker.Exec = () => 1;

        await ProvisionExpectingFailureAsync("DATABASE_READINESS_TIMEOUT");

        Assert.Empty(_docker.Containers);
        Assert.True(_docker.Volumes.ContainsKey(VolumeName));
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.RemoveVolumeAsync)));
    }

    [Fact]
    public async Task Provision_ContainerCreationFails_RemovesVolumeCreatedByThisAttempt()
    {
        _docker.FailOn(nameof(IDockerEngine.CreateContainerAsync));

        await ProvisionExpectingFailureAsync("DOCKER_CONTAINER_CREATE_FAILED");

        Assert.Empty(_docker.Volumes);
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.RemoveContainerAsync)));
    }

    [Fact]
    public async Task Provision_ContainerStartFails_RemovesContainerAndVolumeCreatedByThisAttempt()
    {
        _docker.FailOn(nameof(IDockerEngine.StartContainerAsync));

        await ProvisionExpectingFailureAsync("DOCKER_CONTAINER_START_FAILED");

        Assert.Empty(_docker.Containers);
        Assert.Empty(_docker.Volumes);
    }

    [Fact]
    public async Task Provision_StartingAPreExistingContainerFails_LeavesItInPlace()
    {
        AddOwnVolume();
        _docker.AddContainer(OwnContainer(DockerContainerState.Exited));
        _docker.FailOn(nameof(IDockerEngine.StartContainerAsync));

        await ProvisionExpectingFailureAsync("DOCKER_CONTAINER_START_FAILED");

        Assert.True(_docker.Containers.ContainsKey(ContainerName));
        Assert.True(_docker.Volumes.ContainsKey(VolumeName));
    }

    [Fact]
    public async Task Provision_CleanupItselfFails_StillReportsTheOriginalError()
    {
        _docker.Exec = () => 1;
        _docker.FailOn(nameof(IDockerEngine.RemoveContainerAsync));

        await ProvisionExpectingFailureAsync("DATABASE_READINESS_TIMEOUT");
    }

    [Fact]
    public async Task Provision_RetryAfterFailedAttempt_SucceedsWithTheSamePassword()
    {
        _docker.Exec = () => 1;
        await ProvisionExpectingFailureAsync("DATABASE_READINESS_TIMEOUT");
        var firstPassword = _docker.LastCreatedSpec!.Environment["POSTGRES_PASSWORD"];

        _docker.Exec = () => 0;
        await ProvisionAsync();

        Assert.Equal(DockerContainerState.Running, _docker.Containers[ContainerName].State);
        Assert.Equal(firstPassword, _docker.LastCreatedSpec!.Environment["POSTGRES_PASSWORD"]);
        Assert.Equal(1, _secrets.CreatedCount);
    }

    // --- Error translation ------------------------------------------------------------------

    [Fact]
    public async Task Provision_DockerUnavailable_FailsWithSafeError()
    {
        _docker.Unavailable = true;

        var exception = await ProvisionExpectingFailureAsync("DOCKER_UNAVAILABLE");

        Assert.Equal("Docker is not available.", exception.Message);
        Assert.DoesNotContain("docker.sock", exception.Message);
        Assert.IsType<DockerEngineException>(exception.InnerException);
    }

    [Theory]
    [InlineData(nameof(IDockerEngine.NetworkExistsAsync), "DOCKER_NETWORK_CREATE_FAILED")]
    [InlineData(nameof(IDockerEngine.CreateNetworkAsync), "DOCKER_NETWORK_CREATE_FAILED")]
    [InlineData(nameof(IDockerEngine.FindContainerAsync), "DOCKER_OPERATION_FAILED")]
    [InlineData(nameof(IDockerEngine.FindVolumeAsync), "DOCKER_VOLUME_CREATE_FAILED")]
    [InlineData(nameof(IDockerEngine.CreateVolumeAsync), "DOCKER_VOLUME_CREATE_FAILED")]
    [InlineData(nameof(IDockerEngine.ImageExistsAsync), "DOCKER_IMAGE_PULL_FAILED")]
    [InlineData(nameof(IDockerEngine.PullImageAsync), "DOCKER_IMAGE_PULL_FAILED")]
    [InlineData(nameof(IDockerEngine.CreateContainerAsync), "DOCKER_CONTAINER_CREATE_FAILED")]
    [InlineData(nameof(IDockerEngine.StartContainerAsync), "DOCKER_CONTAINER_START_FAILED")]
    [InlineData(nameof(IDockerEngine.ExecAsync), "DOCKER_OPERATION_FAILED")]
    public async Task Provision_DockerOperationFails_ReportsItsCodeWithoutRawDockerDetails(string operation, string expectedCode)
    {
        _docker.FailOn(operation);

        var exception = await ProvisionExpectingFailureAsync(expectedCode);

        Assert.DoesNotContain("raw-daemon-detail", exception.Message);
        Assert.DoesNotContain(operation, exception.Message);
    }

    [Fact]
    public async Task Provision_UnexpectedException_PropagatesUnchangedAfterCleanup()
    {
        _docker.Exec = () => throw new InvalidOperationException("bug");

        await Assert.ThrowsAsync<InvalidOperationException>(() => ProvisionAsync());

        Assert.Empty(_docker.Containers);
        Assert.Empty(_docker.Volumes);
    }

    // --- Cancellation -----------------------------------------------------------------------

    [Fact]
    public async Task Provision_CancelledWhileWaitingForReadiness_StopsAndRemovesNothing()
    {
        using var cancellation = new CancellationTokenSource();
        _docker.Exec = () =>
        {
            cancellation.Cancel();
            return 1;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProvisionAsync(cancellation.Token));

        Assert.Equal(1, _docker.CountCalls(nameof(IDockerEngine.ExecAsync)));
        Assert.True(_docker.Containers.ContainsKey(ContainerName));
        Assert.True(_docker.Volumes.ContainsKey(VolumeName));
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.RemoveContainerAsync)));
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.RemoveVolumeAsync)));
    }

    [Fact]
    public async Task Provision_AlreadyCancelled_DoesNothing()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProvisionAsync(cancellation.Token));

        Assert.Empty(_docker.Calls);
    }

    // --- Secrets ----------------------------------------------------------------------------

    [Fact]
    public async Task Provision_NeverLogsThePassword_OnSuccessOrFailure()
    {
        await ProvisionAsync();
        var password = _docker.LastCreatedSpec!.Environment["POSTGRES_PASSWORD"];

        var failing = Instance.Create("second", InstanceEngine.Mysql, "8.4", 1, 1024, 20, DateTime.UtcNow);
        _docker.Exec = () => 1;
        await Assert.ThrowsAsync<InstanceProvisioningException>(() => _provisioner.ProvisionAsync(failing, default));
        var secondPassword = await _secrets.GetOrCreateAdminPasswordAsync(failing.Id, default);

        Assert.NotEmpty(_logger.Entries);
        Assert.DoesNotContain(_logger.Entries, entry => entry.Contains(password) || entry.Contains(secondPassword));
        Assert.DoesNotContain(_logger.Entries, entry => entry.Contains("POSTGRES_PASSWORD") || entry.Contains("MYSQL_ROOT_PASSWORD"));
    }

    // --- Deprovision ------------------------------------------------------------------------

    [Fact]
    public async Task Deprovision_RemovesTheInstancesContainerAndVolume_ButNotTheNetwork()
    {
        await ProvisionAsync();

        await _provisioner.DeprovisionAsync(_instance, default);

        Assert.Empty(_docker.Containers);
        Assert.Empty(_docker.Volumes);
        Assert.Contains(Network, _docker.Networks);
    }

    [Fact]
    public async Task Deprovision_NothingToRemove_Succeeds()
    {
        await _provisioner.DeprovisionAsync(_instance, default);

        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.RemoveContainerAsync)));
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.RemoveVolumeAsync)));
    }

    [Fact]
    public async Task Deprovision_ResourcesBelongingToSomethingElse_AreLeftUntouched()
    {
        _docker.AddContainer(OwnContainer(DockerContainerState.Running) with { Labels = new Dictionary<string, string>() });
        _docker.Volumes[VolumeName] = new DockerVolume(VolumeName, DockerResourceNaming.InstanceLabels(Guid.NewGuid()));

        await _provisioner.DeprovisionAsync(_instance, default);

        Assert.True(_docker.Containers.ContainsKey(ContainerName));
        Assert.True(_docker.Volumes.ContainsKey(VolumeName));
    }

    [Fact]
    public async Task Deprovision_DockerUnavailable_FailsWithSafeError()
    {
        _docker.Unavailable = true;

        var exception = await Assert.ThrowsAsync<InstanceProvisioningException>(
            () => _provisioner.DeprovisionAsync(_instance, default));

        Assert.Equal("DOCKER_UNAVAILABLE", exception.Code);
    }

    [Fact]
    public async Task Deprovision_RemovalFails_ReportsRemoveFailed()
    {
        await ProvisionAsync();
        _docker.FailOn(nameof(IDockerEngine.RemoveVolumeAsync), DockerFailure.Conflict);

        var exception = await Assert.ThrowsAsync<InstanceProvisioningException>(
            () => _provisioner.DeprovisionAsync(_instance, default));

        Assert.Equal("DOCKER_RESOURCE_REMOVE_FAILED", exception.Code);
        Assert.True(_docker.Volumes.ContainsKey(VolumeName));
    }

    private void AssertNothingCreatedOrRemoved()
    {
        foreach (var operation in new[]
                 {
                     nameof(IDockerEngine.CreateContainerAsync), nameof(IDockerEngine.CreateVolumeAsync),
                     nameof(IDockerEngine.StartContainerAsync), nameof(IDockerEngine.RemoveContainerAsync),
                     nameof(IDockerEngine.RemoveVolumeAsync)
                 })
        {
            Assert.Equal(0, _docker.CountCalls(operation));
        }
    }
}
