using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Monitoring;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Jobs;

/// <summary>
/// Reads job ids from the <see cref="JobQueue"/> and hands each to a <see cref="JobProcessor"/>
/// in its own DI scope, running at most <see cref="JobOptions.MaxConcurrency"/> jobs at once.
/// </summary>
/// <remarks>
/// Before it takes the first job it recovers the work a previous process left behind, in this
/// order: lost and interrupted jobs are queued again, then instances are reconciled with their
/// runtime. This runs in the background, so the API is already serving; jobs created meanwhile
/// wait in the queue. Afterwards it keeps looking for expired job leases, which covers a job whose
/// lease was still valid at startup.
/// </remarks>
public sealed class JobWorker(
    JobQueue queue,
    IServiceScopeFactory scopeFactory,
    InstanceReconciler reconciler,
    IOptions<JobOptions> options,
    TimeProvider timeProvider,
    ILogger<JobWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartupRecoveryRetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunStartupRecoveryAsync(stoppingToken);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.Value.MaxConcurrency,
            CancellationToken = stoppingToken
        };

        logger.LogInformation("Job worker started with concurrency {MaxConcurrency}", parallelOptions.MaxDegreeOfParallelism);

        // On shutdown the token stops the dequeue loop and cancels the jobs in flight.
        await Task.WhenAll(
            Parallel.ForEachAsync(queue.DequeueAllAsync(stoppingToken), parallelOptions, ProcessAsync),
            RecoverExpiredLeasesPeriodicallyAsync(stoppingToken));
    }

    /// <summary>Retried until it succeeds, so a database that is not up yet only delays the worker.</summary>
    private async Task RunStartupRecoveryAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    var queued = await scope.ServiceProvider.GetRequiredService<JobRecovery>()
                        .RecoverAsync(includePending: true, stoppingToken);
                    logger.LogInformation("Startup job recovery queued {JobCount} jobs", queued.Count);
                }

                await reconciler.ReconcileAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Startup recovery failed; retrying in {RetryDelaySeconds}s",
                    StartupRecoveryRetryDelay.TotalSeconds);
                await Task.Delay(StartupRecoveryRetryDelay, timeProvider, stoppingToken);
            }
        }
    }

    private async Task RecoverExpiredLeasesPeriodicallyAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.LeaseDurationSeconds), timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<JobRecovery>()
                    .RecoverAsync(includePending: false, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Recovery of expired job leases failed; it runs again at the next interval");
            }
        }
    }

    private async ValueTask ProcessAsync(QueuedJob queued, CancellationToken cancellationToken)
    {
        var jobId = queued.JobId;

        // Under the id of the request that created the job, so its logs join that request's.
        using var correlation = queued.CorrelationId is null
            ? null
            : logger.BeginScope("Request {" + RequestCorrelation.LogProperty + "}", queued.CorrelationId);

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            await processor.ProcessAsync(jobId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Job {JobId} interrupted by shutdown", jobId);
            throw;
        }
        catch (Exception exception)
        {
            // One broken job must not stop the worker.
            logger.LogError(exception, "Job {JobId} could not be processed", jobId);
        }
    }
}
