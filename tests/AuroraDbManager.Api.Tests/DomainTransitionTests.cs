using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;

namespace AuroraDbManager.Api.Tests;

public sealed class DomainTransitionTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Lease = Guid.NewGuid();

    private static Job NewJob(int maxAttempts = 3) =>
        Job.Create(JobType.ProvisionInstance, Guid.NewGuid(), maxAttempts, Now);

    private static Instance NewInstance() =>
        Instance.Create("db", InstanceEngine.Postgres, "16", 1, 1024, 20, Now);

    [Fact]
    public void Job_NewJob_IsPendingWithNoAttempts()
    {
        var job = NewJob();

        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(0, job.Attempt);
        Assert.Null(job.StartedAt);
        Assert.Null(job.CompletedAt);
    }

    [Fact]
    public void Job_StartThenComplete_FollowsLifecycle()
    {
        var job = NewJob();

        job.Start(Lease, Now.AddMinutes(1), Now.AddSeconds(1));
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(1, job.Attempt);
        Assert.Equal(Now.AddSeconds(1), job.StartedAt);

        job.Complete(Now.AddSeconds(2));
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(Now.AddSeconds(2), job.CompletedAt);
        Assert.Equal(Now.AddSeconds(2), job.UpdatedAt);
    }

    [Fact]
    public void Job_FailAttempt_StaysRunningUntilAttemptsAreExhausted()
    {
        var job = NewJob(maxAttempts: 2);
        job.Start(Lease, Now.AddMinutes(1), Now);

        job.FailAttempt("CODE", "first", Now);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Null(job.CompletedAt);
        Assert.Equal("first", job.ErrorMessage);

        job.StartNextAttempt(Now);
        Assert.Equal(2, job.Attempt);

        job.FailAttempt("CODE", "second", Now.AddSeconds(5));
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(Now.AddSeconds(5), job.CompletedAt);
        Assert.Equal("second", job.ErrorMessage);
    }

    [Fact]
    public void Job_CompleteAfterFailedAttempt_ClearsError()
    {
        var job = NewJob();
        job.Start(Lease, Now.AddMinutes(1), Now);
        job.FailAttempt("CODE", "message", Now);
        job.StartNextAttempt(Now);

        job.Complete(Now);

        Assert.Null(job.ErrorCode);
        Assert.Null(job.ErrorMessage);
    }

    [Fact]
    public void Job_StartNextAttempt_BeyondMaxAttempts_IsRejected()
    {
        var job = NewJob(maxAttempts: 1);
        job.Start(Lease, Now.AddMinutes(1), Now);

        Assert.Throws<InvalidOperationException>(() => job.StartNextAttempt(Now));
        Assert.Equal(1, job.Attempt);
    }

    [Fact]
    public void Job_InvalidTransitions_AreRejected()
    {
        var pending = NewJob();
        Assert.Throws<InvalidOperationException>(() => pending.Complete(Now));
        Assert.Throws<InvalidOperationException>(() => pending.FailAttempt("CODE", "message", Now));
        Assert.Throws<InvalidOperationException>(() => pending.StartNextAttempt(Now));

        var running = NewJob();
        running.Start(Lease, Now.AddMinutes(1), Now);
        Assert.Throws<InvalidOperationException>(() => running.Start(Lease, Now.AddMinutes(1), Now));

        var completed = NewJob();
        completed.Start(Lease, Now.AddMinutes(1), Now);
        completed.Complete(Now);
        Assert.Throws<InvalidOperationException>(() => completed.Start(Lease, Now.AddMinutes(1), Now));
        Assert.Throws<InvalidOperationException>(() => completed.Complete(Now));
        Assert.Throws<InvalidOperationException>(() => completed.FailAttempt("CODE", "message", Now));

        var failed = NewJob(maxAttempts: 1);
        failed.Start(Lease, Now.AddMinutes(1), Now);
        failed.FailAttempt("CODE", "message", Now);
        Assert.Throws<InvalidOperationException>(() => failed.Start(Lease, Now.AddMinutes(1), Now));
        Assert.Throws<InvalidOperationException>(() => failed.StartNextAttempt(Now));
        Assert.Throws<InvalidOperationException>(() => failed.Complete(Now));
    }

    [Fact]
    public void Job_Start_TakesTheLease_AndFinishingGivesItUp()
    {
        var completed = NewJob();
        completed.Start(Lease, Now.AddMinutes(1), Now);
        Assert.Equal(Lease, completed.LeaseId);
        Assert.Equal(Now.AddMinutes(1), completed.LeaseExpiresAt);

        completed.Complete(Now);
        Assert.Null(completed.LeaseId);
        Assert.Null(completed.LeaseExpiresAt);

        var failed = NewJob(maxAttempts: 1);
        failed.Start(Lease, Now.AddMinutes(1), Now);
        failed.FailAttempt("CODE", "message", Now);
        Assert.Null(failed.LeaseId);
        Assert.Null(failed.LeaseExpiresAt);
    }

    [Fact]
    public void Job_FailedAttemptWithRetriesLeft_KeepsTheLease()
    {
        var job = NewJob();
        job.Start(Lease, Now.AddMinutes(1), Now);

        job.FailAttempt("CODE", "message", Now);
        job.StartNextAttempt(Now);

        Assert.Equal(Lease, job.LeaseId);
    }

    [Fact]
    public void Job_ReturnToPending_GivesUpTheLeaseAndDoesNotCountTheInterruptedAttempt()
    {
        var job = NewJob();
        job.Start(Lease, Now.AddMinutes(1), Now);

        job.ReturnToPending(Now.AddSeconds(30));

        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(0, job.Attempt);
        Assert.Null(job.LeaseId);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Equal(Now, job.StartedAt);

        var secondLease = Guid.NewGuid();
        job.Start(secondLease, Now.AddMinutes(2), Now.AddMinutes(1));
        Assert.Equal(1, job.Attempt);
        Assert.Equal(secondLease, job.LeaseId);
        Assert.Equal(Now, job.StartedAt);
    }

    [Fact]
    public void Job_ReturnToPending_DuringSecondAttempt_ResumesWithTheSecondAttempt()
    {
        var job = NewJob();
        job.Start(Lease, Now.AddMinutes(1), Now);
        job.FailAttempt("CODE", "first attempt failed", Now);
        job.StartNextAttempt(Now);

        job.ReturnToPending(Now);
        Assert.Equal(1, job.Attempt);
        Assert.Equal("first attempt failed", job.ErrorMessage);

        job.Start(Guid.NewGuid(), Now.AddMinutes(1), Now);
        Assert.Equal(2, job.Attempt);
    }

    [Fact]
    public void Job_ReturnToPending_OnlyFromRunning()
    {
        var pending = NewJob();
        Assert.Throws<InvalidOperationException>(() => pending.ReturnToPending(Now));

        var completed = NewJob();
        completed.Start(Lease, Now.AddMinutes(1), Now);
        completed.Complete(Now);
        Assert.Throws<InvalidOperationException>(() => completed.ReturnToPending(Now));
    }

    [Fact]
    public void Job_FailAttempt_TruncatesLongMessages()
    {
        var job = NewJob();
        job.Start(Lease, Now.AddMinutes(1), Now);

        job.FailAttempt("CODE", new string('x', Job.ErrorMessageMaxLength + 50), Now);

        Assert.Equal(Job.ErrorMessageMaxLength, job.ErrorMessage!.Length);
    }

    [Fact]
    public void Instance_MarkRunning_MovesFromProvisioningToRunning()
    {
        var instance = NewInstance();

        instance.MarkRunning(Now.AddSeconds(3));

        Assert.Equal(InstanceStatus.Running, instance.Status);
        Assert.Equal(Now.AddSeconds(3), instance.UpdatedAt);
    }

    [Fact]
    public void Instance_MarkFailed_MovesFromProvisioningToFailed_AndRecordsWhy()
    {
        var instance = NewInstance();

        instance.MarkFailed("CODE", "message", Now);

        Assert.Equal(InstanceStatus.Failed, instance.Status);
        Assert.Equal("CODE", instance.ErrorCode);
        Assert.Equal("message", instance.ErrorMessage);
    }

    [Fact]
    public void Instance_MarkFailed_MovesFromRunningToFailed()
    {
        var instance = NewInstance();
        instance.MarkRunning(Now);

        instance.MarkFailed("DATABASE_CONTAINER_MISSING", "gone", Now.AddHours(1));

        Assert.Equal(InstanceStatus.Failed, instance.Status);
        Assert.Equal("DATABASE_CONTAINER_MISSING", instance.ErrorCode);
        Assert.Equal(Now.AddHours(1), instance.UpdatedAt);
    }

    [Fact]
    public void Instance_TransitionsFromOtherStatuses_AreRejected()
    {
        var running = NewInstance();
        running.MarkRunning(Now);
        Assert.Throws<InvalidOperationException>(() => running.MarkRunning(Now));

        var failed = NewInstance();
        failed.MarkFailed("CODE", "message", Now);
        Assert.Throws<InvalidOperationException>(() => failed.MarkRunning(Now));
        Assert.Throws<InvalidOperationException>(() => failed.MarkFailed("CODE", "message", Now));
    }
}
