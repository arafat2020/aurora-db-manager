using AuroraDbManager.Api.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Tests;

/// <summary>
/// Job processing without the background worker: each test runs the job itself through
/// <see cref="ApiFactory.ProcessJobAsync"/>, so nothing depends on timing.
/// </summary>
public sealed class JobProcessingTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public JobProcessingTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private FakeInstanceProvisioner Provisioner => _factory.Provisioner;

    [Fact]
    public async Task Process_PendingJob_CompletesJobAndMarksInstanceRunning()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.NotNull(job.GetProperty("startedAt").GetString());
        Assert.NotNull(job.GetProperty("completedAt").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, job.GetProperty("error").ValueKind);
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
        Assert.Equal(1, Provisioner.CallCount);
    }

    [Fact]
    public async Task Process_FirstAttemptFails_KeepsInstanceProvisioningWhileRetrying()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        Provisioner.FailNextCalls(1);
        Provisioner.Block();

        var processing = _factory.ProcessJobAsync(jobId);
        await Provisioner.WaitForCallsAsync();
        Provisioner.Release();
        // The first attempt has failed and the second is now held inside the provisioner.
        await Provisioner.WaitForCallsAsync();

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("running", job.Status());
        Assert.Equal(2, job.GetProperty("attempt").GetInt32());
        Assert.Equal("PROVISIONING_FAILED", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("provisioning", (await _client.GetInstanceAsync(instanceId)).Status());

        Provisioner.Release();
        await processing;
    }

    [Fact]
    public async Task Process_FailsThenSucceeds_CompletesAndStopsRetrying()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        Provisioner.FailNextCalls(1);

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(2, job.GetProperty("attempt").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, job.GetProperty("error").ValueKind);
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
        Assert.Equal(2, Provisioner.CallCount);
    }

    [Fact]
    public async Task Process_AllAttemptsFail_MarksJobAndInstanceFailed()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        Provisioner.FailAllCalls();

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        Assert.Equal(3, job.GetProperty("maxAttempts").GetInt32());
        Assert.NotNull(job.GetProperty("completedAt").GetString());
        Assert.Equal("PROVISIONING_FAILED", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Simulated provisioning failure.", job.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("failed", (await _client.GetInstanceAsync(instanceId)).Status());
        Assert.Equal(3, Provisioner.CallCount);
    }

    [Fact]
    public async Task Process_MaxAttemptsOfOne_FailsAfterSingleAttempt()
    {
        using var factory = new ApiFactory { MaxAttempts = 1 };
        using var client = factory.CreateClient();
        var (instanceId, jobId) = await client.CreateInstanceAsync();
        factory.Provisioner.FailAllCalls();

        await factory.ProcessJobAsync(jobId);

        var job = await client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Equal("failed", (await client.GetInstanceAsync(instanceId)).Status());
        Assert.Equal(1, factory.Provisioner.CallCount);
    }

    [Fact]
    public async Task Process_UnexpectedProvisionerException_DoesNotExposeItsDetails()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();
        Provisioner.FailNextCalls(int.MaxValue, new InvalidOperationException("Host=db;Password=secret"));

        await _factory.ProcessJobAsync(jobId);

        var response = await _client.GetAsync($"{ApiClientExtensions.JobsUrl}/{jobId}");
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("PROVISIONING_FAILED", raw);
        Assert.DoesNotContain("secret", raw);
        Assert.DoesNotContain("InvalidOperationException", raw);
        Assert.DoesNotContain("   at ", raw);
    }

    [Fact]
    public async Task Process_CompletedJob_IsNotExecutedAgain()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();
        await _factory.ProcessJobAsync(jobId);
        var completed = await _client.GetJobAsync(jobId);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal(1, Provisioner.CallCount);
        Assert.Equal(completed.GetRawText(), (await _client.GetJobAsync(jobId)).GetRawText());
    }

    [Fact]
    public async Task Process_FailedJob_IsNotExecutedAgain()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        Provisioner.FailNextCalls(3);
        await _factory.ProcessJobAsync(jobId);
        var failed = await _client.GetJobAsync(jobId);

        // The provisioner would succeed now, but the job has used all its attempts.
        await _factory.ProcessJobAsync(jobId);

        Assert.Equal(3, Provisioner.CallCount);
        Assert.Equal(failed.GetRawText(), (await _client.GetJobAsync(jobId)).GetRawText());
        Assert.Equal("failed", (await _client.GetInstanceAsync(instanceId)).Status());
    }

    [Fact]
    public async Task Process_JobAlreadyRunning_IsSkipped()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();
        Provisioner.Block();
        var first = _factory.ProcessJobAsync(jobId);
        await Provisioner.WaitForCallsAsync();

        // A second execution of the same job while the first is still inside the provisioner.
        await _factory.ProcessJobAsync(jobId);

        Assert.Equal(1, Provisioner.CallCount);
        Provisioner.Release();
        await first;
        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
        Assert.Equal(1, Provisioner.CallCount);
    }

    [Fact]
    public async Task Process_SameJobConcurrently_ProvisionsOnce()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _factory.ProcessJobAsync(jobId))));

        Assert.Equal(1, Provisioner.CallCount);
        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
    }

    [Fact]
    public async Task Start_JobLoadedByTwoExecutions_OnlyTheFirstClaimIsSaved()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();

        await _factory.WithDbAsync(async first =>
        {
            var firstJob = await first.Jobs.SingleAsync(j => j.Id == jobId);

            await _factory.WithDbAsync(async second =>
            {
                var secondJob = await second.Jobs.SingleAsync(j => j.Id == jobId);
                secondJob.Start(DateTime.UtcNow);
                return await second.SaveChangesAsync();
            });

            firstJob.Start(DateTime.UtcNow);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => first.SaveChangesAsync());
            return 0;
        });
    }

    [Fact]
    public async Task Process_InstanceAlreadyRunning_DoesNotProvisionAgain()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        await _factory.ProcessJobAsync(jobId);
        var secondJobId = await _factory.WithDbAsync(async db =>
        {
            var job = Job.Create(JobType.ProvisionInstance, instanceId, maxAttempts: 3, DateTime.UtcNow);
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            return job.Id;
        });

        await _factory.ProcessJobAsync(secondJobId);

        Assert.Equal(1, Provisioner.CallCount);
        Assert.Equal("completed", (await _client.GetJobAsync(secondJobId)).Status());
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
    }

    [Fact]
    public async Task Process_Cancelled_DoesNotCompleteJobOrInstance()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        Provisioner.Block();
        using var cancellation = new CancellationTokenSource();
        var processing = _factory.ProcessJobAsync(jobId, cancellation.Token);
        await Provisioner.WaitForCallsAsync();

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("running", job.Status());
        Assert.Null(job.GetProperty("completedAt").GetString());
        Assert.Equal("provisioning", (await _client.GetInstanceAsync(instanceId)).Status());
    }

    [Fact]
    public async Task Process_InstanceDeletedBeforeProcessing_DoesNothing()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        await _client.DeleteAsync($"{ApiClientExtensions.InstancesUrl}/{instanceId}");

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal(0, Provisioner.CallCount);
    }

    [Fact]
    public async Task Process_InstanceDeletedWhileProvisioning_AbandonsJobQuietly()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        Provisioner.Block();
        var processing = _factory.ProcessJobAsync(jobId);
        await Provisioner.WaitForCallsAsync();

        await _client.DeleteAsync($"{ApiClientExtensions.InstancesUrl}/{instanceId}");
        Provisioner.Release();

        await processing;
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Jobs.CountAsync()));
    }
}
