using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Infrastructure.Docker;

/// <summary>
/// <see cref="IInstanceRuntimeProbe"/> over Docker: one inspection of the instance's container
/// and, if it is running, the same readiness command provisioning waits for, run once.
/// </summary>
public sealed class DockerInstanceRuntimeProbe(
    IDockerEngine docker,
    DockerImageResolver images,
    ILogger<DockerInstanceRuntimeProbe> logger) : IInstanceRuntimeProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<InstanceRuntimeState> InspectAsync(Instance instance, CancellationToken cancellationToken)
    {
        var containerName = DockerResourceNaming.ContainerName(instance.Id);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        DockerContainer? container;
        try
        {
            container = await docker.FindContainerAsync(containerName, timeout.Token);
        }
        catch (Exception exception) when (IsFailure(exception, cancellationToken))
        {
            logger.LogWarning(exception, "The container of instance {InstanceId} could not be inspected", instance.Id);
            return InstanceRuntimeState.RuntimeUnavailable;
        }

        // A container that merely has the instance's name is not the instance's database server.
        if (container is null || !DockerResourceNaming.IsOwnedBy(container.Labels, instance.Id))
        {
            return InstanceRuntimeState.ContainerMissing;
        }

        if (container.State != DockerContainerState.Running)
        {
            return InstanceRuntimeState.ContainerNotRunning;
        }

        try
        {
            var image = images.Resolve(instance.Engine, instance.Version);
            return await docker.ExecAsync(containerName, image.ReadinessCommand, timeout.Token) == 0
                ? InstanceRuntimeState.Ready
                : InstanceRuntimeState.DatabaseNotReady;
        }
        catch (DockerEngineException exception) when (exception.Kind == DockerFailure.Unavailable)
        {
            logger.LogWarning(exception, "The database of instance {InstanceId} could not be checked", instance.Id);
            return InstanceRuntimeState.RuntimeUnavailable;
        }
        catch (Exception exception) when (IsFailure(exception, cancellationToken))
        {
            // The container stopped in between, the command could not be run, or it did not return in time.
            logger.LogWarning(exception, "The database of instance {InstanceId} could not be checked", instance.Id);
            return InstanceRuntimeState.DatabaseNotReady;
        }
    }

    // Everything but the caller's own cancellation; this probe's timeout is a failure like any other.
    private static bool IsFailure(Exception exception, CancellationToken cancellationToken) =>
        !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);
}
