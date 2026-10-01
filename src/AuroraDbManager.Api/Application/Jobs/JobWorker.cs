using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Jobs;

/// <summary>
/// Reads job ids from the <see cref="JobQueue"/> and hands each to a <see cref="JobProcessor"/>
/// in its own DI scope, running at most <see cref="JobOptions.MaxConcurrency"/> jobs at once.
/// </summary>
public sealed class JobWorker(
    JobQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<JobOptions> options,
    ILogger<JobWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.Value.MaxConcurrency,
            CancellationToken = stoppingToken
        };

        logger.LogInformation("Job worker started with concurrency {MaxConcurrency}", parallelOptions.MaxDegreeOfParallelism);

        // On shutdown the token stops the dequeue loop and cancels the jobs in flight.
        return Parallel.ForEachAsync(queue.DequeueAllAsync(stoppingToken), parallelOptions, ProcessAsync);
    }

    private async ValueTask ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            await processor.ProcessAsync(jobId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Job {JobId} interrupted by shutdown; it remains unfinished", jobId);
            throw;
        }
        catch (Exception exception)
        {
            // One broken job must not stop the worker.
            logger.LogError(exception, "Job {JobId} could not be processed", jobId);
        }
    }
}
