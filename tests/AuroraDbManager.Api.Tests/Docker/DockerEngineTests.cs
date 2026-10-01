using AuroraDbManager.Api.Infrastructure.Docker;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Tests.Docker;

public sealed class DockerEngineTests
{
    [Fact]
    public async Task Operation_DaemonNotReachable_FailsAsUnavailable()
    {
        // A socket path nothing listens on; no Docker daemon is needed or contacted. Kept short:
        // Unix socket paths are limited to about 100 characters.
        var socket = $"/tmp/aurora-no-docker-{Guid.NewGuid():N}"[..36] + ".sock";
        using var engine = new DockerEngine(Options.Create(new DockerOptions { Endpoint = $"unix://{socket}" }));

        var exception = await Assert.ThrowsAsync<DockerEngineException>(
            () => engine.FindContainerAsync("aurora-instance-x", default));

        Assert.Equal(DockerFailure.Unavailable, exception.Kind);
    }

    [Fact]
    public void Construction_DoesNotConnect()
    {
        using var engine = new DockerEngine(Options.Create(new DockerOptions { Endpoint = "tcp://203.0.113.1:2375" }));
    }
}
