using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Docker;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Tests.Docker;

public sealed class DockerInstanceEndpointResolverTests
{
    private const string Network = "aurora-db";

    private readonly FakeDockerEngine _docker = new();
    private readonly DockerInstanceEndpointResolver _resolver;

    public DockerInstanceEndpointResolverTests()
    {
        _resolver = new DockerInstanceEndpointResolver(_docker, Options.Create(new DockerOptions { NetworkName = Network }));
    }

    private static Instance NewInstance(InstanceEngine engine = InstanceEngine.Postgres) =>
        Instance.Create("db", engine, "16", 1, 1024, 20, DateTime.UtcNow);

    private void AddContainer(
        Instance instance,
        DockerContainerState state = DockerContainerState.Running,
        Guid? labelledFor = null,
        IReadOnlyDictionary<string, string>? addresses = null) =>
        _docker.AddContainer(new DockerContainer(
            DockerResourceNaming.ContainerName(instance.Id),
            "postgres:16",
            state,
            DockerResourceNaming.InstanceLabels(labelledFor ?? instance.Id),
            [],
            addresses ?? new Dictionary<string, string> { [Network] = "172.20.0.5", ["bridge"] = "172.17.0.9" }));

    [Theory]
    [InlineData(InstanceEngine.Postgres, 5432)]
    [InlineData(InstanceEngine.Mysql, 3306)]
    public async Task RunningContainer_ResolvesToItsAddressOnTheAuroraNetwork_AndTheEnginesPort(InstanceEngine engine, int port)
    {
        var instance = NewInstance(engine);
        AddContainer(instance);

        var endpoint = await _resolver.ResolveAsync(instance, default);

        Assert.Equal(new InstanceEndpoint("172.20.0.5", port), endpoint);
        // Looking up an endpoint only inspects; it never starts, creates or changes anything.
        Assert.Equal([$"FindContainerAsync {DockerResourceNaming.ContainerName(instance.Id)}"], _docker.Calls);
    }

    [Fact]
    public async Task MissingContainer_IsEngineUnavailable()
    {
        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() => _resolver.ResolveAsync(NewInstance(), default));

        Assert.Equal("DATABASE_ENGINE_UNAVAILABLE", exception.Code);
    }

    [Theory]
    [InlineData(DockerContainerState.Exited)]
    [InlineData(DockerContainerState.Created)]
    [InlineData(DockerContainerState.Other)]
    public async Task ContainerThatIsNotRunning_IsEngineUnavailable_AndIsNotStarted(DockerContainerState state)
    {
        var instance = NewInstance();
        AddContainer(instance, state);

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() => _resolver.ResolveAsync(instance, default));

        Assert.Equal("DATABASE_ENGINE_UNAVAILABLE", exception.Code);
        Assert.Equal(0, _docker.CountCalls(nameof(IDockerEngine.StartContainerAsync)));
    }

    [Fact]
    public async Task ContainerWithTheInstancesNameButAnotherOwner_IsNeverUsed()
    {
        var instance = NewInstance();
        AddContainer(instance, labelledFor: Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() => _resolver.ResolveAsync(instance, default));

        Assert.Equal("DATABASE_ENGINE_UNAVAILABLE", exception.Code);
    }

    [Theory]
    [InlineData("bridge", "172.17.0.9")]
    [InlineData(Network, "")]
    public async Task ContainerWithoutAnAddressOnTheAuroraNetwork_IsEngineUnavailable(string network, string address)
    {
        var instance = NewInstance();
        AddContainer(instance, addresses: new Dictionary<string, string> { [network] = address });

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() => _resolver.ResolveAsync(instance, default));

        Assert.Equal("DATABASE_ENGINE_UNAVAILABLE", exception.Code);
    }

    [Fact]
    public async Task DockerUnavailable_IsEngineUnavailable_WithoutDaemonDetails()
    {
        var instance = NewInstance();
        AddContainer(instance);
        _docker.Unavailable = true;

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() => _resolver.ResolveAsync(instance, default));

        Assert.Equal("DATABASE_ENGINE_UNAVAILABLE", exception.Code);
        Assert.DoesNotContain("docker.sock", exception.Message);
        Assert.DoesNotContain("raw-daemon-detail", exception.Message);
        Assert.IsType<DockerEngineException>(exception.InnerException);
    }
}
