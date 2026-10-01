using Docker.DotNet;

namespace AuroraDbManager.Api.Infrastructure.Docker;

public static class DockerClientFactory
{
    /// <summary>
    /// Creates a client for the configured endpoint. Creating it does not connect, so this
    /// succeeds even when Docker is not running.
    /// </summary>
    public static DockerClient Create(DockerOptions options)
    {
        var endpoint = string.IsNullOrWhiteSpace(options.Endpoint)
            ? Environment.GetEnvironmentVariable("DOCKER_HOST")
            : options.Endpoint;

        using var configuration = string.IsNullOrWhiteSpace(endpoint)
            ? new DockerClientConfiguration()
            : new DockerClientConfiguration(new Uri(endpoint));

        return configuration.CreateClient();
    }
}
