namespace AuroraDbManager.Api.Infrastructure.Docker;

/// <summary>Error codes reported by Docker provisioning. They appear on failed jobs and in API errors.</summary>
public static class DockerProvisioningErrors
{
    public const string UnsupportedDatabaseVersion = "UNSUPPORTED_DATABASE_VERSION";
    public const string DockerUnavailable = "DOCKER_UNAVAILABLE";
    public const string DockerOperationFailed = "DOCKER_OPERATION_FAILED";
    public const string DockerNetworkCreateFailed = "DOCKER_NETWORK_CREATE_FAILED";
    public const string DockerVolumeCreateFailed = "DOCKER_VOLUME_CREATE_FAILED";
    public const string DockerImagePullFailed = "DOCKER_IMAGE_PULL_FAILED";
    public const string DockerContainerCreateFailed = "DOCKER_CONTAINER_CREATE_FAILED";
    public const string DockerContainerStartFailed = "DOCKER_CONTAINER_START_FAILED";
    public const string DockerResourceConflict = "DOCKER_RESOURCE_CONFLICT";
    public const string DockerResourceRemoveFailed = "DOCKER_RESOURCE_REMOVE_FAILED";
    public const string DatabaseContainerExited = "DATABASE_CONTAINER_EXITED";
    public const string DatabaseReadinessTimeout = "DATABASE_READINESS_TIMEOUT";
}
