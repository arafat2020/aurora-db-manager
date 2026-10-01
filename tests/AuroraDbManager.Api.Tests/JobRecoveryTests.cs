using AuroraDbManager.Api.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AuroraDbManager.Api.Tests;

/// <summary>
/// Recovery of jobs that no execution is working on. "Restart" tests run one factory, dispose it
/// (losing its in-memory queue, like a stopped process) and start a second one on the same database.
/// </summary>
public sealed class JobRecoveryTests : IDisposable
{
    private readonly TempDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private ApiFactory StoppedWorker(TimeProvider? clock = null) => new() { DatabasePath = _database.Path, Clock = clock };

    private ApiFactory RestartedApplication(TimeProvider? clock = null) =>
        new() { DatabasePath = _database.Path, RunWorker = true, Clock = clock };

    private static readonly DateTime LongAgo = DateTime.UtcNow.AddHours(-1);
    private static readonly DateTime FarAhead = DateTime.UtcNow.AddHours(1);

    // --- Across a restart ---------------------------------------------------------------------

    [Fact]
    public async Task Restart_PendingJobWhoseQueueEntryWasLost_IsExecuted()
    {
        Guid instanceId, jobId;
        using (var before = StoppedWorker())
        using (var client = before.CreateClient())
        {
            (instanceId, jobId) = await client.CreateInstanceAsync();
        }

        using var after = RestartedApplication();
        using var restarted = after.CreateClient();
        var job = await restarted.WaitForFinishedJobAsync(jobId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Equal("running", (await restarted.GetInstanceAsync(instanceId)).Status());
        Assert.Equal(1, after.Provisioner.CallCount);
    }

    [Fact]
    public async Task Restart_RunningJobWithExpiredLease_IsRecoveredAndExecuted_WithoutLosingAnAttempt()
    {
        Guid instanceId, jobId;
        using (var before = StoppedWorker())
        using (var client = before.CreateClient())
        {
            (instanceId, jobId) = await client.CreateInstanceAsync();
            await before.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);
        }

        using var after = RestartedApplication();
        using var restarted = after.CreateClient();
        var job = await restarted.WaitForFinishedJobAsync(jobId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Equal("running", (await restarted.GetInstanceAsync(instanceId)).Status());
        Assert.Equal(1, after.Provisioner.CallCount);
    }

    [Fact]
    public async Task Restart_RunningJobWithValidLease_IsNotStolen_UntilTheLeaseExpires()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Guid leasedJobId, otherJobId, leaseId;
        using (var before = StoppedWorker(clock))
        using (var client = before.CreateClient())
        {
            (_, leasedJobId) = await client.CreateInstanceAsync(name: "owned-by-a-live-execution");
            leaseId = await before.SimulateAbandonedExecutionAsync(leasedJobId, clock.GetUtcNow().UtcDateTime.AddMinutes(10));
            (_, otherJobId) = await client.CreateInstanceAsync(name: "lost-from-the-queue");
        }

        using var after = RestartedApplication(clock);
        using var restarted = after.CreateClient();

        // Once the other job is done, startup recovery has run and the worker is processing.
        Assert.Equal("completed", (await restarted.WaitForFinishedJobAsync(otherJobId)).Status());
        var leased = await after.GetJobEntityAsync(leasedJobId);
        Assert.Equal(JobStatus.Running, leased.Status);
        Assert.Equal(leaseId, leased.LeaseId);
        Assert.Equal(1, after.Provisioner.CallCount);

        // Its owner never renews. When the lease has run out, the periodic check recovers the job.
        clock.Advance(TimeSpan.FromMinutes(11));
        var recovered = await restarted.WaitForFinishedJobAsync(leasedJobId);

        Assert.Equal("completed", recovered.Status());
        Assert.Equal(1, recovered.GetProperty("attempt").GetInt32());
        Assert.Equal(2, after.Provisioner.CallCount);
    }

    [Fact]
    public async Task Restart_CompletedAndFailedJobs_AreNotExecutedAgain()
    {
        Guid completedJobId, failedJobId;
        using (var before = StoppedWorker())
        using (var client = before.CreateClient())
        {
            (_, completedJobId) = await client.CreateInstanceAsync(name: "done");
            await before.ProcessJobAsync(completedJobId);
            (_, failedJobId) = await client.CreateInstanceAsync(name: "broken");
            before.Provisioner.FailAllCalls();
            await before.ProcessJobAsync(failedJobId);
        }

        using var after = RestartedApplication();
        using var restarted = after.CreateClient();
        var (_, sentinelJobId) = await restarted.CreateInstanceAsync(name: "sentinel");
        await restarted.WaitForFinishedJobAsync(sentinelJobId);

        Assert.Equal(1, after.Provisioner.CallCount);
        Assert.Equal("completed", (await restarted.GetJobAsync(completedJobId)).Status());
        Assert.Equal("failed", (await restarted.GetJobAsync(failedJobId)).Status());
    }

    [Fact]
    public async Task Shutdown_InterruptedJob_IsLeftPending_AndCompletesAfterRestart()
    {
        Guid instanceId, jobId;
        using (var before = RestartedApplication())
        using (var client = before.CreateClient())
        {
            before.Provisioner.Block();
            (instanceId, jobId) = await client.CreateInstanceAsync();
            await before.Provisioner.WaitForCallsAsync();
            // Disposing the factory stops the host while the job is inside the provisioner.
        }

        using (var stopped = StoppedWorker())
        {
            var interrupted = await stopped.GetJobEntityAsync(jobId);
            Assert.Equal(JobStatus.Pending, interrupted.Status);
            Assert.Equal(0, interrupted.Attempt);
            Assert.Null(interrupted.LeaseId);
            Assert.Null(interrupted.ErrorCode);
        }

        using var after = RestartedApplication();
        using var restarted = after.CreateClient();
        var job = await restarted.WaitForFinishedJobAsync(jobId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Equal("running", (await restarted.GetInstanceAsync(instanceId)).Status());
    }

    // --- Recovery itself ----------------------------------------------------------------------

    [Fact]
    public async Task Recover_ExpiredLease_ReturnsTheJobToPending_WithoutCountingTheAttempt()
    {
        using var factory = StoppedWorker();
        using var client = factory.CreateClient();
        var (_, jobId) = await client.CreateInstanceAsync();
        await factory.SimulateAbandonedExecutionAsync(jobId, LongAgo);

        var queued = await factory.RecoverJobsAsync(includePending: false);

        Assert.Equal([jobId], queued);
        var job = await factory.GetJobEntityAsync(jobId);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(0, job.Attempt);
        Assert.Null(job.LeaseId);
        Assert.Null(job.LeaseExpiresAt);
    }

    [Fact]
    public async Task Recover_ValidLease_LeavesTheJobAlone()
    {
        using var factory = StoppedWorker();
        using var client = factory.CreateClient();
        var (_, jobId) = await client.CreateInstanceAsync();
        var leaseId = await factory.SimulateAbandonedExecutionAsync(jobId, FarAhead);

        var queued = await factory.RecoverJobsAsync(includePending: true);

        Assert.Empty(queued);
        var job = await factory.GetJobEntityAsync(jobId);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(leaseId, job.LeaseId);
        Assert.Equal(1, job.Attempt);
    }

    [Fact]
    public async Task Recover_AtStartup_QueuesPendingJobsOnly()
    {
        using var factory = StoppedWorker();
        using var client = factory.CreateClient();
        var (_, completedJobId) = await client.CreateInstanceAsync(name: "done");
        await factory.ProcessJobAsync(completedJobId);
        var (_, firstPending) = await client.CreateInstanceAsync(name: "first");
        var (_, secondPending) = await client.CreateInstanceAsync(name: "second");

        var queued = await factory.RecoverJobsAsync(includePending: true);

        Assert.Equal([firstPending, secondPending], queued);
    }

    [Fact]
    public async Task Recover_PeriodicCheck_DoesNotQueuePendingJobsAgain()
    {
        using var factory = StoppedWorker();
        using var client = factory.CreateClient();
        await client.CreateInstanceAsync();

        Assert.Empty(await factory.RecoverJobsAsync(includePending: false));
    }

    [Fact]
    public async Task Recover_TwoProcessesLoadTheSameStaleJob_OnlyOneRecoveryIsSaved()
    {
        using var factory = StoppedWorker();
        using var client = factory.CreateClient();
        var (_, jobId) = await client.CreateInstanceAsync();
        await factory.SimulateAbandonedExecutionAsync(jobId, LongAgo);

        await factory.WithDbAsync(async first =>
        {
            var firstJob = await first.Jobs.SingleAsync(j => j.Id == jobId);

            // The other process recovers the job and its worker claims it.
            var winnerLease = await factory.WithDbAsync(async second =>
            {
                var secondJob = await second.Jobs.SingleAsync(j => j.Id == jobId);
                secondJob.ReturnToPending(DateTime.UtcNow);
                await second.SaveChangesAsync();
                var lease = Guid.NewGuid();
                secondJob.Start(lease, FarAhead, DateTime.UtcNow);
                await second.SaveChangesAsync();
                return lease;
            });

            firstJob.ReturnToPending(DateTime.UtcNow);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => first.SaveChangesAsync());

            Assert.Equal(winnerLease, (await factory.GetJobEntityAsync(jobId)).LeaseId);
            return 0;
        });
    }

    [Fact]
    public async Task Recover_RunConcurrently_RecoversTheJobOnce_AndItExecutesOnce()
    {
        using var factory = StoppedWorker();
        using var client = factory.CreateClient();
        var (instanceId, jobId) = await client.CreateInstanceAsync();
        await factory.SimulateAbandonedExecutionAsync(jobId, LongAgo);

        var recoveries = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => factory.RecoverJobsAsync(includePending: false))));
        Assert.Equal([jobId], recoveries.SelectMany(ids => ids));

        // Every "process" then has the job in its queue and tries to run it.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => factory.ProcessJobAsync(jobId))));

        Assert.Equal(1, factory.Provisioner.CallCount);
        var job = await factory.GetJobEntityAsync(jobId);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(1, job.Attempt);
        Assert.Equal("running", (await client.GetInstanceAsync(instanceId)).Status());
    }
}
