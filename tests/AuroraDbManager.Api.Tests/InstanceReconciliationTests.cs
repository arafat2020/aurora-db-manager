using System.Net;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Tests;

/// <summary>Reconciliation rules per instance status, with the provisioner faked.</summary>
public sealed class InstanceReconciliationTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public InstanceReconciliationTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private FakeInstanceProvisioner Provisioner => _factory.Provisioner;

    private async Task<Guid> CreateRunningInstanceAsync(string name = "production-db")
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync(name: name);
        await _factory.ProcessJobAsync(jobId);
        return instanceId;
    }

    private Task<int> CountJobsAsync(Guid instanceId) =>
        _factory.WithDbAsync(db => db.Jobs.CountAsync(j => j.InstanceId == instanceId));

    // --- running ------------------------------------------------------------------------------

    [Fact]
    public async Task RunningInstance_DatabaseIsUpOrCouldBeStarted_StaysRunning()
    {
        var instanceId = await CreateRunningInstanceAsync();

        var report = await _factory.ReconcileAsync();

        Assert.Equal([instanceId], Provisioner.EnsureRunningInstanceIds);
        Assert.Equal(1, report.Verified);
        Assert.Empty(report.Failed);
        var instance = await _client.GetInstanceAsync(instanceId);
        Assert.Equal("running", instance.Status());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, instance.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task RunningInstance_CannotBeBroughtBack_BecomesFailedWithSafeError()
    {
        var instanceId = await CreateRunningInstanceAsync();
        Provisioner.EnsureRunningFailure = new InstanceProvisioningException(
            "DATABASE_READINESS_TIMEOUT",
            "The database did not become ready within 120 seconds.",
            new InvalidOperationException("raw docker detail"));

        var report = await _factory.ReconcileAsync();

        Assert.Equal([instanceId], report.Failed);
        var response = await _client.GetAsync($"{ApiClientExtensions.InstancesUrl}/{instanceId}");
        var instance = await response.ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("failed", instance.Status());
        Assert.Equal("DATABASE_READINESS_TIMEOUT", instance.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "The database did not become ready within 120 seconds.",
            instance.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("raw docker detail", await response.Content.ReadAsStringAsync());
        Assert.Empty(Provisioner.DeprovisionedInstanceIds);
    }

    [Fact]
    public async Task RunningInstance_ProvisionerUnavailable_StaysRunning_AndTheApiKeepsWorking()
    {
        var instanceId = await CreateRunningInstanceAsync();
        Provisioner.EnsureRunningFailure = new InstanceProvisioningException("DOCKER_UNAVAILABLE", "Docker is not available.")
        {
            ProvisionerUnavailable = true
        };
        Provisioner.ListResourcesFailure = Provisioner.EnsureRunningFailure;

        var report = await _factory.ReconcileAsync();

        Assert.Equal(1, report.Unverified);
        Assert.Equal(0, report.Verified);
        Assert.Empty(report.Failed);
        Assert.Empty(report.Orphans);
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(ApiClientExtensions.InstancesUrl)).StatusCode);
    }

    [Fact]
    public async Task RunningInstance_UnexpectedError_StaysRunning()
    {
        var instanceId = await CreateRunningInstanceAsync();
        Provisioner.EnsureRunningFailure = new InvalidOperationException("bug");

        var report = await _factory.ReconcileAsync();

        Assert.Equal(1, report.Unverified);
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
    }

    [Fact]
    public async Task RunningInstances_AreEachReconciled_AndOnlyTheBrokenOnesFail()
    {
        var first = await CreateRunningInstanceAsync("first");
        var second = await CreateRunningInstanceAsync("second");
        var third = await CreateRunningInstanceAsync("third");

        var report = await _factory.ReconcileAsync();

        Assert.Equal(3, report.Verified);
        Assert.Equal(new[] { first, second, third }.Order(), Provisioner.EnsureRunningInstanceIds.Order());
    }

    // --- failed -------------------------------------------------------------------------------

    [Fact]
    public async Task FailedInstance_IsNotTouched_AndRemainsFailed()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        Provisioner.FailAllCalls();
        await _factory.ProcessJobAsync(jobId);
        var callsBefore = Provisioner.CallCount;

        var report = await _factory.ReconcileAsync();

        Assert.Empty(Provisioner.EnsureRunningInstanceIds);
        Assert.Equal(callsBefore, Provisioner.CallCount);
        Assert.Empty(report.JobsCreated);
        Assert.Equal(1, await CountJobsAsync(instanceId));
        var instance = await _client.GetInstanceAsync(instanceId);
        Assert.Equal("failed", instance.Status());
        Assert.Equal("PROVISIONING_FAILED", instance.GetProperty("error").GetProperty("code").GetString());
    }

    // --- provisioning -------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvisioningInstance_WithUnfinishedJob_GetsNoSecondJob(bool jobIsRunning)
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        if (jobIsRunning)
        {
            await _factory.SimulateAbandonedExecutionAsync(jobId, DateTime.UtcNow.AddHours(1));
        }

        var report = await _factory.ReconcileAsync();

        Assert.Empty(report.JobsCreated);
        Assert.Equal(1, await CountJobsAsync(instanceId));
        Assert.Empty(Provisioner.EnsureRunningInstanceIds);
        Assert.Equal("provisioning", (await _client.GetInstanceAsync(instanceId)).Status());
    }

    [Fact]
    public async Task ProvisioningInstance_WithoutAnyJob_GetsExactlyOne_HoweverOftenReconciliationRuns()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        await _factory.WithDbAsync(db => db.Jobs.Where(j => j.Id == jobId).ExecuteDeleteAsync());

        var first = await _factory.ReconcileAsync();
        var second = await _factory.ReconcileAsync();

        Assert.Equal([instanceId], first.JobsCreated);
        Assert.Empty(second.JobsCreated);
        var job = await _factory.WithDbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.InstanceId == instanceId));
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(JobType.ProvisionInstance, job.Type);

        // The new job is queued and provisions the instance like any other.
        await _factory.ProcessJobAsync(job.Id);
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
    }

    [Fact]
    public async Task UnfinishedJobs_AreUniquePerInstance_InTheDatabase()
    {
        var (instanceId, _) = await _client.CreateInstanceAsync();

        await Assert.ThrowsAsync<DbUpdateException>(() => _factory.WithDbAsync(async db =>
        {
            db.Jobs.Add(Job.Create(JobType.ProvisionInstance, instanceId, 3, DateTime.UtcNow));
            return await db.SaveChangesAsync();
        }));

        Assert.Equal(1, await CountJobsAsync(instanceId));
    }

    // --- orphans ------------------------------------------------------------------------------

    [Fact]
    public async Task ResourcesWithoutAnInstance_AreReportedAsOrphans_AndNothingIsRemoved()
    {
        var instanceId = await CreateRunningInstanceAsync();
        var unknownInstance = Guid.NewGuid();
        var own = new ProvisionedResource("container", $"aurora-instance-{instanceId}", instanceId);
        var orphanContainer = new ProvisionedResource("container", $"aurora-instance-{unknownInstance}", unknownInstance);
        var orphanVolume = new ProvisionedResource("volume", $"aurora-instance-{unknownInstance}-data", unknownInstance);
        var unlabelled = new ProvisionedResource("volume", "aurora-instance-broken-label", null);
        Provisioner.Resources.AddRange([own, orphanContainer, orphanVolume, unlabelled]);

        var report = await _factory.ReconcileAsync();

        Assert.Equal([orphanContainer, orphanVolume, unlabelled], report.Orphans);
        Assert.Empty(Provisioner.DeprovisionedInstanceIds);
        Assert.Equal([instanceId], Provisioner.EnsureRunningInstanceIds);
    }

    // --- at startup ---------------------------------------------------------------------------

    [Fact]
    public async Task Startup_ReconcilesRunningInstances_BeforeTheWorkerTakesJobs()
    {
        using var database = new TempDatabase();
        Guid runningId, pendingJobId;
        using (var before = new ApiFactory { DatabasePath = database.Path })
        using (var client = before.CreateClient())
        {
            (runningId, var jobId) = await client.CreateInstanceAsync(name: "already-running");
            await before.ProcessJobAsync(jobId);
            (_, pendingJobId) = await client.CreateInstanceAsync(name: "lost-from-the-queue");
        }

        using var after = new ApiFactory { DatabasePath = database.Path, RunWorker = true };
        using var restarted = after.CreateClient();
        await restarted.WaitForFinishedJobAsync(pendingJobId);

        // The job ran, so the worker had started, which it only does after reconciliation.
        Assert.Equal([runningId], after.Provisioner.EnsureRunningInstanceIds);
        Assert.Equal("running", (await restarted.GetInstanceAsync(runningId)).Status());
    }

    [Fact]
    public async Task Startup_ProvisionerUnavailable_ApiStartsAndServes()
    {
        using var database = new TempDatabase();
        Guid runningId;
        using (var before = new ApiFactory { DatabasePath = database.Path })
        using (var client = before.CreateClient())
        {
            (runningId, var jobId) = await client.CreateInstanceAsync();
            await before.ProcessJobAsync(jobId);
        }

        var unavailable = new InstanceProvisioningException("DOCKER_UNAVAILABLE", "Docker is not available.")
        {
            ProvisionerUnavailable = true
        };
        using var after = new ApiFactory { DatabasePath = database.Path, RunWorker = true };
        after.Provisioner.EnsureRunningFailure = unavailable;
        after.Provisioner.ListResourcesFailure = unavailable;
        after.Provisioner.FailNextCalls(int.MaxValue, unavailable);
        using var restarted = after.CreateClient();

        // New work is accepted and fails through the normal job mechanism.
        var (newInstanceId, newJobId) = await restarted.CreateInstanceAsync(name: "while-docker-is-down");
        var job = await restarted.WaitForFinishedJobAsync(newJobId);

        Assert.Equal("failed", job.Status());
        Assert.Equal("DOCKER_UNAVAILABLE", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("failed", (await restarted.GetInstanceAsync(newInstanceId)).Status());
        Assert.Equal("running", (await restarted.GetInstanceAsync(runningId)).Status());
    }
}
