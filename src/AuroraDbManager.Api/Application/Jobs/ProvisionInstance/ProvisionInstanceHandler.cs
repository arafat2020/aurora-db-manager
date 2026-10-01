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

        try
        {
            await provisioner.ProvisionAsync(instance, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Only InstanceProvisioningException messages are written for clients.
            var message = exception is InstanceProvisioningException
                ? exception.Message
                : "Instance provisioning failed.";
            throw new JobExecutionException(JobErrorCodes.ProvisioningFailed, message, exception);
        }

        instance.MarkRunning(timeProvider.GetUtcNow().UtcDateTime);
    }

    public async Task OnFailedAsync(Job job, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.FirstOrDefaultAsync(i => i.Id == job.InstanceId, cancellationToken);

        if (instance?.Status == InstanceStatus.Provisioning)
        {
            instance.MarkFailed(timeProvider.GetUtcNow().UtcDateTime);
        }
    }
}
