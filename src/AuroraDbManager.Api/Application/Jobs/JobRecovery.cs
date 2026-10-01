using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Application.Jobs;

/// <summary>
/// Puts jobs that no execution is working on back into the queue. The queue is in memory and is
/// lost with the process; the database is where lost work is found again.
/// </summary>
public sealed class JobRecovery(
    AppDbContext db,
    JobQueue queue,
    TimeProvider timeProvider,
    ILogger<JobRecovery> logger)
{
    /// <summary>
    /// Returns running jobs whose lease has expired to <c>pending</c> and queues them. With
    /// <paramref name="includePending"/>, also queues every other pending job; that is for
    /// startup, when the queue that held them is gone. A running job with a live lease is never touched.
    /// </summary>
    /// <returns>The ids of the queued jobs.</returns>
    public async Task<IReadOnlyList<Guid>> RecoverAsync(bool includePending, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // A running job without a lease predates leases and has no owner either.
        var expired = await db.Jobs
            .Where(j => j.Status == JobStatus.Running && (j.LeaseExpiresAt == null || j.LeaseExpiresAt <= now))
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(cancellationToken);

        var recovered = new List<Guid>();
        foreach (var job in expired)
        {
            var leaseId = job.LeaseId;
            var interruptedAttempt = job.Attempt;
            try
            {
                job.ReturnToPending(now);
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another process recovered it first, or its owner finished or renewed after all.
                db.Entry(job).State = EntityState.Detached;
                continue;
            }

            recovered.Add(job.Id);
            logger.LogWarning(
                "Recovering expired job lease {LeaseId}: job {JobId} ({JobType}) for instance {InstanceId} was interrupted in attempt {Attempt} and is pending again",
                leaseId, job.Id, job.Type, job.InstanceId, interruptedAttempt);
        }

        var toQueue = includePending
            ? await db.Jobs.Where(j => j.Status == JobStatus.Pending).OrderBy(j => j.CreatedAt).Select(j => j.Id).ToListAsync(cancellationToken)
            : recovered;

        foreach (var jobId in toQueue)
        {
            // Queuing a job that is already queued is harmless: only one execution can claim it.
            await queue.EnqueueAsync(jobId, cancellationToken);
            logger.LogInformation("Recovering pending job {JobId}: queued", jobId);
        }

        return toQueue;
    }

    /// <summary>
    /// Returns a job that its own execution had to abandon to <c>pending</c>, provided the
    /// execution still holds the lease.
    /// </summary>
    public async Task ReleaseAsync(Guid jobId, Guid leaseId)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId);
        if (job is null || job.Status != JobStatus.Running || job.LeaseId != leaseId)
        {
            return;
        }

        var interruptedAttempt = job.Attempt;
        try
        {
            job.ReturnToPending(timeProvider.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return;
        }

        logger.LogInformation(
            "Job {JobId} ({JobType}) for instance {InstanceId} interrupted in attempt {Attempt}: job lease {LeaseId} released, job is pending again",
            job.Id, job.Type, job.InstanceId, interruptedAttempt, leaseId);
    }
}
