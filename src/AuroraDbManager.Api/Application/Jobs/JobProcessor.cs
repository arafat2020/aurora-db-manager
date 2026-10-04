using AuroraDbManager.Api.Application.Monitoring;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Jobs;

/// <summary>
/// Runs a single job to its end: claims it, executes its handler, retries failed attempts and
/// records the outcome. Knows nothing about HTTP or about how jobs reach it.
/// </summary>
/// <remarks>
/// The processor owns the job's lease; handlers never see it. Claiming a job stores a new lease
/// id together with the <c>running</c> status, the lease is extended in the background while the
/// handler works, and every later update of the job is conditional on that lease id. An execution
/// that lost its lease therefore cannot complete or fail a job another execution now owns.
/// </remarks>
public sealed class JobProcessor(
    AppDbContext db,
    IEnumerable<IJobHandler> handlers,
    IServiceScopeFactory scopeFactory,
    IOptions<JobOptions> options,
    TimeProvider timeProvider,
    AuroraMetrics metrics,
    ILogger<JobProcessor> logger)
{
    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    private TimeSpan LeaseDuration => TimeSpan.FromSeconds(options.Value.LeaseDurationSeconds);

    /// <summary>
    /// Processes the job if it is still pending; otherwise does nothing. If
    /// <paramref name="cancellationToken"/> is cancelled mid-way the interrupted attempt is given
    /// up and the job goes back to <c>pending</c>; it is never marked completed or failed.
    /// </summary>
    public async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null)
        {
            logger.LogWarning("Job {JobId} skipped: it no longer exists", jobId);
            return;
        }

        // Completed and failed jobs are finished; a running job belongs to another execution.
        if (job.Status != JobStatus.Pending)
        {
            logger.LogInformation(
                "Job {JobId} ({JobType}) for instance {InstanceId} skipped: status is {JobStatus}",
                job.Id, job.Type, job.InstanceId, job.Status);
            return;
        }

        var handler = handlers.Single(h => h.Type == job.Type);
        var leaseId = Guid.NewGuid();

        try
        {
            // Status and lease are saved in one conditional update: if another execution claimed
            // the job first, this save fails and this execution backs off.
            job.Start(leaseId, UtcNow + LeaseDuration, UtcNow);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation(
                "Job {JobId} ({JobType}) for instance {InstanceId} skipped: another execution claimed it first",
                job.Id, job.Type, job.InstanceId);
            return;
        }

        // Everything logged from here on, by the handler and what it calls included, says which job it is for.
        using var jobScope = logger.BeginScope("Job {JobId} ({JobType})", job.Id, job.Type);

        // Monotonic: the job's duration is elapsed time, not a difference of wall-clock times.
        var started = timeProvider.GetTimestamp();
        metrics.JobStarted(job.Type);
        logger.LogInformation(
            "Job {JobId} ({JobType}) for instance {InstanceId} started: job lease {LeaseId} acquired",
            job.Id, job.Type, job.InstanceId, leaseId);

        // Cancelled on shutdown, and also when the lease turns out to be lost.
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewal = RenewLeaseAsync(job, leaseId, execution, renewalStop.Token);

        try
        {
            await RunAttemptsAsync(job, handler, started, execution.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            metrics.JobCancelled(job.Type);
            await StopRenewalAsync();
            await ReleaseInterruptedJobAsync(job, leaseId);
            throw;
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            // The renewal loop found the lease gone and has logged it; the job is someone else's now.
            metrics.JobCancelled(job.Type);
        }
        catch (DbUpdateConcurrencyException)
        {
            metrics.JobCancelled(job.Type);
            logger.LogWarning(
                "Job {JobId} ({JobType}) for instance {InstanceId} abandoned: job lease {LeaseId} lost, or the job or its instance was changed or deleted concurrently",
                job.Id, job.Type, job.InstanceId, leaseId);
        }
        finally
        {
            await StopRenewalAsync();
        }

        async Task StopRenewalAsync()
        {
            await renewalStop.CancelAsync();
            await renewal;
        }
    }

    private async Task RunAttemptsAsync(Job job, IJobHandler handler, long started, CancellationToken cancellationToken)
    {
        while (true)
        {
            logger.LogInformation(
                "Job {JobId} ({JobType}) for instance {InstanceId} attempt {Attempt}/{MaxAttempts} started",
                job.Id, job.Type, job.InstanceId, job.Attempt, job.MaxAttempts);

            var failure = await TryExecuteAsync(handler, job, cancellationToken);
            if (failure is null)
            {
                job.Complete(UtcNow);
                await db.SaveChangesAsync(cancellationToken);

                var duration = timeProvider.GetElapsedTime(started);
                metrics.JobCompleted(job.Type, duration);
                logger.LogInformation(
                    "Job {JobId} ({JobType}) for instance {InstanceId} completed on attempt {Attempt} in {DurationMs} ms",
                    job.Id, job.Type, job.InstanceId, job.Attempt, (long)duration.TotalMilliseconds);
                return;
            }

            var failedAttempt = job.Attempt;
            job.FailAttempt(failure.Code, failure.Message, UtcNow);

            if (job.Status == JobStatus.Failed)
            {
                await handler.OnFailedAsync(job, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);

                var duration = timeProvider.GetElapsedTime(started);
                metrics.JobFailed(job.Type, failure.Code, duration);
                logger.LogError(
                    failure,
                    "Job {JobId} ({JobType}) for instance {InstanceId} failed after {Attempt} attempts in {DurationMs} ms: {ErrorCode}",
                    job.Id, job.Type, job.InstanceId, job.Attempt, (long)duration.TotalMilliseconds, failure.Code);
                return;
            }

            // The next attempt begins with its wait. Saved together with the failure, so a job
            // interrupted during the wait is known to have failed this attempt and resumes with
            // the next one.
            job.StartNextAttempt(UtcNow);
            await db.SaveChangesAsync(cancellationToken);

            metrics.JobRetried(job.Type);
            var retryDelay = TimeSpan.FromSeconds(options.Value.RetryDelaySeconds);
            logger.LogWarning(
                failure,
                "Job {JobId} ({JobType}) for instance {InstanceId} attempt {Attempt}/{MaxAttempts} failed with {ErrorCode}; retry scheduled in {RetryDelaySeconds}s",
                job.Id, job.Type, job.InstanceId, failedAttempt, job.MaxAttempts, failure.Code, retryDelay.TotalSeconds);

            await Task.Delay(retryDelay, timeProvider, cancellationToken);
        }
    }

    /// <summary>
    /// Extends the lease at every renewal interval until <paramref name="stop"/> is cancelled. Uses
    /// its own database context, because the handler is using the processor's. If the lease is no
    /// longer this execution's, cancels <paramref name="execution"/>.
    /// </summary>
    private async Task RenewLeaseAsync(Job job, Guid leaseId, CancellationTokenSource execution, CancellationToken stop)
    {
        var interval = TimeSpan.FromSeconds(options.Value.LeaseRenewalIntervalSeconds);
        var jobId = job.Id;

        try
        {
            while (true)
            {
                await Task.Delay(interval, timeProvider, stop);

                int renewed;
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var renewalDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var expiresAt = UtcNow + LeaseDuration;
                    renewed = await renewalDb.Jobs
                        .Where(j => j.Id == jobId && j.LeaseId == leaseId && j.Status == JobStatus.Running)
                        .ExecuteUpdateAsync(update => update.SetProperty(j => j.LeaseExpiresAt, expiresAt), stop);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Tried again at the next interval; the lease only matters once it has expired.
                    logger.LogWarning(
                        exception,
                        "Job {JobId} ({JobType}) for instance {InstanceId}: job lease {LeaseId} could not be renewed",
                        jobId, job.Type, job.InstanceId, leaseId);
                    continue;
                }

                if (renewed == 0)
                {
                    logger.LogWarning(
                        "Job {JobId} ({JobType}) for instance {InstanceId}: job lease {LeaseId} lost; stopping this execution",
                        jobId, job.Type, job.InstanceId, leaseId);
                    await execution.CancelAsync();
                    return;
                }

                logger.LogDebug(
                    "Job {JobId} ({JobType}) for instance {InstanceId}: job lease {LeaseId} renewed",
                    jobId, job.Type, job.InstanceId, leaseId);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// On shutdown, hands the job back right away instead of leaving it to lease expiry. Not
    /// cancellable, and done in a fresh scope because this one's context was interrupted mid-work.
    /// </summary>
    private async Task ReleaseInterruptedJobAsync(Job job, Guid leaseId)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<JobRecovery>().ReleaseAsync(job.Id, leaseId);
        }
        catch (Exception exception)
        {
            // The job stays running; it is recovered once its lease has expired.
            logger.LogWarning(
                exception,
                "Job {JobId} ({JobType}) for instance {InstanceId}: interrupted job could not be released from job lease {LeaseId}",
                job.Id, job.Type, job.InstanceId, leaseId);
        }
    }

    /// <summary>Runs one attempt and returns its failure, or null if it succeeded.</summary>
    private static async Task<JobExecutionException?> TryExecuteAsync(
        IJobHandler handler, Job job, CancellationToken cancellationToken)
    {
        try
        {
            await handler.ExecuteAsync(job, cancellationToken);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JobExecutionException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            // Unknown exceptions may carry internal details, so the job gets a generic error.
            return new JobExecutionException(
                JobErrorCodes.JobExecutionFailed,
                "An unexpected error occurred while executing the job.",
                exception);
        }
    }
}
