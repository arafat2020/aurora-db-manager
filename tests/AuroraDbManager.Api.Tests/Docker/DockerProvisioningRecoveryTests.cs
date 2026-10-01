using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Docker;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Tests.Docker;

/// <summary>
/// The whole recovery path through the real job system and the real Docker provisioner, with only
/// the Docker Engine faked: what happens to Docker resources a dead process left behind.
/// </summary>
public sealed class DockerProvisioningRecoveryTests : IDisposable
{
    private readonly TempDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private ApiFactory Application(bool runWorker) =>
        new() { DatabasePath = _database.Path, RunWorker = runWorker, UseDockerProvisioner = true };

    private static void AddOwnVolume(FakeDockerEngine docker, Guid instanceId)
    {
        var name = DockerResourceNaming.VolumeName(instanceId);
        docker.Volumes[name] = new DockerVolume(name, DockerResourceNaming.InstanceLabels(instanceId));
    }

    private static void AddOwnContainer(FakeDockerEngine docker, Guid instanceId, DockerContainerState state)
    {
        AddOwnVolume(docker, instanceId);
        docker.AddContainer(new DockerContainer(
            DockerResourceNaming.ContainerName(instanceId),
            "postgres:16",
            state,
            DockerResourceNaming.InstanceLabels(instanceId),
            [new DockerMount(DockerResourceNaming.VolumeName(instanceId), "/var/lib/postgresql/data")]));
    }

    [Theory]
    [InlineData(DockerContainerState.Running)]
    [InlineData(DockerContainerState.Exited)]
    public async Task StaleProvisioningJob_ContainerAlreadyExists_IsAdopted_NotDuplicated(DockerContainerState leftBehind)
    {
        Guid instanceId, jobId;
        using (var before = Application(runWorker: false))
        using (var client = before.CreateClient())
        {
            (instanceId, jobId) = await client.CreateInstanceAsync();
            await before.SimulateAbandonedExecutionAsync(jobId, DateTime.UtcNow.AddMinutes(-5));
        }

        using var after = Application(runWorker: true);
        AddOwnContainer(after.Docker, instanceId, leftBehind);
        using var restarted = after.CreateClient();
        var job = await restarted.WaitForFinishedJobAsync(jobId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Equal("running", (await restarted.GetInstanceAsync(instanceId)).Status());
        Assert.Equal(0, after.Docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync)));
        Assert.Equal(0, after.Docker.CountCalls(nameof(IDockerEngine.CreateVolumeAsync)));
        Assert.Equal(leftBehind == DockerContainerState.Exited ? 1 : 0, after.Docker.CountCalls(nameof(IDockerEngine.StartContainerAsync)));
        Assert.True(after.Docker.CountCalls(nameof(IDockerEngine.ExecAsync)) >= 1);
        Assert.Single(after.Docker.Containers);
        Assert.Equal(1, await after.WithDbAsync(db => db.Jobs.CountAsync(j => j.InstanceId == instanceId)));
    }

    [Fact]
    public async Task PendingProvisioningJob_VolumeExistsWithoutContainer_ReusesVolume_AndStillChecksReadiness()
    {
        Guid instanceId, jobId;
        using (var before = Application(runWorker: false))
        using (var client = before.CreateClient())
        {
            (instanceId, jobId) = await client.CreateInstanceAsync();
        }

        using var after = Application(runWorker: true);
        AddOwnVolume(after.Docker, instanceId);
        var checks = 0;
        after.Docker.Exec = () => ++checks < 3 ? 1 : 0;
        using var restarted = after.CreateClient();
        var job = await restarted.WaitForFinishedJobAsync(jobId);

        Assert.Equal("completed", job.Status());
        Assert.Equal("running", (await restarted.GetInstanceAsync(instanceId)).Status());
        Assert.Equal(0, after.Docker.CountCalls(nameof(IDockerEngine.CreateVolumeAsync)));
        Assert.Equal(1, after.Docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync)));
        Assert.Equal(3, after.Docker.CountCalls(nameof(IDockerEngine.ExecAsync)));
        Assert.Equal(DockerResourceNaming.VolumeName(instanceId), after.Docker.LastCreatedSpec!.VolumeName);
    }

    [Fact]
    public async Task StaleProvisioningJob_ForeignContainerHasTheName_FailsWithConflict_AndLeavesItUntouched()
    {
        Guid instanceId, jobId;
        using (var before = Application(runWorker: false))
        using (var client = before.CreateClient())
        {
            (instanceId, jobId) = await client.CreateInstanceAsync();
            await before.SimulateAbandonedExecutionAsync(jobId, DateTime.UtcNow.AddMinutes(-5));
        }

        using var after = Application(runWorker: true);
        var foreign = new DockerContainer(
            DockerResourceNaming.ContainerName(instanceId), "postgres:16", DockerContainerState.Exited,
            new Dictionary<string, string> { ["owner"] = "someone-else" }, []);
        after.Docker.AddContainer(foreign);
        using var restarted = after.CreateClient();
        var job = await restarted.WaitForFinishedJobAsync(jobId);

        Assert.Equal("failed", job.Status());
        Assert.Equal("DOCKER_RESOURCE_CONFLICT", job.GetProperty("error").GetProperty("code").GetString());
        var instance = await restarted.GetInstanceAsync(instanceId);
        Assert.Equal("failed", instance.Status());
        Assert.Equal("DOCKER_RESOURCE_CONFLICT", instance.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(foreign, Assert.Single(after.Docker.Containers).Value);
        Assert.Equal(0, after.Docker.CountCalls(nameof(IDockerEngine.StartContainerAsync)));
        Assert.Equal(0, after.Docker.CountCalls(nameof(IDockerEngine.RemoveContainerAsync)));
    }

    [Fact]
    public async Task Restart_RunningInstanceWhoseContainerStopped_IsStartedAgain_AndStaysRunning()
    {
        Guid instanceId;
        using (var before = Application(runWorker: false))
        using (var client = before.CreateClient())
        {
            (instanceId, var jobId) = await client.CreateInstanceAsync();
            await before.ProcessJobAsync(jobId);
        }

        // Docker was restarted while Aurora was down: the container exists but is stopped.
        using var after = Application(runWorker: false);
        AddOwnContainer(after.Docker, instanceId, DockerContainerState.Exited);
        using var restarted = after.CreateClient();

        var report = await after.ReconcileAsync();

        Assert.Equal(1, report.Verified);
        Assert.Empty(report.Failed);
        Assert.Equal(DockerContainerState.Running, after.Docker.Containers[DockerResourceNaming.ContainerName(instanceId)].State);
        Assert.Equal(1, after.Docker.CountCalls(nameof(IDockerEngine.StartContainerAsync)));
        Assert.Equal(0, after.Docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync)));
        Assert.Equal("running", (await restarted.GetInstanceAsync(instanceId)).Status());
    }

    [Fact]
    public async Task Restart_RunningInstanceWhoseContainerIsGone_BecomesFailed_AndNoReplacementIsCreated()
    {
        Guid instanceId;
        using (var before = Application(runWorker: false))
        using (var client = before.CreateClient())
        {
            (instanceId, var jobId) = await client.CreateInstanceAsync();
            await before.ProcessJobAsync(jobId);
        }

        using var after = Application(runWorker: false);
        AddOwnVolume(after.Docker, instanceId);
        using var restarted = after.CreateClient();

        var report = await after.ReconcileAsync();

        Assert.Equal([instanceId], report.Failed);
        var instance = await restarted.GetInstanceAsync(instanceId);
        Assert.Equal("failed", instance.Status());
        Assert.Equal("DATABASE_CONTAINER_MISSING", instance.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(after.Docker.Containers);
        Assert.Single(after.Docker.Volumes);
        Assert.Equal(0, after.Docker.CountCalls(nameof(IDockerEngine.RemoveVolumeAsync)));
    }

    [Fact]
    public async Task Restart_DockerUnavailable_RunningInstanceStaysRunning()
    {
        Guid instanceId;
        using (var before = Application(runWorker: false))
        using (var client = before.CreateClient())
        {
            (instanceId, var jobId) = await client.CreateInstanceAsync();
            await before.ProcessJobAsync(jobId);
        }

        using var after = Application(runWorker: false);
        after.Docker.Unavailable = true;
        using var restarted = after.CreateClient();

        var report = await after.ReconcileAsync();

        Assert.Equal(1, report.Unverified);
        Assert.Empty(report.Failed);
        Assert.Equal("running", (await restarted.GetInstanceAsync(instanceId)).Status());
    }

    [Fact]
    public async Task Reconciliation_OrphanAndForeignResources_AreNeverModified()
    {
        using var application = Application(runWorker: false);
        using var client = application.CreateClient();
        var (instanceId, jobId) = await client.CreateInstanceAsync();
        await application.ProcessJobAsync(jobId);

        var orphanId = Guid.NewGuid();
        AddOwnContainer(application.Docker, orphanId, DockerContainerState.Exited);
        application.Docker.Volumes["someone-elses"] = new DockerVolume("someone-elses", new Dictionary<string, string>());
        application.Docker.AddContainer(new DockerContainer(
            "someone-elses-db", "postgres:16", DockerContainerState.Exited, new Dictionary<string, string>(), []));
        var before = application.Docker.Containers.ToDictionary();
        var volumesBefore = application.Docker.Volumes.Keys.Order().ToList();
        application.Docker.Calls.Clear();

        var report = await application.ReconcileAsync();

        Assert.Equal(
            [DockerResourceNaming.ContainerName(orphanId), DockerResourceNaming.VolumeName(orphanId)],
            report.Orphans.Select(orphan => orphan.Name).Order());
        Assert.All(report.Orphans, orphan => Assert.Equal(orphanId, orphan.InstanceId));
        Assert.Equal(before, application.Docker.Containers);
        Assert.Equal(volumesBefore, application.Docker.Volumes.Keys.Order());
        foreach (var operation in new[]
                 {
                     nameof(IDockerEngine.StartContainerAsync), nameof(IDockerEngine.RemoveContainerAsync),
                     nameof(IDockerEngine.RemoveVolumeAsync), nameof(IDockerEngine.CreateContainerAsync)
                 })
        {
            Assert.Equal(0, application.Docker.CountCalls(operation));
        }

        Assert.Equal(JobStatus.Completed, (await application.GetJobEntityAsync(jobId)).Status);
        Assert.Equal("running", (await client.GetInstanceAsync(instanceId)).Status());
    }
}
