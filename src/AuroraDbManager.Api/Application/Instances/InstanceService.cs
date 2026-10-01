using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Instances;

public sealed class InstanceService(
    AppDbContext db,
    IInstanceProvisioner provisioner,
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
        // If the process dies between the commit and this line the job stays pending until the
        // next start, when job recovery queues it. Not cancellable: the job is already committed.
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

    /// <summary>
    /// Deletes an instance together with its database server and data. An instance that is still
    /// provisioning is not deleted: its job may be creating resources at this very moment, and
    /// removing the metadata would leave them behind with nothing pointing to them.
    /// </summary>
    /// <exception cref="InstanceProvisioningException">
    /// The instance's resources could not be removed. Its metadata is kept so the delete can be retried.
    /// </exception>
    public async Task<DeleteInstanceResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (instance is null)
        {
            return DeleteInstanceResult.NotFound;
        }

        if (instance.Status == InstanceStatus.Provisioning)
        {
            return DeleteInstanceResult.Provisioning;
        }

        // Resources first: if this fails the metadata still exists and still points at them.
        await provisioner.DeprovisionAsync(instance, cancellationToken);

        await db.Instances.Where(i => i.Id == id).ExecuteDeleteAsync(cancellationToken);
        logger.LogInformation("Instance {InstanceId} deleted", id);
        return DeleteInstanceResult.Deleted;
    }
}

public enum DeleteInstanceResult
{
    Deleted,
    NotFound,

    /// <summary>Not deleted because the instance is still being provisioned.</summary>
    Provisioning
}
