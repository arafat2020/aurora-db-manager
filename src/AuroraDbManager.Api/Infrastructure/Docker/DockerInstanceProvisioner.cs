using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Docker;

/// <summary>
/// Runs an instance as a Docker container: one container and one data volume per instance, all
/// containers on one shared network.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency.</b> Resource names derive from the instance id, so a repeated call finds what
/// an earlier call left behind and adopts it instead of creating duplicates. A running container
/// is only checked for readiness, a stopped one is started, and an existing volume is reused.
/// A resource that has the expected name but is not labelled as this instance's, or does not
/// match its configuration, is never touched; provisioning fails with a conflict instead.
/// </para>
/// <para>
/// <b>Cleanup.</b> When an attempt fails, only what that same attempt created is removed: its
/// container, and its volume (which at that point holds nothing but the failed attempt's possibly
/// half-initialized data directory). Containers and volumes that existed before the attempt are
/// left exactly as they were, and the shared network is never removed. When an attempt is
/// cancelled nothing is removed, so the next attempt can adopt the resources.
/// </para>
/// </remarks>
public sealed class DockerInstanceProvisioner(
    IDockerEngine docker,
    DockerImageResolver images,
    IInstanceSecretStore secrets,
    IOptions<DockerOptions> options,
    TimeProvider timeProvider,
    ILogger<DockerInstanceProvisioner> logger) : IInstanceProvisioner
{
    public async Task ProvisionAsync(Instance instance, CancellationToken cancellationToken)
    {
        var containerName = DockerResourceNaming.ContainerName(instance.Id);
        var volumeName = DockerResourceNaming.VolumeName(instance.Id);
        var createdContainer = false;
        var createdVolume = false;

        logger.LogInformation(
            "Starting Docker provisioning of {Engine} {Version} instance {InstanceId}",
            instance.Engine, instance.Version, instance.Id);

        try
        {
            var image = images.Resolve(instance.Engine, instance.Version);
            logger.LogInformation("Resolved database image {Image} for instance {InstanceId}", image.Image, instance.Id);

            await EnsureNetworkAsync(instance, cancellationToken);

            var container = await DockerAsync(
                DockerProvisioningErrors.DockerOperationFailed,
                "Docker could not inspect the instance's container.",
                () => docker.FindContainerAsync(containerName, cancellationToken));

            if (container is null)
            {
                createdVolume = await EnsureVolumeAsync(instance, volumeName, cancellationToken);
                await EnsureImageAsync(instance, image, cancellationToken);

                var password = await secrets.GetOrCreateAdminPasswordAsync(instance.Id, cancellationToken);
                var spec = DockerContainerSpec.For(instance, image, password, options.Value.NetworkName);

                logger.LogInformation("Creating database container {ContainerName} for instance {InstanceId}", containerName, instance.Id);
                await DockerAsync(
                    DockerProvisioningErrors.DockerContainerCreateFailed,
                    "Docker could not create the database container.",
                    () => docker.CreateContainerAsync(spec, cancellationToken));
                createdContainer = true;

                await StartContainerAsync(instance, containerName, cancellationToken);
            }
            else
            {
                EnsureBelongsToInstance(container, instance, image, volumeName);

                switch (container.State)
                {
                    case DockerContainerState.Running:
                        logger.LogInformation(
                            "Database container {ContainerName} of instance {InstanceId} is already running",
                            containerName, instance.Id);
                        break;
                    case DockerContainerState.Created or DockerContainerState.Exited:
                        await StartContainerAsync(instance, containerName, cancellationToken);
                        break;
                    default:
                        throw new InstanceProvisioningException(
                            DockerProvisioningErrors.DockerResourceConflict,
                            $"Docker container '{containerName}' already exists in a state it cannot be started from.");
                }
            }

            await WaitUntilReadyAsync(instance, containerName, image, cancellationToken);

            logger.LogInformation("Docker provisioning of instance {InstanceId} completed", instance.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Docker provisioning of instance {InstanceId} failed with {ErrorCode}",
                instance.Id, (exception as InstanceProvisioningException)?.Code ?? "an unexpected error");

            await CleanUpFailedAttemptAsync(instance, containerName, createdContainer, volumeName, createdVolume, cancellationToken);
            throw;
        }
    }

    public async Task DeprovisionAsync(Instance instance, CancellationToken cancellationToken)
    {
        var containerName = DockerResourceNaming.ContainerName(instance.Id);
        var volumeName = DockerResourceNaming.VolumeName(instance.Id);
        const string removeFailed = "Docker could not remove the instance's resources.";

        var container = await DockerAsync(
            DockerProvisioningErrors.DockerResourceRemoveFailed, removeFailed,
            () => docker.FindContainerAsync(containerName, cancellationToken));
        if (container is not null && IsOwned(container.Labels, instance, containerName))
        {
            await DockerAsync(
                DockerProvisioningErrors.DockerResourceRemoveFailed, removeFailed,
                () => docker.RemoveContainerAsync(containerName, cancellationToken));
            logger.LogInformation("Removed database container {ContainerName} of instance {InstanceId}", containerName, instance.Id);
        }

        var volume = await DockerAsync(
            DockerProvisioningErrors.DockerResourceRemoveFailed, removeFailed,
            () => docker.FindVolumeAsync(volumeName, cancellationToken));
        if (volume is not null && IsOwned(volume.Labels, instance, volumeName))
        {
            await DockerAsync(
                DockerProvisioningErrors.DockerResourceRemoveFailed, removeFailed,
                () => docker.RemoveVolumeAsync(volumeName, cancellationToken));
            logger.LogInformation("Removed data volume {VolumeName} of instance {InstanceId}", volumeName, instance.Id);
        }
    }

    private async Task EnsureNetworkAsync(Instance instance, CancellationToken cancellationToken)
    {
        var networkName = options.Value.NetworkName;
        const string failed = "Docker could not provide the instance network.";

        var exists = await DockerAsync(
            DockerProvisioningErrors.DockerNetworkCreateFailed, failed,
            () => docker.NetworkExistsAsync(networkName, cancellationToken));

        if (!exists)
        {
            try
            {
                await docker.CreateNetworkAsync(networkName, DockerResourceNaming.NetworkLabels(), cancellationToken);
            }
            catch (DockerEngineException exception) when (exception.Kind == DockerFailure.Conflict)
            {
                // Another provisioning created the network in the meantime.
            }
            catch (DockerEngineException exception)
            {
                throw Translate(exception, DockerProvisioningErrors.DockerNetworkCreateFailed, failed);
            }
        }

        logger.LogInformation("Ensured Aurora network {NetworkName} for instance {InstanceId}", networkName, instance.Id);
    }

    /// <returns>Whether the volume was created by this call rather than found.</returns>
    private async Task<bool> EnsureVolumeAsync(Instance instance, string volumeName, CancellationToken cancellationToken)
    {
        const string failed = "Docker could not provide the instance's data volume.";

        var volume = await DockerAsync(
            DockerProvisioningErrors.DockerVolumeCreateFailed, failed,
            () => docker.FindVolumeAsync(volumeName, cancellationToken));

        if (volume is not null && !DockerResourceNaming.IsOwnedBy(volume.Labels, instance.Id))
        {
            throw new InstanceProvisioningException(
                DockerProvisioningErrors.DockerResourceConflict,
                $"Docker volume '{volumeName}' already exists and does not belong to this instance.");
        }

        if (volume is null)
        {
            await DockerAsync(
                DockerProvisioningErrors.DockerVolumeCreateFailed, failed,
                () => docker.CreateVolumeAsync(volumeName, DockerResourceNaming.InstanceLabels(instance.Id), cancellationToken));
        }

        logger.LogInformation(
            "Ensured instance volume {VolumeName} for instance {InstanceId} ({VolumeOrigin})",
            volumeName, instance.Id, volume is null ? "created" : "reused");
        return volume is null;
    }

    private async Task EnsureImageAsync(Instance instance, DatabaseImage image, CancellationToken cancellationToken)
    {
        const string failed = "Docker could not pull the database image.";

        var exists = await DockerAsync(
            DockerProvisioningErrors.DockerImagePullFailed, failed,
            () => docker.ImageExistsAsync(image.Image, cancellationToken));
        if (exists)
        {
            return;
        }

        logger.LogInformation("Pulling database image {Image} for instance {InstanceId}", image.Image, instance.Id);
        await DockerAsync(
            DockerProvisioningErrors.DockerImagePullFailed, failed,
            () => docker.PullImageAsync(image.Image, cancellationToken));
    }

    private async Task StartContainerAsync(Instance instance, string containerName, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting database container {ContainerName} for instance {InstanceId}", containerName, instance.Id);
        await DockerAsync(
            DockerProvisioningErrors.DockerContainerStartFailed,
            "Docker could not start the database container.",
            () => docker.StartContainerAsync(containerName, cancellationToken));
    }

    /// <summary>Rejects a container that has the instance's name but was not created for it as it is now.</summary>
    private static void EnsureBelongsToInstance(DockerContainer container, Instance instance, DatabaseImage image, string volumeName)
    {
        string? mismatch = null;
        if (!DockerResourceNaming.IsOwnedBy(container.Labels, instance.Id))
        {
            mismatch = "does not belong to this instance";
        }
        else if (container.Image != image.Image)
        {
            mismatch = "runs a different database image";
        }
        else if (!container.Mounts.Any(mount => mount.VolumeName == volumeName && mount.Target == image.DataPath))
        {
            mismatch = "does not use the instance's data volume";
        }

        if (mismatch is not null)
        {
            throw new InstanceProvisioningException(
                DockerProvisioningErrors.DockerResourceConflict,
                $"Docker container '{container.Name}' already exists and {mismatch}.");
        }
    }

    private async Task WaitUntilReadyAsync(Instance instance, string containerName, DatabaseImage image, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(options.Value.ReadinessTimeoutSeconds);
        var pollInterval = TimeSpan.FromMilliseconds(options.Value.ReadinessPollIntervalMilliseconds);
        var deadline = timeProvider.GetUtcNow() + timeout;

        logger.LogInformation(
            "Waiting for database readiness of instance {InstanceId} (timeout {ReadinessTimeoutSeconds}s)",
            instance.Id, timeout.TotalSeconds);

        while (true)
        {
            // A container that has stopped will never become ready; do not wait out the timeout.
            var container = await DockerAsync(
                DockerProvisioningErrors.DockerOperationFailed,
                "Docker could not inspect the database container.",
                () => docker.FindContainerAsync(containerName, cancellationToken));
            if (container?.State != DockerContainerState.Running)
            {
                throw new InstanceProvisioningException(
                    DockerProvisioningErrors.DatabaseContainerExited,
                    "The database container stopped before the database became ready.");
            }

            if (await IsReadyAsync(containerName, image, cancellationToken))
            {
                logger.LogInformation("Database of instance {InstanceId} became ready", instance.Id);
                return;
            }

            if (timeProvider.GetUtcNow() >= deadline)
            {
                throw new InstanceProvisioningException(
                    DockerProvisioningErrors.DatabaseReadinessTimeout,
                    $"The database did not become ready within {timeout.TotalSeconds:0} seconds.");
            }

            await Task.Delay(pollInterval, timeProvider, cancellationToken);
        }
    }

    private async Task<bool> IsReadyAsync(string containerName, DatabaseImage image, CancellationToken cancellationToken)
    {
        try
        {
            return await docker.ExecAsync(containerName, image.ReadinessCommand, cancellationToken) == 0;
        }
        catch (DockerEngineException exception) when (exception.Kind is DockerFailure.Conflict or DockerFailure.NotFound)
        {
            // The container stopped between the state check and the command; the next check reports it.
            return false;
        }
        catch (DockerEngineException exception)
        {
            throw Translate(exception, DockerProvisioningErrors.DockerOperationFailed, "Docker could not check whether the database is ready.");
        }
    }

    private async Task CleanUpFailedAttemptAsync(
        Instance instance,
        string containerName,
        bool createdContainer,
        string volumeName,
        bool createdVolume,
        CancellationToken cancellationToken)
    {
        try
        {
            if (createdContainer)
            {
                await docker.RemoveContainerAsync(containerName, cancellationToken);
                logger.LogInformation(
                    "Removed database container {ContainerName} created by the failed attempt for instance {InstanceId}",
                    containerName, instance.Id);
            }

            if (createdVolume)
            {
                await docker.RemoveVolumeAsync(volumeName, cancellationToken);
                logger.LogInformation(
                    "Removed data volume {VolumeName} created by the failed attempt for instance {InstanceId}",
                    volumeName, instance.Id);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best effort: what is left behind is labelled and will be adopted by the next attempt.
            logger.LogWarning(
                exception,
                "Could not clean up Docker resources of the failed attempt for instance {InstanceId}",
                instance.Id);
        }
    }

    private bool IsOwned(IReadOnlyDictionary<string, string> labels, Instance instance, string resourceName)
    {
        if (DockerResourceNaming.IsOwnedBy(labels, instance.Id))
        {
            return true;
        }

        logger.LogWarning(
            "Docker resource {ResourceName} does not belong to instance {InstanceId} and was left untouched",
            resourceName, instance.Id);
        return false;
    }

    /// <summary>Runs a Docker operation, reporting its failure under the given client-safe code and message.</summary>
    private static async Task<T> DockerAsync<T>(string failureCode, string failureMessage, Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (DockerEngineException exception)
        {
            throw Translate(exception, failureCode, failureMessage);
        }
    }

    private static Task DockerAsync(string failureCode, string failureMessage, Func<Task> operation) =>
        DockerAsync(failureCode, failureMessage, async () =>
        {
            await operation();
            return true;
        });

    // The Docker exception is kept as the inner exception for logs; clients only see the code and message.
    private static InstanceProvisioningException Translate(DockerEngineException exception, string failureCode, string failureMessage) =>
        exception.Kind == DockerFailure.Unavailable
            ? new InstanceProvisioningException(DockerProvisioningErrors.DockerUnavailable, "Docker is not available.", exception)
            : new InstanceProvisioningException(failureCode, failureMessage, exception);
}
