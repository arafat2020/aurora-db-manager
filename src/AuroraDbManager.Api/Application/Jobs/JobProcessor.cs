using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Jobs;

/// <summary>
/// Runs a single job to its end: claims it, executes its handler, retries failed attempts and
/// records the outcome. Knows nothing about HTTP or about how jobs reach it.
/// </summary>
public sealed class JobProcessor(
    AppDbContext db,
    IEnumerable<IJobHandler> handlers,
    IOptions<JobOptions> options,
    TimeProvider timeProvider,
    ILogger<JobProcessor> logger)
{
    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Processes the job if it is still pending; otherwise does nothing. If
    /// <paramref name="cancellationToken"/> is cancelled mid-way the job is left
    /// <c>running</c>, never marked completed.
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

        try
        {
            // The status concurrency token makes this the claim: if another execution started
            // the job first, the save fails and this one backs off.
            job.Start(UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Job {JobId} ({JobType}) for instance {InstanceId} started",
                job.Id, job.Type, job.InstanceId);

            await RunAttemptsAsync(job, handler, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning(
                "Job {JobId} ({JobType}) for instance {InstanceId} abandoned: the job or its instance was changed or deleted concurrently",
                job.Id, job.Type, job.InstanceId);
        }
    }

    private async Task RunAttemptsAsync(Job job, IJobHandler handler, CancellationToken cancellationToken)
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
                logger.LogInformation(
                    "Job {JobId} ({JobType}) for instance {InstanceId} completed on attempt {Attempt}",
                    job.Id, job.Type, job.InstanceId, job.Attempt);
                return;
            }

            job.FailAttempt(failure.Code, failure.Message, UtcNow);

            if (job.Status == JobStatus.Failed)
            {
                await handler.OnFailedAsync(job, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                logger.LogError(
                    failure,
                    "Job {JobId} ({JobType}) for instance {InstanceId} failed after {Attempt} attempts: {ErrorCode}",
                    job.Id, job.Type, job.InstanceId, job.Attempt, failure.Code);
                return;
            }

            await db.SaveChangesAsync(cancellationToken);

            var retryDelay = TimeSpan.FromSeconds(options.Value.RetryDelaySeconds);
            logger.LogWarning(
                failure,
                "Job {JobId} ({JobType}) for instance {InstanceId} attempt {Attempt}/{MaxAttempts} failed with {ErrorCode}; retry scheduled in {RetryDelaySeconds}s",
                job.Id, job.Type, job.InstanceId, job.Attempt, job.MaxAttempts, failure.Code, retryDelay.TotalSeconds);

            await Task.Delay(retryDelay, timeProvider, cancellationToken);

            job.StartNextAttempt(UtcNow);
            await db.SaveChangesAsync(cancellationToken);
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
