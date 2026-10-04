using System.Net;
using System.Text.Json;
using AuroraDbManager.Api.Infrastructure.Docker;
using AuroraDbManager.Api.Tests.Docker;
using Microsoft.EntityFrameworkCore;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Monitoring;

/// <summary>
/// <c>GET /api/v1/instances/{id}/health</c> with the real Docker provisioner and the real runtime
/// probe over the in-memory Docker Engine: what is reported for a running, stopped, missing or
/// unreachable database server, and that reporting it changes nothing.
/// </summary>
public sealed class InstanceHealthTests : IDisposable
{
    private readonly ApiFactory _factory = new() { UseDockerProvisioner = true };
    private readonly HttpClient _client;

    public InstanceHealthTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private FakeDockerEngine Docker => _factory.Docker;

    private async Task<JsonElement> HealthAsync(Guid instanceId) =>
        await (await _client.GetAsync(InstanceHealthUrl(instanceId))).ReadJsonAsync(HttpStatusCode.OK);

    private static bool? Flag(JsonElement body, string section, string name)
    {
        var value = body.GetProperty(section).GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetBoolean();
    }

    private void SetContainerState(Guid instanceId, DockerContainerState state)
    {
        var name = DockerResourceNaming.ContainerName(instanceId);
        Docker.Containers[name] = Docker.Containers[name] with { State = state };
    }

    [Fact]
    public async Task ContainerRunning_DatabaseReachable_IsHealthy()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);

        var body = await HealthAsync(instanceId);

        Assert.Equal(instanceId, body.GetProperty("instanceId").GetGuid());
        Assert.Equal("healthy", body.Status());
        Assert.Equal("running", body.GetProperty("instanceStatus").GetString());
        Assert.True(Flag(body, "container", "exists"));
        Assert.True(Flag(body, "container", "running"));
        Assert.True(Flag(body, "database", "reachable"));
        Assert.False(body.TryGetProperty("reason", out _));
        Assert.Equal(JsonValueKind.String, body.GetProperty("checkedAt").ValueKind);
    }

    [Fact]
    public async Task Response_HasOnlyTheDocumentedFields_AndNoSecretsOrRuntimeDetails()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var password = await _factory.AdminPasswordAsync(instanceId);

        var response = await _client.GetAsync(InstanceHealthUrl(instanceId));

        var body = await response.ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(
            ["checkedAt", "container", "database", "instanceId", "instanceStatus", "status"],
            body.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(["exists", "running"], body.GetProperty("container").EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(["reachable"], body.GetProperty("database").EnumerateObject().Select(property => property.Name));

        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(password, text, StringComparison.Ordinal);
        Assert.DoesNotContain("POSTGRES_PASSWORD", text, StringComparison.Ordinal);
        Assert.DoesNotContain(DockerResourceNaming.ContainerName(instanceId), text, StringComparison.Ordinal);
        Assert.DoesNotContain("docker.sock", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContainerMissing_IsUnhealthy_WhileTheInstanceStaysRunningOnRecord()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        Docker.Containers.Remove(DockerResourceNaming.ContainerName(instanceId));

        var body = await HealthAsync(instanceId);

        Assert.Equal("unhealthy", body.Status());
        Assert.Equal("container_missing", body.GetProperty("reason").GetString());
        Assert.False(Flag(body, "container", "exists"));
        Assert.False(Flag(body, "container", "running"));
        Assert.False(Flag(body, "database", "reachable"));
        // Metadata and runtime disagree, and the response says both.
        Assert.Equal("running", body.GetProperty("instanceStatus").GetString());
    }

    [Theory]
    [InlineData(DockerContainerState.Exited)]
    [InlineData(DockerContainerState.Created)]
    [InlineData(DockerContainerState.Other)]
    public async Task ContainerNotRunning_IsUnhealthy(DockerContainerState state)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        SetContainerState(instanceId, state);

        var body = await HealthAsync(instanceId);

        Assert.Equal("unhealthy", body.Status());
        Assert.Equal("container_not_running", body.GetProperty("reason").GetString());
        Assert.True(Flag(body, "container", "exists"));
        Assert.False(Flag(body, "container", "running"));
        Assert.False(Flag(body, "database", "reachable"));
    }

    [Fact]
    public async Task ContainerRunning_DatabaseNotAcceptingConnections_IsDegraded()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        Docker.Exec = () => 1;

        var body = await HealthAsync(instanceId);

        Assert.Equal("degraded", body.Status());
        Assert.Equal("database_not_ready", body.GetProperty("reason").GetString());
        Assert.True(Flag(body, "container", "running"));
        Assert.False(Flag(body, "database", "reachable"));
    }

    [Fact]
    public async Task ContainerStopsBetweenTheInspectionAndTheDatabaseCheck_IsDegraded_NotAnError()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        Docker.FailOn(nameof(IDockerEngine.ExecAsync), DockerFailure.Conflict);

        var body = await HealthAsync(instanceId);

        Assert.Equal("degraded", body.Status());
        Assert.Equal("database_not_ready", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task DockerUnavailable_IsDegraded_WithNothingClaimedAboutTheInstance()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        Docker.Unavailable = true;

        var response = await _client.GetAsync(InstanceHealthUrl(instanceId));

        var body = await response.ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("degraded", body.Status());
        Assert.Equal("runtime_unavailable", body.GetProperty("reason").GetString());
        // Unknown, not false: the instance may well be serving.
        Assert.Null(Flag(body, "container", "exists"));
        Assert.Null(Flag(body, "container", "running"));
        Assert.Null(Flag(body, "database", "reachable"));

        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("docker.sock", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-daemon-detail", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainerWithTheInstancesNameButNotItsLabels_IsNotTheInstances()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var name = DockerResourceNaming.ContainerName(instanceId);
        Docker.Containers[name] = Docker.Containers[name] with { Labels = DockerResourceNaming.InstanceLabels(Guid.NewGuid()) };

        var body = await HealthAsync(instanceId);

        Assert.Equal("unhealthy", body.Status());
        Assert.Equal("container_missing", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InstanceStillProvisioning_ReportsWhatIsObserved_NextToItsStatusOnRecord()
    {
        var (instanceId, _) = await _client.CreateInstanceAsync();

        var body = await HealthAsync(instanceId);

        Assert.Equal("provisioning", body.GetProperty("instanceStatus").GetString());
        Assert.Equal("unhealthy", body.Status());
        Assert.Equal("container_missing", body.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("missing")]
    [InlineData("not-ready")]
    [InlineData("docker-down")]
    public async Task Health_OnlyObserves_ItNeverChangesTheInstanceOrItsRuntime(string condition)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var before = await _client.GetInstanceAsync(instanceId);
        switch (condition)
        {
            case "stopped":
                SetContainerState(instanceId, DockerContainerState.Exited);
                break;
            case "missing":
                Docker.Containers.Remove(DockerResourceNaming.ContainerName(instanceId));
                break;
            case "not-ready":
                Docker.Exec = () => 1;
                break;
            default:
                Docker.Unavailable = true;
                break;
        }

        Docker.Calls.Clear();
        var jobsBefore = await _factory.WithDbAsync(db => db.Jobs.CountAsync());

        for (var i = 0; i < 3; i++)
        {
            await HealthAsync(instanceId);
        }

        // The persisted instance is exactly as it was: status, error and the time it last changed.
        var after = await _client.GetInstanceAsync(instanceId);
        Assert.Equal("running", after.Status());
        Assert.Equal(before.GetRawText(), after.GetRawText());
        Assert.Equal(jobsBefore, await _factory.WithDbAsync(db => db.Jobs.CountAsync()));

        // And Docker was only asked: nothing started, created or removed.
        Assert.All(Docker.Calls, call => Assert.True(
            call.StartsWith(nameof(IDockerEngine.FindContainerAsync), StringComparison.Ordinal)
            || call.StartsWith(nameof(IDockerEngine.ExecAsync), StringComparison.Ordinal),
            $"Unexpected Docker call: {call}"));
    }

    [Fact]
    public async Task UnknownInstance_ReturnsNotFound()
    {
        var response = await _client.GetAsync(InstanceHealthUrl(Guid.NewGuid()));

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "INSTANCE_NOT_FOUND");
    }
}
