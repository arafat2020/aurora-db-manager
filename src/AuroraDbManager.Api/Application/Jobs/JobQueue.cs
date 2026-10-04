using System.Threading.Channels;
using AuroraDbManager.Api.Application.Monitoring;

namespace AuroraDbManager.Api.Application.Jobs;

/// <summary>
/// In-process queue of job ids waiting for the <see cref="JobWorker"/>. Only ids are queued;
/// the worker loads the job's current state from the database before processing it.
/// </summary>
/// <remarks>
/// The queue lives in memory. Ids that were queued but not yet processed are lost when the
/// process stops; <see cref="JobRecovery"/> queues the corresponding jobs again on the next start.
/// With each id goes the id of the API request it was queued for, if any, so the worker's logs
/// can be traced back to that request. It is for logs only and is lost with the queue: a job
/// that is recovered, or created by the scheduler, has none.
/// </remarks>
public sealed class JobQueue
{
    private readonly Channel<QueuedJob> _channel = Channel.CreateUnbounded<QueuedJob>();

    /// <summary>Queues a job. Call only after the job has been committed to the database.</summary>
    public ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(new QueuedJob(jobId, RequestCorrelation.Current), cancellationToken);

    public IAsyncEnumerable<QueuedJob> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <param name="JobId">The job to process.</param>
/// <param name="CorrelationId">The id of the API request that created the job; null if no request did.</param>
public readonly record struct QueuedJob(Guid JobId, string? CorrelationId);
