using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;

namespace AuroraDbManager.Api.Tests;

public sealed class DomainTransitionTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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

        job.Start(Now.AddSeconds(1));
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
        job.Start(Now);

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
        job.Start(Now);
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
        job.Start(Now);

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
        running.Start(Now);
        Assert.Throws<InvalidOperationException>(() => running.Start(Now));

        var completed = NewJob();
        completed.Start(Now);
        completed.Complete(Now);
        Assert.Throws<InvalidOperationException>(() => completed.Start(Now));
        Assert.Throws<InvalidOperationException>(() => completed.Complete(Now));
        Assert.Throws<InvalidOperationException>(() => completed.FailAttempt("CODE", "message", Now));

        var failed = NewJob(maxAttempts: 1);
        failed.Start(Now);
        failed.FailAttempt("CODE", "message", Now);
        Assert.Throws<InvalidOperationException>(() => failed.Start(Now));
        Assert.Throws<InvalidOperationException>(() => failed.StartNextAttempt(Now));
        Assert.Throws<InvalidOperationException>(() => failed.Complete(Now));
    }

    [Fact]
    public void Job_FailAttempt_TruncatesLongMessages()
    {
        var job = NewJob();
        job.Start(Now);

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
    public void Instance_MarkFailed_MovesFromProvisioningToFailed()
    {
        var instance = NewInstance();

        instance.MarkFailed(Now);

        Assert.Equal(InstanceStatus.Failed, instance.Status);
    }

    [Fact]
    public void Instance_TransitionsFromOtherStatuses_AreRejected()
    {
        var running = NewInstance();
        running.MarkRunning(Now);
        Assert.Throws<InvalidOperationException>(() => running.MarkRunning(Now));
        Assert.Throws<InvalidOperationException>(() => running.MarkFailed(Now));

        var failed = NewInstance();
        failed.MarkFailed(Now);
        Assert.Throws<InvalidOperationException>(() => failed.MarkRunning(Now));
        Assert.Throws<InvalidOperationException>(() => failed.MarkFailed(Now));
    }
}
