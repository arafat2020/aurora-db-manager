using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Instances;

public sealed class InstanceService(
    AppDbContext db,
    JobQueue jobQueue,
    IOptions<JobOptions> jobOptions,
    TimeProvider timeProvider,
    ILogger<InstanceService> logger)
{
    public async Task<CreateInstanceResponse> CreateAsync(CreateInstanceRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var instance = Instance.Create(
            request.Name!.Trim(),
            Enum.Parse<InstanceEngine>(request.Engine!, ignoreCase: true),
            request.Version!.Trim(),
            request.Cpu!.Value,
            request.MemoryMb!.Value,
            request.StorageGb!.Value,
            now);
        var job = Job.Create(JobType.ProvisionInstance, instance.Id, jobOptions.Value.MaxAttempts, now);

        // One SaveChanges is one transaction: the instance and its job are stored together or not at all.
        db.Instances.Add(instance);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        // Queued only after the commit, so the worker never sees a job that is not in the database.
        // If the process dies between the commit and this line the job stays pending and is never
        // picked up; nothing re-queues it yet. Not cancellable: the job is already committed.
        await jobQueue.EnqueueAsync(job.Id, CancellationToken.None);
        logger.LogInformation(
            "Job {JobId} ({JobType}) for instance {InstanceId} queued",
            job.Id, job.Type, job.InstanceId);

        return new CreateInstanceResponse(InstanceResponse.From(instance), JobResponse.From(job));
    }

    public async Task<InstanceResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

        return instance is null ? null : InstanceResponse.From(instance);
    }

    public async Task<InstanceListResponse> ListAsync(ListInstancesQuery query, CancellationToken cancellationToken)
    {
        var totalCount = await db.Instances.CountAsync(cancellationToken);

        var instances = await db.Instances.AsNoTracking()
            .OrderByDescending(i => i.CreatedAt)
            .ThenByDescending(i => i.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new InstanceListResponse(
            instances.Select(InstanceResponse.From).ToList(),
            query.Page,
            query.PageSize,
            totalCount);
    }

    /// <returns><c>false</c> when no instance with the given id exists.</returns>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await db.Instances.Where(i => i.Id == id).ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }
}
