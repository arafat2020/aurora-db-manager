using AuroraDbManager.Api.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AuroraDbManager.Api.Tests;

/// <summary>
/// Lease ownership of running jobs. The worker is off; tests run jobs themselves and move the
/// clock by hand, so nothing waits for real time to pass.
/// </summary>
public sealed class JobLeaseTests : IDisposable
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(20);

    // A job held in the provisioner must end the way the test expects; if it does not, fail rather than hang.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public JobLeaseTests()
    {
        _factory = new ApiFactory { Clock = _clock };
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private FakeInstanceProvisioner Provisioner => _factory.Provisioner;

    /// <summary>Starts processing and returns once the job is held inside the provisioner.</summary>
    private async Task<Task> StartProcessingHeldInProvisionerAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        Provisioner.Block();
        var processing = _factory.ProcessJobAsync(jobId, cancellationToken);
        await Provisioner.WaitForCallsAsync();
        return processing;
    }

    /// <summary>What a second worker does once the first one's lease has expired: recover the job and claim it.</summary>
    private Task<Guid> TakeOverAsync(Guid jobId) => _factory.WithDbAsync(async db =>
    {
        var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
        job.ReturnToPending(_clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync();

        var newLease = Guid.NewGuid();
        job.Start(newLease, _clock.GetUtcNow().UtcDateTime.AddHours(1), _clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync();
        return newLease;
    });

    /// <summary>Moves the clock forward in renewal-interval steps until the condition holds.</summary>
    private async Task AdvanceUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            Assert.False(timeout.IsCancellationRequested, "The expected state was not reached in time.");
            _clock.Advance(RenewalInterval);
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    [Fact]
    public async Task Claim_PersistsTheLeaseTogetherWithTheRunningStatus()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();
        var claimedAt = _clock.GetUtcNow().UtcDateTime;

        var processing = await StartProcessingHeldInProvisionerAsync(jobId);

        var job = await _factory.GetJobEntityAsync(jobId);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.NotNull(job.LeaseId);
        Assert.Equal(claimedAt + LeaseDuration, job.LeaseExpiresAt);

        Provisioner.Release();
        await processing.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Completion_ClearsTheLease()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();

        await _factory.ProcessJobAsync(jobId);

        var job = await _factory.GetJobEntityAsync(jobId);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Null(job.LeaseId);
        Assert.Null(job.LeaseExpiresAt);
    }

    [Fact]
    public async Task FinalFailure_ClearsTheLease()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();
        Provisioner.FailAllCalls();

        await _factory.ProcessJobAsync(jobId);

        var job = await _factory.GetJobEntityAsync(jobId);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Null(job.LeaseId);
    }

    [Fact]
    public async Task LongRunningJob_KeepsRenewingItsLease()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();
        var processing = await StartProcessingHeldInProvisionerAsync(jobId);
        var claimed = await _factory.GetJobEntityAsync(jobId);

        // Far longer than one lease duration, in renewal-interval steps.
        var expiry = claimed.LeaseExpiresAt!.Value;
        for (var renewal = 0; renewal < 5; renewal++)
        {
            var previous = expiry;
            await AdvanceUntilAsync(async () => (await _factory.GetJobEntityAsync(jobId)).LeaseExpiresAt > previous);
            expiry = (await _factory.GetJobEntityAsync(jobId)).LeaseExpiresAt!.Value;
        }

        var job = await _factory.GetJobEntityAsync(jobId);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(claimed.LeaseId, job.LeaseId);
        Assert.True(job.LeaseExpiresAt > _clock.GetUtcNow().UtcDateTime, "The lease must still be valid.");
        Assert.True(job.LeaseExpiresAt >= claimed.LeaseExpiresAt + (5 * RenewalInterval));

        // A lease that is being renewed is never recovered.
        Assert.Empty(await _factory.RecoverJobsAsync(includePending: false));

        Provisioner.Release();
        await processing.WaitAsync(TestTimeout);
        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
    }

    [Fact]
    public async Task ExecutionThatLostItsLease_CannotCompleteTheJobOrChangeTheInstance()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        var processing = await StartProcessingHeldInProvisionerAsync(jobId);

        var otherLease = await TakeOverAsync(jobId);
        Provisioner.Release();
        await processing.WaitAsync(TestTimeout);

        // The first execution finished provisioning, but its completion was rejected as a whole.
        var job = await _factory.GetJobEntityAsync(jobId);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(otherLease, job.LeaseId);
        Assert.Null(job.CompletedAt);
        Assert.Equal("provisioning", (await _client.GetInstanceAsync(instanceId)).Status());
    }

    [Fact]
    public async Task ExecutionThatLostItsLease_CannotFailTheJobEither()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        Provisioner.FailAllCalls();
        var processing = await StartProcessingHeldInProvisionerAsync(jobId);

        var otherLease = await TakeOverAsync(jobId);
        Provisioner.Release();
        await processing.WaitAsync(TestTimeout);

        var job = await _factory.GetJobEntityAsync(jobId);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(otherLease, job.LeaseId);
        Assert.Null(job.ErrorCode);
        Assert.Equal(1, Provisioner.CallCount);
        Assert.Equal("provisioning", (await _client.GetInstanceAsync(instanceId)).Status());
    }

    [Fact]
    public async Task Renewal_ThatFindsTheLeaseGone_StopsTheExecution()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        var processing = await StartProcessingHeldInProvisionerAsync(jobId);
        var otherLease = await TakeOverAsync(jobId);

        // The provisioner is never released: only the lost lease can end this execution.
        await AdvanceUntilAsync(() => Task.FromResult(processing.IsCompleted));
        await processing.WaitAsync(TestTimeout);

        var job = await _factory.GetJobEntityAsync(jobId);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(otherLease, job.LeaseId);
        Assert.Equal(0, Provisioner.InFlight);
        Assert.Equal("provisioning", (await _client.GetInstanceAsync(instanceId)).Status());
    }

    [Fact]
    public async Task Cancellation_StopsLeaseRenewal()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();
        using var cancellation = new CancellationTokenSource();
        var processing = await StartProcessingHeldInProvisionerAsync(jobId, cancellation.Token);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TestTimeout));
        var released = await _factory.GetJobEntityAsync(jobId);

        // Nothing renews any more, however much time passes.
        _clock.Advance(10 * LeaseDuration);
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        var job = await _factory.GetJobEntityAsync(jobId);
        Assert.Equal(JobStatus.Pending, released.Status);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Null(job.LeaseId);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Equal(released.UpdatedAt, job.UpdatedAt);
    }

    [Fact]
    public async Task InterruptedFirstAttempt_IsNotCounted_WhenTheJobRunsAgain()
    {
        var (instanceId, jobId) = await _client.CreateInstanceAsync();
        using var cancellation = new CancellationTokenSource();
        var processing = await StartProcessingHeldInProvisionerAsync(jobId, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TestTimeout));

        Provisioner.Release();
        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
    }

    [Fact]
    public async Task InterruptedSecondAttempt_ResumesAsSecondAttempt_KeepingTheFirstFailure()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();
        Provisioner.FailNextCalls(1);
        using var cancellation = new CancellationTokenSource();
        var processing = await StartProcessingHeldInProvisionerAsync(jobId, cancellation.Token);
        Provisioner.Release();
        await Provisioner.WaitForCallsAsync();

        // Interrupted while the second attempt is inside the provisioner.
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TestTimeout));

        var interrupted = await _factory.GetJobEntityAsync(jobId);
        Assert.Equal(JobStatus.Pending, interrupted.Status);
        Assert.Equal(1, interrupted.Attempt);
        Assert.Equal("PROVISIONING_FAILED", interrupted.ErrorCode);

        Provisioner.Release();
        await _factory.ProcessJobAsync(jobId);

        var job = await _factory.GetJobEntityAsync(jobId);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(2, job.Attempt);
    }

    [Fact]
    public async Task JobApi_DoesNotExposeTheLease()
    {
        var (_, jobId) = await _client.CreateInstanceAsync();
        var processing = await StartProcessingHeldInProvisionerAsync(jobId);
        var leaseId = (await _factory.GetJobEntityAsync(jobId)).LeaseId!.Value;

        var body = await _client.GetStringAsync($"{ApiClientExtensions.JobsUrl}/{jobId}");

        Assert.DoesNotContain("lease", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(leaseId.ToString(), body);

        Provisioner.Release();
        await processing.WaitAsync(TestTimeout);
    }
}
