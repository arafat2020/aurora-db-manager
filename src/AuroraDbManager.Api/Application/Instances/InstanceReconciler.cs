using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Instances;

/// <param name="Verified">Running instances whose database is up, possibly after being started again.</param>
/// <param name="Unverified">Running instances that could not be checked because the provisioner was unavailable.</param>
/// <param name="Failed">Running instances whose database could not be brought back; now <c>failed</c>.</param>
/// <param name="JobsCreated">Provisioning instances that had no unfinished job and were given one.</param>
/// <param name="Orphans">Managed resources that belong to no instance. Reported, never removed.</param>
public sealed record ReconciliationReport(
    int Verified,
    int Unverified,
    IReadOnlyList<Guid> Failed,
    IReadOnlyList<Guid> JobsCreated,
    IReadOnlyList<ProvisionedResource> Orphans);

/// <summary>
/// Brings instance metadata and the instances' real runtime back in line after a restart.
/// </summary>
/// <remarks>
/// Rules, by instance status:
/// <list type="bullet">
/// <item><b>provisioning</b>: left to its provisioning job. Only if it has no unfinished job is one
/// created; the provisioner then adopts whatever resources already exist.</item>
/// <item><b>running</b>: its database is checked and started if it is stopped. If it definitely
/// cannot be brought back the instance becomes <c>failed</c>. If the provisioner cannot be reached
/// nothing is known, and the instance is left as it is. A replacement is never created.</item>
/// <item><b>failed</b>, <b>stopped</b>: not touched.</item>
/// </list>
/// Resources that belong to no instance are logged as orphans and left alone.
/// </remarks>
public sealed class InstanceReconciler(
    IServiceScopeFactory scopeFactory,
    JobQueue jobQueue,
    IOptions<JobOptions> jobOptions,
    TimeProvider timeProvider,
    ILogger<InstanceReconciler> logger)
{
    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<ReconciliationReport> ReconcileAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Reconciling instance runtime");

        var jobsCreated = await EnsureProvisioningJobsAsync(cancellationToken);
        var (verified, unverified, failed) = await ReconcileRunningInstancesAsync(cancellationToken);
        var orphans = await FindOrphansAsync(cancellationToken);

        logger.LogInformation(
            "Reconciled instance runtime: {VerifiedCount} running, {UnverifiedCount} not verifiable, {FailedCount} failed, {JobsCreatedCount} provisioning jobs created, {OrphanCount} orphan resources",
            verified, unverified, failed.Count, jobsCreated.Count, orphans.Count);

        return new ReconciliationReport(verified, unverified, failed, jobsCreated, orphans);
    }

    /// <summary>Gives a job to provisioning instances that have none left to move them forward.</summary>
    private async Task<IReadOnlyList<Guid>> EnsureProvisioningJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stranded = await db.Instances
            .Where(i => i.Status == InstanceStatus.Provisioning)
            .Where(i => !db.Jobs.Any(j =>
                j.InstanceId == i.Id
                && j.Type == JobType.ProvisionInstance
                && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running)))
            .OrderBy(i => i.CreatedAt)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        var created = new List<Guid>();
        foreach (var instanceId in stranded)
        {
            var job = Job.Create(JobType.ProvisionInstance, instanceId, jobOptions.Value.MaxAttempts, UtcNow);
            db.Jobs.Add(job);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Another process gave it a job first (unique index), or the instance was deleted.
                db.Entry(job).State = EntityState.Detached;
                continue;
            }

            await jobQueue.EnqueueAsync(job.Id, cancellationToken);
            created.Add(instanceId);
            logger.LogWarning(
                "Provisioning instance {InstanceId} had no unfinished job; job {JobId} ({JobType}) created and queued",
                instanceId, job.Id, job.Type);
        }

        return created;
    }

    private async Task<(int Verified, int Unverified, IReadOnlyList<Guid> Failed)> ReconcileRunningInstancesAsync(
        CancellationToken cancellationToken)
    {
        List<Guid> runningIds;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            runningIds = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Instances
                .Where(i => i.Status == InstanceStatus.Running)
                .OrderBy(i => i.CreatedAt)
                .Select(i => i.Id)
                .ToListAsync(cancellationToken);
        }

        var verified = 0;
        var unverified = 0;
        var failed = new List<Guid>();

        // A stopped database takes a while to come up, so instances are handled side by side.
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = jobOptions.Value.MaxConcurrency,
            CancellationToken = cancellationToken
        };
        await Parallel.ForEachAsync(runningIds, parallelOptions, async (instanceId, token) =>
        {
            switch (await ReconcileRunningInstanceAsync(instanceId, token))
            {
                case true:
                    Interlocked.Increment(ref verified);
                    break;
                case false:
                    lock (failed)
                    {
                        failed.Add(instanceId);
                    }

                    break;
                default:
                    Interlocked.Increment(ref unverified);
                    break;
            }
        });

        return (verified, unverified, failed);
    }

    /// <returns>True if the database is up, false if the instance was marked failed, null if nothing could be established.</returns>
    private async Task<bool?> ReconcileRunningInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provisioner = scope.ServiceProvider.GetRequiredService<IInstanceProvisioner>();

        var instance = await db.Instances.FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is not { Status: InstanceStatus.Running })
        {
            return null;
        }

        using var logScope = logger.BeginScope(
            "Reconciling {Engine} {Version} instance {InstanceId}", instance.Engine, instance.Version, instance.Id);

        try
        {
            await provisioner.EnsureRunningAsync(instance, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InstanceProvisioningException exception) when (!exception.ProvisionerUnavailable)
        {
            logger.LogError(
                exception,
                "Instance {InstanceId} could not be brought back and is now failed: {ErrorCode}",
                instance.Id, exception.Code);

            try
            {
                instance.MarkFailed(exception.Code, exception.Message, UtcNow);
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Changed or deleted by someone else in the meantime; their change stands.
                return null;
            }

            return false;
        }
        catch (Exception exception)
        {
            // Not knowing is no reason to declare a database dead.
            logger.LogWarning(
                exception,
                "Instance {InstanceId} could not be verified and is left as it is: {ErrorCode}",
                instance.Id, (exception as InstanceProvisioningException)?.Code ?? "unexpected error");
            return null;
        }
    }

    private async Task<IReadOnlyList<ProvisionedResource>> FindOrphansAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        IReadOnlyList<ProvisionedResource> resources;
        try
        {
            resources = await scope.ServiceProvider.GetRequiredService<IInstanceProvisioner>()
                .ListResourcesAsync(cancellationToken);
        }
        catch (InstanceProvisioningException exception)
        {
            logger.LogWarning(exception, "Orphan resources could not be looked for: {ErrorCode}", exception.Code);
            return [];
        }

        // Read after the listing: an instance's row always exists before its resources do.
        var instanceIds = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Instances
            .Select(i => i.Id)
            .ToListAsync(cancellationToken)).ToHashSet();

        var orphans = resources
            .Where(resource => resource.InstanceId is not { } id || !instanceIds.Contains(id))
            .ToList();

        foreach (var orphan in orphans)
        {
            logger.LogWarning(
                "Orphan Docker resource detected: {ResourceKind} {ResourceName} (instance {InstanceId}) belongs to no instance; left untouched",
                orphan.Kind, orphan.Name, orphan.InstanceId);
        }

        return orphans;
    }
}
