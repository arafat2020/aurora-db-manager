using AuroraDbManager.Api.Application.Connectivity;
using System.Net;
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
/// <para>
/// <b>Published ports.</b> A container publishes what its instance's record says and nothing
/// else: the engine's port on the recorded host port, bound to the server's configured address,
/// or no port at all. Docker cannot change that on an existing container, so it is changed by
/// replacing the container: the old one is stopped and set aside under another name, a new one
/// is created on the same data volume, started and checked, and only then is the old one
/// removed. If the new one cannot be brought up, it is removed and the old one is put back and
/// started. The data volume is never removed, replaced or recreated by any of this.
/// </para>
/// </remarks>
public sealed class DockerInstanceProvisioner(
    IDockerEngine docker,
    DockerImageResolver images,
    IInstanceSecretStore secrets,
    IOptions<DockerOptions> options,
    IOptions<ExternalAccessOptions> externalAccess,
    TimeProvider timeProvider,
    ILogger<DockerInstanceProvisioner> logger) : IInstanceProvisioner
{
    private const string PortConfigurationFailed = "Docker could not apply the instance's port configuration.";

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
                var spec = SpecFor(instance, image, password);

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
                // An attempt adopts what an earlier attempt of the same job created, and that
                // publishes what the record says. Anything else is not adopted, and not changed.
                if (!PublishesExactly(container, DesiredBinding(instance, image)))
                {
                    throw new InstanceProvisioningException(
                        DockerProvisioningErrors.DockerResourceConflict,
                        $"Docker container '{containerName}' already exists and does not publish the ports the instance's record says.");
                }

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

    /// <summary>
    /// Recovery of an instance that was provisioned before: its container is expected to exist.
    /// A stopped container (Docker or the host was restarted) is started and checked for
    /// readiness. Nothing is created, replaced or removed here, whatever happens.
    /// </summary>
    public async Task EnsureRunningAsync(Instance instance, CancellationToken cancellationToken)
    {
        var containerName = DockerResourceNaming.ContainerName(instance.Id);
        var volumeName = DockerResourceNaming.VolumeName(instance.Id);

        await RecoverInterruptedReplacementAsync(instance, cancellationToken);

        var container = await DockerAsync(
            DockerProvisioningErrors.DockerOperationFailed,
            "Docker could not inspect the instance's container.",
            () => docker.FindContainerAsync(containerName, cancellationToken));

        if (container is null)
        {
            throw new InstanceProvisioningException(
                DockerProvisioningErrors.DatabaseContainerMissing,
                "The instance's database container no longer exists.");
        }

        if (!DockerResourceNaming.IsOwnedBy(container.Labels, instance.Id))
        {
            throw new InstanceProvisioningException(
                DockerProvisioningErrors.DockerResourceConflict,
                $"Docker container '{containerName}' does not belong to this instance.");
        }

        // What the container publishes is the record's to say. A container that says otherwise, after
        // a change that was interrupted or a bind address that was reconfigured, is not left as it
        // is: neither exposed where the record says it is private, nor private where it says exposed.
        var publishesAsRecorded = PublishesExactly(container, DesiredBinding(instance));

        if (container.State == DockerContainerState.Running && publishesAsRecorded)
        {
            logger.LogInformation(
                "Database container {ContainerName} of instance {InstanceId} is running",
                containerName, instance.Id);
            return;
        }

        if (container.State is not (DockerContainerState.Running or DockerContainerState.Created or DockerContainerState.Exited))
        {
            throw new InstanceProvisioningException(
                DockerProvisioningErrors.DockerResourceConflict,
                $"Docker container '{containerName}' is in a state it cannot be started from.");
        }

        var image = images.Resolve(instance.Engine, instance.Version);
        EnsureBelongsToInstance(container, instance, image, volumeName);

        if (!publishesAsRecorded)
        {
            logger.LogWarning(
                "Database container {ContainerName} of instance {InstanceId} does not publish what the instance's record says; replacing it",
                containerName, instance.Id);
            await ReplaceContainerAsync(instance, image, cancellationToken);
            return;
        }

        logger.LogWarning(
            "Restarting stopped database container {ContainerName} of instance {InstanceId}",
            containerName, instance.Id);
        await StartContainerAsync(instance, containerName, cancellationToken);
        await WaitUntilReadyAsync(instance, containerName, image, cancellationToken);
    }

    public async Task ApplyExternalAccessAsync(Instance instance, CancellationToken cancellationToken)
    {
        var containerName = DockerResourceNaming.ContainerName(instance.Id);
        var image = images.Resolve(instance.Engine, instance.Version);

        await RecoverInterruptedReplacementAsync(instance, cancellationToken);

        var container = await DockerAsync(
            DockerProvisioningErrors.DockerPortConfigurationFailed, PortConfigurationFailed,
            () => docker.FindContainerAsync(containerName, cancellationToken));
        if (container is null)
        {
            throw new InstanceProvisioningException(
                DockerProvisioningErrors.DatabaseContainerMissing,
                "The instance's database container no longer exists.");
        }

        // Only ever the instance's own container, on the instance's own volume.
        EnsureBelongsToInstance(container, instance, image, DockerResourceNaming.VolumeName(instance.Id));

        if (!PublishesExactly(container, DesiredBinding(instance, image)))
        {
            await ReplaceContainerAsync(instance, image, cancellationToken);
            return;
        }

        // Nothing to change; what is left is that the server is up.
        if (container.State != DockerContainerState.Running)
        {
            await StartContainerAsync(instance, containerName, cancellationToken);
        }

        await WaitUntilReadyAsync(instance, containerName, image, cancellationToken);
    }

    public async Task<IReadOnlyList<ProvisionedResource>> ListResourcesAsync(CancellationToken cancellationToken)
    {
        const string failed = "Docker could not list the managed resources.";

        var containers = await DockerAsync(
            DockerProvisioningErrors.DockerOperationFailed, failed,
            () => docker.ListContainersAsync(DockerResourceNaming.ManagedLabel, "true", cancellationToken));
        var volumes = await DockerAsync(
            DockerProvisioningErrors.DockerOperationFailed, failed,
            () => docker.ListVolumesAsync(DockerResourceNaming.ManagedLabel, "true", cancellationToken));

        // Only resources with an instance label are instance resources; the shared network has none.
        return containers.Select(container => (Kind: "container", Resource: container))
            .Concat(volumes.Select(volume => (Kind: "volume", Resource: volume)))
            .Where(entry => entry.Resource.Labels.ContainsKey(DockerResourceNaming.InstanceIdLabel))
            .Select(entry => new ProvisionedResource(
                entry.Kind, entry.Resource.Name, DockerResourceNaming.OwnerOf(entry.Resource.Labels)))
            .ToList();
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

        // What an interrupted replacement may have left: the instance's former container, which holds the volume too.
        var replacedName = DockerResourceNaming.ReplacedContainerName(instance.Id);
        var replaced = await DockerAsync(
            DockerProvisioningErrors.DockerResourceRemoveFailed, removeFailed,
            () => docker.FindContainerAsync(replacedName, cancellationToken));
        if (replaced is not null && IsOwned(replaced.Labels, instance, replacedName))
        {
            await DockerAsync(
                DockerProvisioningErrors.DockerResourceRemoveFailed, removeFailed,
                () => docker.RemoveContainerAsync(replacedName, cancellationToken));
            logger.LogInformation("Removed database container {ContainerName} of instance {InstanceId}", replacedName, instance.Id);
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

    private DockerContainerSpec SpecFor(Instance instance, DatabaseImage image, string password) =>
        DockerContainerSpec.For(instance, image, password, options.Value.NetworkName, externalAccess.Value.NormalizedBindAddress);

    private DockerPortBinding? DesiredBinding(Instance instance, DatabaseImage image) =>
        DockerContainerSpec.PortBindingFor(instance, image.Port, externalAccess.Value.NormalizedBindAddress);

    // Without an image at hand: the engine's port is the same for every version of it.
    private DockerPortBinding? DesiredBinding(Instance instance) =>
        DockerContainerSpec.PortBindingFor(instance, EngineDefaults.Port(instance.Engine), externalAccess.Value.NormalizedBindAddress);

    /// <summary>Whether the container publishes exactly <paramref name="desired"/>: that one port, there, and no other; or none at all.</summary>
    private static bool PublishesExactly(DockerContainer container, DockerPortBinding? desired)
    {
        var actual = container.PortBindings ?? [];
        if (desired is null)
        {
            return actual.Count == 0;
        }

        return actual.Count == 1
            && actual[0].ContainerPort == desired.ContainerPort
            && actual[0].HostPort == desired.HostPort
            && IPAddress.TryParse(actual[0].HostAddress, out var actualAddress)
            && actualAddress.Equals(IPAddress.Parse(desired.HostAddress));
    }

    /// <summary>
    /// Replaces the instance's container by one that publishes what the instance's record says,
    /// on the same data volume. See the remarks of this class. Returns once the new container's
    /// database accepts connections and the container has been checked; throws, with the former
    /// container put back and started, if that could not be achieved.
    /// </summary>
    private async Task ReplaceContainerAsync(Instance instance, DatabaseImage image, CancellationToken cancellationToken)
    {
        var containerName = DockerResourceNaming.ContainerName(instance.Id);
        var replacedName = DockerResourceNaming.ReplacedContainerName(instance.Id);
        var volumeName = DockerResourceNaming.VolumeName(instance.Id);
        var desired = DesiredBinding(instance, image);

        logger.LogWarning(
            "Replacing database container {ContainerName} of instance {InstanceId} to publish {PublishedPort}; the database is interrupted until the new container is ready",
            containerName, instance.Id, desired is null ? "no port" : $"host port {desired.HostPort}");

        // The password the data directory was initialized with. The image reads it only when it
        // initializes an empty data directory, which this one is not.
        var password = await secrets.GetOrCreateAdminPasswordAsync(instance.Id, cancellationToken);
        var spec = SpecFor(instance, image, password);

        var setAside = false;
        var created = false;
        try
        {
            // Stopped in order, so the data directory is closed cleanly before another server opens it.
            await DockerAsync(
                DockerProvisioningErrors.DockerPortConfigurationFailed, PortConfigurationFailed,
                () => docker.StopContainerAsync(containerName, cancellationToken));
            await DockerAsync(
                DockerProvisioningErrors.DockerPortConfigurationFailed, PortConfigurationFailed,
                () => docker.RenameContainerAsync(containerName, replacedName, cancellationToken));
            setAside = true;

            await DockerAsync(
                DockerProvisioningErrors.DockerPortConfigurationFailed, PortConfigurationFailed,
                () => docker.CreateContainerAsync(spec, cancellationToken));
            created = true;

            await DockerAsync(
                DockerProvisioningErrors.DockerPortConfigurationFailed, PortConfigurationFailed,
                () => docker.StartContainerAsync(containerName, cancellationToken));
            await WaitUntilReadyAsync(instance, containerName, image, cancellationToken);

            // Not taken on trust: what Docker now has is looked at before the old container goes.
            var replacement = await DockerAsync(
                DockerProvisioningErrors.DockerPortConfigurationFailed, PortConfigurationFailed,
                () => docker.FindContainerAsync(containerName, cancellationToken));
            if (replacement is null)
            {
                throw new InstanceProvisioningException(DockerProvisioningErrors.DockerPortConfigurationFailed, PortConfigurationFailed);
            }

            EnsureBelongsToInstance(replacement, instance, image, volumeName);
            if (!PublishesExactly(replacement, desired))
            {
                throw new InstanceProvisioningException(DockerProvisioningErrors.DockerPortConfigurationFailed, PortConfigurationFailed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Left as it is: the next start finds what was set aside and puts it back.
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Database container {ContainerName} of instance {InstanceId} could not be replaced ({ErrorCode}); putting the former container back",
                containerName, instance.Id, (exception as InstanceProvisioningException)?.Code ?? "an unexpected error");

            var restored = await PutFormerContainerBackAsync(instance, image, setAside, created);

            // A taken port and an unreachable Docker are what they are; everything else is this.
            if (exception is InstanceProvisioningException { Code: DockerProvisioningErrors.PortAlreadyInUse or DockerProvisioningErrors.DockerUnavailable })
            {
                throw;
            }

            throw new InstanceProvisioningException(
                DockerProvisioningErrors.DockerPortConfigurationFailed,
                restored
                    ? "The database server could not be restarted with the new port configuration. It is running as it was before."
                    : "The database server could not be restarted with the new port configuration, and could not be brought back as it was either. Check the instance's health.",
                exception);
        }

        try
        {
            await docker.RemoveContainerAsync(replacedName, CancellationToken.None);
        }
        catch (DockerEngineException exception)
        {
            // Stopped, and no longer the instance's container; removed when the instance is next looked after.
            logger.LogWarning(
                exception,
                "The former database container {ContainerName} of instance {InstanceId} could not be removed",
                replacedName, instance.Id);
        }

        logger.LogInformation(
            "Database container {ContainerName} of instance {InstanceId} replaced; it publishes {PublishedPort}",
            containerName, instance.Id, desired is null ? "no port" : $"host port {desired.HostPort}");
    }

    /// <summary>Undoes a replacement that did not work out. Not cancellable: a half-undone replacement helps nobody.</summary>
    /// <returns>Whether the former container is running and its database accepts connections again.</returns>
    private async Task<bool> PutFormerContainerBackAsync(Instance instance, DatabaseImage image, bool setAside, bool created)
    {
        var containerName = DockerResourceNaming.ContainerName(instance.Id);
        try
        {
            if (created)
            {
                // The container only: the volume is a named one, and those are never removed with a container.
                await docker.RemoveContainerAsync(containerName, CancellationToken.None);
            }

            if (setAside)
            {
                await docker.RenameContainerAsync(DockerResourceNaming.ReplacedContainerName(instance.Id), containerName, CancellationToken.None);
            }

            await docker.StartContainerAsync(containerName, CancellationToken.None);
            await WaitUntilReadyAsync(instance, containerName, image, CancellationToken.None);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "The former database container {ContainerName} of instance {InstanceId} could not be put back",
                containerName, instance.Id);
            return false;
        }
    }

    /// <summary>
    /// Finishes or undoes a replacement that was interrupted, by a crash or a cancellation: if the
    /// instance's former container is still set aside, either the new one is complete and the
    /// former one is removed, or the former one is put back under the instance's name.
    /// </summary>
    private async Task RecoverInterruptedReplacementAsync(Instance instance, CancellationToken cancellationToken)
    {
        var containerName = DockerResourceNaming.ContainerName(instance.Id);
        var replacedName = DockerResourceNaming.ReplacedContainerName(instance.Id);
        const string failed = "Docker could not inspect the instance's container.";

        var replaced = await DockerAsync(
            DockerProvisioningErrors.DockerOperationFailed, failed,
            () => docker.FindContainerAsync(replacedName, cancellationToken));
        if (replaced is null || !IsOwned(replaced.Labels, instance, replacedName))
        {
            return;
        }

        var current = await DockerAsync(
            DockerProvisioningErrors.DockerOperationFailed, failed,
            () => docker.FindContainerAsync(containerName, cancellationToken));

        if (current is not null && !DockerResourceNaming.IsOwnedBy(current.Labels, instance.Id))
        {
            throw new InstanceProvisioningException(
                DockerProvisioningErrors.DockerResourceConflict,
                $"Docker container '{containerName}' does not belong to this instance.");
        }

        if (current is not null && PublishesExactly(current, DesiredBinding(instance)))
        {
            // The replacement was complete but for removing what it replaced.
            await DockerAsync(
                DockerProvisioningErrors.DockerOperationFailed, failed,
                () => docker.RemoveContainerAsync(replacedName, cancellationToken));
            logger.LogWarning(
                "Removed the former database container {ContainerName} that an interrupted replacement left for instance {InstanceId}",
                replacedName, instance.Id);
            return;
        }

        if (current is not null)
        {
            await DockerAsync(
                DockerProvisioningErrors.DockerOperationFailed, failed,
                () => docker.RemoveContainerAsync(containerName, cancellationToken));
        }

        await DockerAsync(
            DockerProvisioningErrors.DockerOperationFailed, failed,
            () => docker.RenameContainerAsync(replacedName, containerName, cancellationToken));
        logger.LogWarning(
            "Put back the database container {ContainerName} of instance {InstanceId} that an interrupted replacement had set aside",
            containerName, instance.Id);
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
        exception.Kind switch
        {
            DockerFailure.Unavailable =>
                new InstanceProvisioningException(DockerProvisioningErrors.DockerUnavailable, "Docker is not available.", exception)
                {
                    ProvisionerUnavailable = true
                },
            DockerFailure.PortUnavailable =>
                new InstanceProvisioningException(DockerProvisioningErrors.PortAlreadyInUse, "The host port is already in use.", exception),
            _ => new InstanceProvisioningException(failureCode, failureMessage, exception)
        };
}
