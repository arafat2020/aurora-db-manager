using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Application.Jobs.ProvisionInstance;

/// <summary>
/// Provisions the job's instance through <see cref="IInstanceProvisioner"/> and reports the
/// result on the instance: <c>running</c> on success, <c>failed</c> once the job has failed for good.
/// </summary>
public sealed class ProvisionInstanceHandler(
    AppDbContext db,
    IInstanceProvisioner provisioner,
    TimeProvider timeProvider,
    ILogger<ProvisionInstanceHandler> logger) : IJobHandler
{
    public JobType Type => JobType.ProvisionInstance;

    public async Task ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.FirstOrDefaultAsync(i => i.Id == job.InstanceId, cancellationToken)
            ?? throw new JobExecutionException(JobErrorCodes.ProvisioningFailed, "The instance no longer exists.");

        if (instance.Status == InstanceStatus.Running)
        {
            logger.LogInformation(
                "Instance {InstanceId} is already running; job {JobId} has nothing to provision",
                instance.Id, job.Id);
            return;
        }

        if (instance.Status != InstanceStatus.Provisioning)
        {
            throw new JobExecutionException(
                JobErrorCodes.ProvisioningFailed,
                "The instance is not waiting to be provisioned.");
        }

        // Every log written while provisioning, including the provisioner's own, carries these.
        using var scope = logger.BeginScope(
            "Job {JobId} attempt {Attempt}: provisioning {Engine} {Version} instance {InstanceId}",
            job.Id, job.Attempt, instance.Engine, instance.Version, instance.Id);

        try
        {
            await provisioner.ProvisionAsync(instance, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InstanceProvisioningException exception)
        {
            // Its code and message are written for clients.
            throw new JobExecutionException(exception.Code, exception.Message, exception);
        }
        catch (Exception exception)
        {
            throw new JobExecutionException(JobErrorCodes.ProvisioningFailed, "Instance provisioning failed.", exception);
        }

        instance.MarkRunning(timeProvider.GetUtcNow().UtcDateTime);
    }

    public async Task OnFailedAsync(Job job, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.FirstOrDefaultAsync(i => i.Id == job.InstanceId, cancellationToken);

        if (instance?.Status == InstanceStatus.Provisioning)
        {
            instance.MarkFailed(
                job.ErrorCode ?? JobErrorCodes.ProvisioningFailed,
                job.ErrorMessage ?? "Instance provisioning failed.",
                timeProvider.GetUtcNow().UtcDateTime);
        }
    }
}
