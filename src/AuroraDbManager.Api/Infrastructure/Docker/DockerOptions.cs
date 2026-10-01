using System.ComponentModel.DataAnnotations;

namespace AuroraDbManager.Api.Infrastructure.Docker;

/// <summary>Settings for Docker provisioning, bound from the <c>Docker</c> configuration section.</summary>
public sealed class DockerOptions
{
    public const string SectionName = "Docker";

    /// <summary>
    /// Docker Engine endpoint, e.g. <c>unix:///var/run/docker.sock</c> or <c>tcp://host:2375</c>.
    /// When empty, the <c>DOCKER_HOST</c> environment variable is used, then the platform default.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>The one Docker network every instance container is attached to.</summary>
    [Required]
    public string NetworkName { get; set; } = "aurora-db";

    /// <summary>How long a started container gets to accept database connections.</summary>
    [Range(1, 3600)]
    public int ReadinessTimeoutSeconds { get; set; } = 120;

    /// <summary>Wait between two readiness checks.</summary>
    [Range(50, 60_000)]
    public int ReadinessPollIntervalMilliseconds { get; set; } = 1000;
}
