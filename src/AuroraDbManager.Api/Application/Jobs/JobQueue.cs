using System.Threading.Channels;

namespace AuroraDbManager.Api.Application.Jobs;

/// <summary>
/// In-process queue of job ids waiting for the <see cref="JobWorker"/>. Only ids are queued;
/// the worker loads the job's current state from the database before processing it.
/// </summary>
/// <remarks>
/// The queue lives in memory. Ids that were queued but not yet processed are lost when the
/// process stops, and nothing re-queues the corresponding jobs on the next start.
/// </remarks>
public sealed class JobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    /// <summary>Queues a job. Call only after the job has been committed to the database.</summary>
    public ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(jobId, cancellationToken);

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
