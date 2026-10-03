using System.Net;
using System.Runtime.InteropServices;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Docker;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Integration;

/// <summary>
/// Database operations against real PostgreSQL and MySQL containers: the whole application with
/// the real provisioner, endpoint resolver, secret store and database managers. Opt-in: see
/// <see cref="DockerFactAttribute"/>. Each test uses its own network and removes everything it created.
/// </summary>
/// <remarks>
/// The managers connect over the engine's own protocol to the container's address on the Docker
/// network; no port is published. The tests therefore have to run where that network is
/// reachable: on a Linux Docker host, or inside a container, which then joins the test's network.
/// On Docker Desktop run them with <c>tests/run-docker-integration-tests.sh</c>. What is in the
/// engine is checked independently of the managers, with the engine's own client inside the container.
/// </remarks>
[Trait("Category", "DockerIntegration")]
public sealed class DatabaseOperationsIntegrationTests : IAsyncLifetime
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable(DockerFactAttribute.EnableVariable) == "1";
    private static readonly bool InContainer = File.Exists("/.dockerenv");
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddHours(-1);

    private readonly string _network = $"aurora-db-test-{Guid.NewGuid():N}";
    private readonly List<Guid> _instanceIds = [];
    private DockerEngine _engine = null!;
    private DockerClient _docker = null!;
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        if (!InContainer && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new InvalidOperationException(
                "Instance containers publish no ports, and on Docker Desktop their network is not reachable from the host. "
                + "Run these tests with tests/run-docker-integration-tests.sh, which runs them in a container.");
        }

        var options = new DockerOptions { NetworkName = _network };
        _engine = new DockerEngine(Options.Create(options));
        _docker = DockerClientFactory.Create(options);

        await _engine.CreateNetworkAsync(_network, DockerResourceNaming.NetworkLabels(), default);
        if (InContainer)
        {
            // A container's host name is its id. Joining the network is what running the API in a
            // container on the Aurora network amounts to.
            await _docker.Networks.ConnectNetworkAsync(_network, new NetworkConnectParameters { Container = Environment.MachineName });
        }

        // No worker: each test runs the jobs itself, so nothing depends on timing.
        _factory = new ApiFactory { RealDockerNetwork = _network };
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        if (!Enabled || _engine is null)
        {
            return;
        }

        _client?.Dispose();
        _factory?.Dispose();

        foreach (var instanceId in _instanceIds)
        {
            await _engine.RemoveContainerAsync(DockerResourceNaming.ContainerName(instanceId), default);
            await _engine.RemoveVolumeAsync(DockerResourceNaming.VolumeName(instanceId), default);
        }

        if (InContainer)
        {
            await _docker.Networks.DisconnectNetworkAsync(
                _network, new NetworkDisconnectParameters { Container = Environment.MachineName, Force = true });
        }

        await _docker.Networks.DeleteNetworkAsync(_network);
        _docker.Dispose();
        _engine.Dispose();
    }

    // --- Create -------------------------------------------------------------------------------

    [DockerFact]
    public async Task Postgres_CreateDatabase_ExistsInTheEngine_AndMetadataBecomesReady()
    {
        var instanceId = await CreateRunningInstanceAsync("postgres", "16");

        var (databaseId, jobId) = await _client.CreateDatabaseAsync(instanceId, "application");
        Assert.Equal(0, await CountInEngineAsync(instanceId, "postgres", "application"));
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId, attempt: 1);
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(1, await CountInEngineAsync(instanceId, "postgres", "application"));
        // The new database is usable: a client can connect to it.
        Assert.Equal(0, await ExecAsync(instanceId, "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -d application -c 'select 1'"));
    }

    [DockerFact]
    public async Task Mysql_CreateDatabase_ExistsInTheEngine_AndMetadataBecomesReady()
    {
        var instanceId = await CreateRunningInstanceAsync("mysql", "8.4");

        var (databaseId, jobId) = await _client.CreateDatabaseAsync(instanceId, "application");
        Assert.Equal(0, await CountInEngineAsync(instanceId, "mysql", "application"));
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId, attempt: 1);
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(1, await CountInEngineAsync(instanceId, "mysql", "application"));
        Assert.Equal(0, await ExecAsync(instanceId, "mysql -h 127.0.0.1 -uroot -p\"$MYSQL_ROOT_PASSWORD\" application -e 'select 1'"));
    }

    [DockerFact]
    public Task Postgres_ManagerCreateTwice_LeavesOneDatabase_AndDeleteTwiceSucceeds() =>
        ManagerIsIdempotentAsync("postgres", "16");

    [DockerFact]
    public Task Mysql_ManagerCreateTwice_LeavesOneDatabase_AndDeleteTwiceSucceeds() =>
        ManagerIsIdempotentAsync("mysql", "8.4");

    private async Task ManagerIsIdempotentAsync(string engine, string version)
    {
        var instanceId = await CreateRunningInstanceAsync(engine, version);
        var database = Database.Create(instanceId, "twice", DateTime.UtcNow);

        await WithManagerAsync(instanceId, (manager, instance) => manager.CreateDatabaseAsync(instance, database, default));
        await WithManagerAsync(instanceId, (manager, instance) => manager.CreateDatabaseAsync(instance, database, default));
        Assert.Equal(1, await CountInEngineAsync(instanceId, engine, "twice"));

        await WithManagerAsync(instanceId, (manager, instance) => manager.DeleteDatabaseAsync(instance, database, default));
        await WithManagerAsync(instanceId, (manager, instance) => manager.DeleteDatabaseAsync(instance, database, default));
        Assert.Equal(0, await CountInEngineAsync(instanceId, engine, "twice"));
    }

    // --- Delete -------------------------------------------------------------------------------

    [DockerFact]
    public Task Postgres_DeleteDatabase_IsGoneFromTheEngineAndFromTheMetadata() =>
        CreateThenDeleteAsync("postgres", "16");

    [DockerFact]
    public Task Mysql_DeleteDatabase_IsGoneFromTheEngineAndFromTheMetadata() =>
        CreateThenDeleteAsync("mysql", "8.4");

    private async Task CreateThenDeleteAsync(string engine, string version)
    {
        var instanceId = await CreateRunningInstanceAsync(engine, version);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "doomed");
        var keptId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "kept");
        Assert.Equal(1, await CountInEngineAsync(instanceId, engine, "doomed"));

        var jobId = await _client.DeleteDatabaseAsync(databaseId);
        // Requested, not done: the engine's database and the metadata are both still there.
        Assert.Equal("deleting", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(1, await CountInEngineAsync(instanceId, engine, "doomed"));
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId, attempt: 1);
        Assert.Equal(0, await CountInEngineAsync(instanceId, engine, "doomed"));
        await (await _client.GetAsync($"{DatabasesUrl}/{databaseId}")).AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
        // Its neighbour is untouched.
        Assert.Equal(1, await CountInEngineAsync(instanceId, engine, "kept"));
        Assert.Equal("ready", (await _client.GetDatabaseAsync(keptId)).Status());
    }

    [DockerFact]
    public async Task Postgres_DeleteDatabase_WithAClientStillConnected_Succeeds()
    {
        var instanceId = await CreateRunningInstanceAsync("postgres", "16");
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "busy");
        // A client that stays connected for longer than the test runs. It is held by its own exec:
        // detached inside the container it would become a child of the server's main process,
        // which treats the exit of any child it does not know as a crashed server process.
        var session = ExecAsync(
            instanceId,
            "PGPASSWORD=\"$POSTGRES_PASSWORD\" exec psql -h 127.0.0.1 -U postgres -d busy -c 'select pg_sleep(300)'");
        await WaitUntilAsync(async () => await ExecAsync(
            instanceId,
            "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -tAc \"select count(*) from pg_stat_activity where datname = 'busy'\" | grep -qx 1") == 0);

        var jobId = await _client.DeleteDatabaseAsync(databaseId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId, attempt: 1);
        Assert.Equal(0, await CountInEngineAsync(instanceId, "postgres", "busy"));
        // The client's session was ended by the drop, and the server is still up for everyone else.
        Assert.NotEqual(0, await session.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, await ExecAsync(instanceId, "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -c 'select 1'"));
    }

    // --- Interruption -------------------------------------------------------------------------

    [DockerFact]
    public Task Postgres_CreateInterruptedAfterTheEngineCreatedTheDatabase_IsAdoptedOnRetry() =>
        InterruptedCreateIsAdoptedAsync("postgres", "16");

    [DockerFact]
    public Task Mysql_CreateInterruptedAfterTheEngineCreatedTheDatabase_IsAdoptedOnRetry() =>
        InterruptedCreateIsAdoptedAsync("mysql", "8.4");

    private async Task InterruptedCreateIsAdoptedAsync(string engine, string version)
    {
        var instanceId = await CreateRunningInstanceAsync(engine, version);
        var (databaseId, jobId) = await _client.CreateDatabaseAsync(instanceId, "interrupted");

        // An execution claims the job, creates the database in the engine, and dies before it
        // can record that: the job is left running under a lease nobody renews.
        var database = await _factory.WithDbAsync(db => db.Databases.AsNoTracking().SingleAsync(d => d.Id == databaseId));
        await WithManagerAsync(instanceId, (manager, instance) => manager.CreateDatabaseAsync(instance, database, default));
        await _factory.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);
        Assert.Equal("creating", (await _client.GetDatabaseAsync(databaseId)).Status());

        Assert.Equal([jobId], await _factory.RecoverJobsAsync(includePending: false));
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId, attempt: 1);
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(1, await CountInEngineAsync(instanceId, engine, "interrupted"));
    }

    [DockerFact]
    public Task Postgres_DeleteInterruptedAfterTheEngineDroppedTheDatabase_RemovesTheMetadataOnRetry() =>
        InterruptedDeleteRemovesMetadataAsync("postgres", "16");

    [DockerFact]
    public Task Mysql_DeleteInterruptedAfterTheEngineDroppedTheDatabase_RemovesTheMetadataOnRetry() =>
        InterruptedDeleteRemovesMetadataAsync("mysql", "8.4");

    private async Task InterruptedDeleteRemovesMetadataAsync(string engine, string version)
    {
        var instanceId = await CreateRunningInstanceAsync(engine, version);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "interrupted");
        var jobId = await _client.DeleteDatabaseAsync(databaseId);

        // An execution claims the job, drops the database, and dies before removing the metadata.
        var database = await _factory.WithDbAsync(db => db.Databases.AsNoTracking().SingleAsync(d => d.Id == databaseId));
        await WithManagerAsync(instanceId, (manager, instance) => manager.DeleteDatabaseAsync(instance, database, default));
        await _factory.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);
        Assert.Equal(0, await CountInEngineAsync(instanceId, engine, "interrupted"));
        Assert.Equal("deleting", (await _client.GetDatabaseAsync(databaseId)).Status());

        Assert.Equal([jobId], await _factory.RecoverJobsAsync(includePending: false));
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId, attempt: 1);
        await (await _client.GetAsync($"{DatabasesUrl}/{databaseId}")).AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
    }

    // --- The instance goes away ---------------------------------------------------------------

    [DockerFact]
    public async Task Postgres_InstanceContainerStopped_OperationsFailSafely_AndNothingIsStartedOrLost()
    {
        var instanceId = await CreateRunningInstanceAsync("postgres", "16");
        var keptId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "kept");
        var containerName = DockerResourceNaming.ContainerName(instanceId);
        await _docker.Containers.StopContainerAsync(containerName, new ContainerStopParameters { WaitBeforeKillSeconds = 30 });

        var (createdId, createJobId) = await _client.CreateDatabaseAsync(instanceId, "late");
        await _factory.ProcessJobAsync(createJobId);

        var createJob = await _client.GetJobAsync(createJobId);
        Assert.Equal("failed", createJob.Status());
        Assert.Equal(3, createJob.GetProperty("attempt").GetInt32());
        Assert.Equal("DATABASE_ENGINE_UNAVAILABLE", createJob.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("failed", (await _client.GetDatabaseAsync(createdId)).Status());

        var deleteJobId = await _client.DeleteDatabaseAsync(keptId);
        await _factory.ProcessJobAsync(deleteJobId);

        Assert.Equal("failed", (await _client.GetJobAsync(deleteJobId)).Status());
        // The delete did not happen, so the metadata is kept.
        Assert.Equal("failed", (await _client.GetDatabaseAsync(keptId)).Status());
        // Database jobs never start an instance.
        Assert.False((await _docker.Containers.InspectContainerAsync(containerName)).State.Running);

        await _docker.Containers.StartContainerAsync(containerName, new ContainerStartParameters());
        await WaitUntilAsync(async () => await ExecAsync(instanceId, "pg_isready -q -h 127.0.0.1 -p 5432") == 0);
        Assert.Equal(1, await CountInEngineAsync(instanceId, "postgres", "kept"));
        Assert.Equal(0, await CountInEngineAsync(instanceId, "postgres", "late"));
    }

    [DockerFact]
    public async Task Api_NeverExposesTheAdministratorPassword()
    {
        var instanceId = await CreateRunningInstanceAsync("postgres", "16");
        var (databaseId, jobId) = await _client.CreateDatabaseAsync(instanceId, "application");
        await _factory.ProcessJobAsync(jobId);

        var container = await _docker.Containers.InspectContainerAsync(DockerResourceNaming.ContainerName(instanceId));
        var password = container.Config.Env.Single(variable => variable.StartsWith("POSTGRES_PASSWORD=", StringComparison.Ordinal))["POSTGRES_PASSWORD=".Length..];
        // No port of the container is published on the host.
        Assert.DoesNotContain(container.NetworkSettings.Ports.Values, bindings => bindings is { Count: > 0 });

        foreach (var url in new[] { $"{DatabasesUrl}/{databaseId}", InstanceDatabasesUrl(instanceId), $"{JobsUrl}/{jobId}", $"{InstancesUrl}/{instanceId}" })
        {
            Assert.DoesNotContain(password, await _client.GetStringAsync(url));
        }
    }

    // --- Helpers ------------------------------------------------------------------------------

    private async Task<Guid> CreateRunningInstanceAsync(string engine, string version)
    {
        var response = await _client.PostAsync(
            InstancesUrl,
            System.Net.Http.Json.JsonContent.Create(ValidInstanceRequest(engine: engine, version: version, memoryMb: engine == "mysql" ? 1024 : 512, storageGb: 1)));
        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        var instanceId = body.GetProperty("instance").GetProperty("id").GetGuid();
        _instanceIds.Add(instanceId);

        await _factory.ProcessJobAsync(body.GetProperty("job").GetProperty("id").GetGuid());
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
        return instanceId;
    }

    private async Task AssertJobCompletedAsync(Guid jobId, int attempt)
    {
        var job = await _client.GetJobAsync(jobId);
        Assert.True(job.Status() == "completed", $"Job is {job.Status()}: {job.GetProperty("error").GetRawText()}");
        Assert.Equal(attempt, job.GetProperty("attempt").GetInt32());
    }

    /// <summary>Calls the instance's real database manager directly, the way a job attempt would.</summary>
    private async Task WithManagerAsync(Guid instanceId, Func<IDatabaseManager, Instance, Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var instance = await _factory.WithDbAsync(db => db.Instances.AsNoTracking().SingleAsync(i => i.Id == instanceId));
        var manager = scope.ServiceProvider.GetServices<IDatabaseManager>().Single(candidate => candidate.Engine == instance.Engine);
        await action(manager, instance);
    }

    /// <summary>How many databases of that name the engine itself reports, asked with its own client inside the container.</summary>
    private async Task<int> CountInEngineAsync(Guid instanceId, string engine, string name)
    {
        var query = engine == "postgres"
            ? $"PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -tAc \"select count(*) from pg_database where datname = '{name}'\""
            : $"mysql -h 127.0.0.1 -uroot -p\"$MYSQL_ROOT_PASSWORD\" -N -B -e \"select count(*) from information_schema.schemata where schema_name = '{name}'\"";

        foreach (var count in new[] { 0, 1, 2 })
        {
            if (await ExecAsync(instanceId, $"{query} | grep -qx {count}") == 0)
            {
                return count;
            }
        }

        throw new InvalidOperationException($"Could not count databases named '{name}' in the {engine} container.");
    }

    private Task<int> ExecAsync(Guid instanceId, string shellCommand) =>
        _engine.ExecAsync(DockerResourceNaming.ContainerName(instanceId), ["sh", "-c", shellCommand], default);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
        }
    }
}
