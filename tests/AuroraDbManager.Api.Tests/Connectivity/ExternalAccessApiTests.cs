using System.Net;
using System.Text.Json;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Infrastructure.Docker;
using Microsoft.EntityFrameworkCore;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Connectivity;

/// <summary>
/// External access through the API, with the real provisioner over the in-memory Docker Engine:
/// who may change it, which port an instance gets, what the record and Docker say afterwards,
/// what is refused and with which code, and what a response contains.
/// </summary>
public sealed class ExternalAccessApiTests : IDisposable
{
    private readonly ApiFactory _factory = new() { UseDockerProvisioner = true };
    private readonly HttpClient _client;

    public ExternalAccessApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static string ExternalAccessUrl(Guid instanceId) => $"{InstancesUrl}/{instanceId}/external-access";

    private static string ConnectionUrl(Guid instanceId) => $"{InstancesUrl}/{instanceId}/connection";

    private static string DatabaseConnectionUrl(Guid databaseId) => $"{DatabasesUrl}/{databaseId}/connection";

    private static Task<HttpResponseMessage> EnableAsync(HttpClient client, Guid instanceId) => client.PostAsync(ExternalAccessUrl(instanceId), content: null);

    private Task<HttpResponseMessage> EnableAsync(Guid instanceId) => EnableAsync(_client, instanceId);

    private Task<HttpResponseMessage> DisableAsync(Guid instanceId) => _client.DeleteAsync(ExternalAccessUrl(instanceId));

    private async Task<int> EnabledPortAsync(Guid instanceId) =>
        (await (await EnableAsync(instanceId)).ReadJsonAsync(HttpStatusCode.OK)).GetProperty("external").GetProperty("port").GetInt32();

    private async Task<JsonElement> ConnectionAsync(Guid instanceId) =>
        await (await _client.GetAsync(ConnectionUrl(instanceId))).ReadJsonAsync(HttpStatusCode.OK);

    private IReadOnlyList<DockerPortBinding> Published(Guid instanceId) =>
        _factory.Docker.Containers[DockerResourceNaming.ContainerName(instanceId)].PortBindings ?? [];

    private Task<(bool Enabled, int? Port)> RecordAsync(Guid instanceId) => _factory.WithDbAsync(async db =>
    {
        var instance = await db.Instances.AsNoTracking().SingleAsync(i => i.Id == instanceId);
        return (instance.ExternalAccessEnabled, instance.ExternalPort);
    });

    // --- Private until enabled -----------------------------------------------------------------

    [Theory]
    [InlineData("postgres", "16", 5432, "postgres")]
    [InlineData("mysql", "8.4", 3306, "root")]
    public async Task NewInstance_IsPrivate_AndSaysWhereItIsOnTheDockerNetwork(string engine, string version, int port, string username)
    {
        var response = await _client.PostAsync(InstancesUrl, System.Net.Http.Json.JsonContent.Create(ValidInstanceRequest(engine: engine, version: version)));
        var created = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        var instanceId = created.GetProperty("instance").GetProperty("id").GetGuid();
        await _factory.ProcessJobAsync(created.GetProperty("job").GetProperty("id").GetGuid());

        var connection = await ConnectionAsync(instanceId);

        Assert.Equal(["engine", "external", "instanceId", "internal", "username"], connection.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(engine, connection.GetProperty("engine").GetString());
        Assert.Equal(username, connection.GetProperty("username").GetString());
        // Disabled: no host and no port, only the address ports would be bound to.
        Assert.Equal(["bindAddress", "enabled"], connection.GetProperty("external").EnumerateObject().Select(p => p.Name).Order());
        Assert.False(connection.GetProperty("external").GetProperty("enabled").GetBoolean());
        Assert.Equal("127.0.0.1", connection.GetProperty("external").GetProperty("bindAddress").GetString());
        Assert.Equal("aurora-db", connection.GetProperty("internal").GetProperty("network").GetString());
        Assert.Equal($"aurora-instance-{instanceId}", connection.GetProperty("internal").GetProperty("host").GetString());
        Assert.Equal(port, connection.GetProperty("internal").GetProperty("port").GetInt32());
        Assert.Empty(Published(instanceId));
        Assert.Equal((false, null), await RecordAsync(instanceId));
    }

    // --- Enabling ------------------------------------------------------------------------------

    [Fact]
    public async Task Enable_AllocatesTheFirstPortOfTheRange_PublishesIt_AndRecordsIt()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);

        var response = await EnableAsync(instanceId);

        var connection = await response.ReadJsonAsync(HttpStatusCode.OK);
        var external = connection.GetProperty("external");
        Assert.True(external.GetProperty("enabled").GetBoolean());
        Assert.Equal(15432, external.GetProperty("port").GetInt32());
        Assert.Equal("127.0.0.1", external.GetProperty("host").GetString());
        Assert.Equal("127.0.0.1", external.GetProperty("bindAddress").GetString());
        Assert.Equal([new DockerPortBinding(5432, "127.0.0.1", 15432)], Published(instanceId));
        Assert.Equal(DockerContainerState.Running, _factory.Docker.Containers[DockerResourceNaming.ContainerName(instanceId)].State);
        Assert.Equal((true, 15432), await RecordAsync(instanceId));
        Assert.Equal(connection.GetRawText(), (await ConnectionAsync(instanceId)).GetRawText());
        // The instance is what it was: running, and with the data volume it had.
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
        Assert.Equal(0, _factory.Docker.CountCalls(nameof(IDockerEngine.RemoveVolumeAsync)));
        Assert.Equal(1, _factory.Docker.CountCalls(nameof(IDockerEngine.CreateVolumeAsync)));
    }

    [Fact]
    public async Task Enable_TakesNoPortFromTheRequest_WhateverItSends()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);

        var response = await _client.PostAsync(
            $"{ExternalAccessUrl(instanceId)}?port=22&hostPort=22&bindAddress=0.0.0.0",
            System.Net.Http.Json.JsonContent.Create(new { port = 22, hostPort = 22, externalPort = 22, bindAddress = "0.0.0.0", host = "0.0.0.0" }));

        await response.ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal([new DockerPortBinding(5432, "127.0.0.1", 15432)], Published(instanceId));
    }

    [Fact]
    public async Task EachInstance_GetsItsOwnPort_AndPortsAlreadyPublishedInDockerAreSkipped()
    {
        var first = await _factory.CreateRunningInstanceAsync(_client, name: "first");
        var second = await _factory.CreateRunningInstanceAsync(_client, name: "second");
        var third = await _factory.CreateRunningInstanceAsync(_client, name: "third", engine: "postgres");
        // Something that is not Aurora's publishes 15433.
        _factory.Docker.OtherPublishedHostPorts.Add(15433);

        Assert.Equal(15432, await EnabledPortAsync(first));
        Assert.Equal(15434, await EnabledPortAsync(second));
        Assert.Equal(15435, await EnabledPortAsync(third));

        Assert.Equal(3, (await _factory.WithDbAsync(db => db.Instances.Select(i => i.ExternalPort).ToListAsync())).Distinct().Count());
    }

    [Fact]
    public async Task PortTakenBySomethingDockerDoesNotList_IsFoundOutWhenPublishing_AndTheNextPortIsUsed()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        _factory.Docker.HostPortsTakenOutsideDocker.Add(15432);
        _factory.Docker.HostPortsTakenOutsideDocker.Add(15433);

        Assert.Equal(15434, await EnabledPortAsync(instanceId));

        Assert.Equal([new DockerPortBinding(5432, "127.0.0.1", 15434)], Published(instanceId));
        Assert.Equal((true, 15434), await RecordAsync(instanceId));
        Assert.DoesNotContain(DockerResourceNaming.ReplacedContainerName(instanceId), _factory.Docker.Containers.Keys);
    }

    [Fact]
    public async Task NoPortLeftInTheRange_IsRefused_AndTheInstanceStaysPrivate()
    {
        using var factory = new ApiFactory
        {
            UseDockerProvisioner = true,
            ConfigureExternalAccess = options => (options.PortRangeStart, options.PortRangeEnd) = (20000, 20001)
        };
        using var client = factory.CreateClient();
        var first = await factory.CreateRunningInstanceAsync(client, name: "first");
        var second = await factory.CreateRunningInstanceAsync(client, name: "second");
        var third = await factory.CreateRunningInstanceAsync(client, name: "third");
        await (await EnableAsync(client, first)).ReadJsonAsync(HttpStatusCode.OK);
        await (await EnableAsync(client, second)).ReadJsonAsync(HttpStatusCode.OK);

        var response = await EnableAsync(client, third);

        var error = await response.AssertErrorAsync(HttpStatusCode.Conflict, "PORT_ALLOCATION_FAILED");
        Assert.Equal("No free host port could be allocated in the configured port range.", error.GetProperty("message").GetString());
        Assert.Empty(factory.Docker.Containers[DockerResourceNaming.ContainerName(third)].PortBindings ?? []);
        Assert.Equal([20000, 20001], (await factory.WithDbAsync(db => db.Instances.Where(i => i.ExternalPort != null).Select(i => i.ExternalPort!.Value).ToListAsync())).Order());
    }

    [Fact]
    public async Task EveryPortTriedIsTaken_GivesUpAfterAFewAttempts_WithAStableError()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        for (var port = 15432; port <= 16432; port++)
        {
            _factory.Docker.HostPortsTakenOutsideDocker.Add(port);
        }

        var response = await EnableAsync(instanceId);

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "PORT_ALLOCATION_FAILED");
        Assert.Equal((false, null), await RecordAsync(instanceId));
        Assert.Empty(Published(instanceId));
        Assert.Equal(DockerContainerState.Running, _factory.Docker.Containers[DockerResourceNaming.ContainerName(instanceId)].State);
        Assert.Equal(Application.Connectivity.InstanceConnectivityService.MaxAllocationAttempts, _factory.Docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync)) - 1);
    }

    [Fact]
    public async Task ConcurrentRequests_ForDifferentInstances_NeverGetTheSamePort()
    {
        var instances = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            instances.Add(await _factory.CreateRunningInstanceAsync(_client, name: $"instance-{i}"));
        }

        var responses = await Task.WhenAll(instances.Select(EnableAsync));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var ports = await _factory.WithDbAsync(db => db.Instances.Select(i => i.ExternalPort).ToListAsync());
        Assert.Equal(6, ports.Count(port => port is not null));
        Assert.Equal(6, ports.Distinct().Count());
        Assert.All(instances, instanceId => Assert.Single(Published(instanceId)));
        Assert.Equal(6, instances.Select(instanceId => Published(instanceId)[0].HostPort).Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentRequests_ForTheSameInstance_EnableItOnce()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => EnableAsync(instanceId)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        foreach (var refused in responses.Where(response => response.StatusCode != HttpStatusCode.OK))
        {
            await refused.AssertErrorAsync(HttpStatusCode.Conflict, "EXTERNAL_ACCESS_ALREADY_ENABLED");
        }

        var (enabled, port) = await RecordAsync(instanceId);
        Assert.True(enabled);
        Assert.Equal([new DockerPortBinding(5432, "127.0.0.1", port!.Value)], Published(instanceId));
    }

    // --- Disabling -----------------------------------------------------------------------------

    [Fact]
    public async Task Disable_UnpublishesThePort_AndGivesItUpForTheNextInstance()
    {
        var first = await _factory.CreateRunningInstanceAsync(_client, name: "first");
        var second = await _factory.CreateRunningInstanceAsync(_client, name: "second");
        Assert.Equal(15432, await EnabledPortAsync(first));

        var response = await DisableAsync(first);

        var connection = await response.ReadJsonAsync(HttpStatusCode.OK);
        Assert.False(connection.GetProperty("external").GetProperty("enabled").GetBoolean());
        Assert.False(connection.GetProperty("external").TryGetProperty("port", out _));
        Assert.False(connection.GetProperty("external").TryGetProperty("host", out _));
        Assert.Empty(Published(first));
        Assert.Equal((false, null), await RecordAsync(first));
        Assert.Equal(DockerContainerState.Running, _factory.Docker.Containers[DockerResourceNaming.ContainerName(first)].State);
        Assert.Equal(0, _factory.Docker.CountCalls(nameof(IDockerEngine.RemoveVolumeAsync)));
        // Free again.
        Assert.Equal(15432, await EnabledPortAsync(second));
    }

    // --- Refusals ------------------------------------------------------------------------------

    [Fact]
    public async Task EnablingTwice_AndDisablingWhatIsDisabled_AreRefusedWithTheirOwnCodes()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);

        await (await DisableAsync(instanceId)).AssertErrorAsync(HttpStatusCode.Conflict, "EXTERNAL_ACCESS_ALREADY_DISABLED");
        await (await EnableAsync(instanceId)).ReadJsonAsync(HttpStatusCode.OK);
        await (await EnableAsync(instanceId)).AssertErrorAsync(HttpStatusCode.Conflict, "EXTERNAL_ACCESS_ALREADY_ENABLED");

        Assert.Equal((true, 15432), await RecordAsync(instanceId));
        Assert.Equal(2, _factory.Docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync)));
    }

    [Fact]
    public async Task InstanceThatIsNotRunning_OrDoesNotExist_IsRefused()
    {
        var (provisioning, _) = await _client.CreateInstanceAsync(name: "still-provisioning");
        var (failed, failing) = await _client.CreateInstanceAsync(name: "broken");
        _factory.Docker.FailOn(nameof(IDockerEngine.CreateContainerAsync));
        await _factory.ProcessJobAsync(failing);

        await (await EnableAsync(provisioning)).AssertErrorAsync(HttpStatusCode.Conflict, "INSTANCE_NOT_READY");
        await (await EnableAsync(failed)).AssertErrorAsync(HttpStatusCode.Conflict, "INSTANCE_NOT_READY");
        await (await EnableAsync(Guid.NewGuid())).AssertErrorAsync(HttpStatusCode.NotFound, "INSTANCE_NOT_FOUND");
        await (await DisableAsync(Guid.NewGuid())).AssertErrorAsync(HttpStatusCode.NotFound, "INSTANCE_NOT_FOUND");
        await (await _client.GetAsync(ConnectionUrl(Guid.NewGuid()))).AssertErrorAsync(HttpStatusCode.NotFound, "INSTANCE_NOT_FOUND");
        await (await _client.GetAsync(DatabaseConnectionUrl(Guid.NewGuid()))).AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
        await (await _client.PostAsync($"{InstancesUrl}/not-an-id/external-access", null)).AssertErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");

        Assert.Equal(0, await _factory.WithDbAsync(db => db.Instances.CountAsync(i => i.ExternalAccessEnabled)));
    }

    [Fact]
    public async Task InstanceWithAJobAtWork_IsNotRestartedUnderIt()
    {
        var creating = await _factory.CreateRunningInstanceAsync(_client, name: "creating");
        await _client.CreateDatabaseAsync(creating, "being_created");
        var backingUp = await _factory.CreateRunningInstanceAsync(_client, name: "backing-up");
        await _client.CreateBackupAsync(await _factory.CreateReadyDatabaseAsync(_client, backingUp, "app"));
        var containers = _factory.Docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync));

        await (await EnableAsync(creating)).AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_OPERATION_IN_PROGRESS");
        await (await EnableAsync(backingUp)).AssertErrorAsync(HttpStatusCode.Conflict, "BACKUP_OPERATION_IN_PROGRESS");

        Assert.Equal(containers, _factory.Docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync)));
        Assert.Equal(0, _factory.Docker.CountCalls(nameof(IDockerEngine.StopContainerAsync)));
    }

    // --- Docker fails --------------------------------------------------------------------------

    [Fact]
    public async Task DockerCannotApplyTheChange_NothingIsRecorded_TheServerRunsAsBefore_AndNothingInternalIsSaid()
    {
        // A second is all a database gets to come up here, so that the test need not wait two minutes.
        using var factory = new ApiFactory { UseDockerProvisioner = true, ConfigureDocker = options => options.ReadinessTimeoutSeconds = 1 };
        using var client = factory.CreateClient();
        var instanceId = await factory.CreateRunningInstanceAsync(client);
        var password = await factory.AdminPasswordAsync(instanceId);
        var name = DockerResourceNaming.ContainerName(instanceId);
        // The database never comes up in a container that publishes a port.
        factory.Docker.Exec = () => (factory.Docker.Containers.GetValueOrDefault(name)?.PortBindings ?? []).Count == 0 ? 0 : 1;

        var response = await EnableAsync(client, instanceId);

        var error = await response.AssertErrorAsync(HttpStatusCode.ServiceUnavailable, "DOCKER_PORT_CONFIGURATION_FAILED");
        Assert.Equal(
            "The database server could not be restarted with the new port configuration. It is running as it was before.",
            error.GetProperty("message").GetString());
        // The record never said enabled, and does not now.
        var stored = await factory.WithDbAsync(db => db.Instances.AsNoTracking().SingleAsync());
        Assert.Equal((false, null), (stored.ExternalAccessEnabled, stored.ExternalPort));
        Assert.False((await (await client.GetAsync(ConnectionUrl(instanceId))).ReadJsonAsync(HttpStatusCode.OK)).GetProperty("external").GetProperty("enabled").GetBoolean());
        Assert.Empty(factory.Docker.Containers[name].PortBindings ?? []);
        Assert.Equal(DockerContainerState.Running, factory.Docker.Containers[name].State);
        Assert.Equal("running", (await client.GetInstanceAsync(instanceId)).Status());
        Assert.Equal(0, factory.Docker.CountCalls(nameof(IDockerEngine.RemoveVolumeAsync)));

        var text = await response.Content.ReadAsStringAsync();
        foreach (var leaked in new[] { password, "raw-daemon-detail", "docker.sock", "aurora-instance-", "Exception", "   at " })
        {
            Assert.DoesNotContain(leaked, text, StringComparison.Ordinal);
        }

        // And it can be tried again once Docker cooperates.
        factory.Docker.Exec = () => 0;
        var retried = await (await EnableAsync(client, instanceId)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(15432, retried.GetProperty("external").GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task DockerUnreachable_IsAnsweredWith503_AndNothingChanges()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        _factory.Docker.Unavailable = true;

        var response = await EnableAsync(instanceId);

        await response.AssertErrorAsync(HttpStatusCode.ServiceUnavailable, "DOCKER_UNAVAILABLE");
        Assert.Equal((false, null), await RecordAsync(instanceId));
    }

    [Fact]
    public async Task DisableThatFails_LeavesTheInstanceEnabled_OnItsPort()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        Assert.Equal(15432, await EnabledPortAsync(instanceId));
        _factory.Docker.FailOn(nameof(IDockerEngine.CreateContainerAsync));

        var response = await DisableAsync(instanceId);

        await response.AssertErrorAsync(HttpStatusCode.ServiceUnavailable, "DOCKER_PORT_CONFIGURATION_FAILED");
        Assert.Equal((true, 15432), await RecordAsync(instanceId));
        Assert.Equal([new DockerPortBinding(5432, "127.0.0.1", 15432)], Published(instanceId));
        Assert.Equal(DockerContainerState.Running, _factory.Docker.Containers[DockerResourceNaming.ContainerName(instanceId)].State);
    }

    // --- After a restart -----------------------------------------------------------------------

    [Fact]
    public async Task Restart_KeepsTheAllocation_AndTheNextInstanceGetsAnotherPort()
    {
        using var database = new TempDatabase();
        Guid first;
        using (var before = new ApiFactory { UseDockerProvisioner = true, DatabasePath = database.Path })
        {
            using var client = before.CreateClient();
            first = await before.CreateRunningInstanceAsync(client, name: "first");
            await (await EnableAsync(client, first)).ReadJsonAsync(HttpStatusCode.OK);
        }

        // A new process, and a Docker Engine that happens to list nothing: the record is enough.
        using var after = new ApiFactory { UseDockerProvisioner = true, DatabasePath = database.Path };
        using var restarted = after.CreateClient();
        var second = await after.CreateRunningInstanceAsync(restarted, name: "second");

        var kept = await (await restarted.GetAsync(ConnectionUrl(first))).ReadJsonAsync(HttpStatusCode.OK);
        var next = await (await EnableAsync(restarted, second)).ReadJsonAsync(HttpStatusCode.OK);

        Assert.True(kept.GetProperty("external").GetProperty("enabled").GetBoolean());
        Assert.Equal(15432, kept.GetProperty("external").GetProperty("port").GetInt32());
        Assert.Equal(15433, next.GetProperty("external").GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task Reconciliation_LeavesAnExposedInstanceRunning_OnItsPort_WithoutReplacingItsContainer()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        Assert.Equal(15432, await EnabledPortAsync(instanceId));
        var container = _factory.Docker.Containers[DockerResourceNaming.ContainerName(instanceId)];
        _factory.Docker.Calls.Clear();

        var report = await _factory.ReconcileAsync();

        Assert.Equal(1, report.Verified);
        Assert.Empty(report.Failed);
        Assert.Same(container, _factory.Docker.Containers[DockerResourceNaming.ContainerName(instanceId)]);
        Assert.Equal(0, _factory.Docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync)));
        Assert.Equal(0, _factory.Docker.CountCalls(nameof(IDockerEngine.StopContainerAsync)));
    }

    [Fact]
    public async Task Reconciliation_BringsAContainerBackInLineWithTheRecord_InBothDirections()
    {
        var exposedOnRecord = await _factory.CreateRunningInstanceAsync(_client, name: "exposed-on-record");
        var privateOnRecord = await _factory.CreateRunningInstanceAsync(_client, name: "private-on-record");
        Assert.Equal(15432, await EnabledPortAsync(exposedOnRecord));
        // Docker, behind the application's back: one lost its port, the other gained one.
        var exposedName = DockerResourceNaming.ContainerName(exposedOnRecord);
        var privateName = DockerResourceNaming.ContainerName(privateOnRecord);
        _factory.Docker.Containers[exposedName] = _factory.Docker.Containers[exposedName] with { PortBindings = null };
        _factory.Docker.Containers[privateName] = _factory.Docker.Containers[privateName] with
        {
            PortBindings = [new DockerPortBinding(5432, "0.0.0.0", 5432)]
        };

        var report = await _factory.ReconcileAsync();

        Assert.Equal(2, report.Verified);
        Assert.Equal([new DockerPortBinding(5432, "127.0.0.1", 15432)], Published(exposedOnRecord));
        // What the record says is private is private again.
        Assert.Empty(Published(privateOnRecord));
        Assert.Equal(0, _factory.Docker.CountCalls(nameof(IDockerEngine.RemoveVolumeAsync)));
        Assert.Equal("running", (await _client.GetInstanceAsync(privateOnRecord)).Status());
    }

    // --- What is told, and what never is -------------------------------------------------------

    [Theory]
    [InlineData("postgres", "16", "postgresql://postgres:<password>@127.0.0.1:15432/app", "postgresql://postgres:<password>@aurora-instance-{0}:5432/app")]
    [InlineData("mysql", "8.4", "mysql://root:<password>@127.0.0.1:15432/app", "mysql://root:<password>@aurora-instance-{0}:3306/app")]
    public async Task DatabaseConnection_HasEverythingButThePassword(string engine, string version, string external, string @internal)
    {
        var created = await (await _client.PostAsync(InstancesUrl, System.Net.Http.Json.JsonContent.Create(ValidInstanceRequest(engine: engine, version: version))))
            .ReadJsonAsync(HttpStatusCode.Accepted);
        var instanceId = created.GetProperty("instance").GetProperty("id").GetGuid();
        await _factory.ProcessJobAsync(created.GetProperty("job").GetProperty("id").GetGuid());
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var password = await _factory.AdminPasswordAsync(instanceId);

        var before = await (await _client.GetAsync(DatabaseConnectionUrl(databaseId))).ReadJsonAsync(HttpStatusCode.OK);
        var enabled = await EnableAsync(instanceId);
        var response = await _client.GetAsync(DatabaseConnectionUrl(databaseId));
        var after = await response.ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal(
            ["connectionStrings", "database", "databaseId", "engine", "external", "instanceId", "internal", "username"],
            after.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("app", after.GetProperty("database").GetString());
        Assert.Equal(engine, after.GetProperty("engine").GetString());
        // While private there is only the way in from the Docker network.
        Assert.False(before.GetProperty("connectionStrings").TryGetProperty("external", out _));
        Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture, @internal, instanceId), before.GetProperty("connectionStrings").GetProperty("internal").GetString());
        Assert.Equal(external, after.GetProperty("connectionStrings").GetProperty("external").GetString());
        Assert.Equal(15432, after.GetProperty("external").GetProperty("port").GetInt32());

        // The password is in no response, and in no log line of any of this.
        foreach (var text in new[] { before.GetRawText(), await enabled.Content.ReadAsStringAsync(), await response.Content.ReadAsStringAsync() })
        {
            Assert.DoesNotContain(password, text, StringComparison.Ordinal);
            Assert.DoesNotContain("PASSWORD", text.Replace("<password>", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains("://", StringComparison.Ordinal) && entry.Contains("@", StringComparison.Ordinal));
        Assert.Contains(_factory.Logs.Entries, entry => entry.Contains($"External access of instance {instanceId} enabled on host port 15432", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BoundOnEveryInterface_TheHostIsOnlyToldIfTheServerIsConfiguredWithOne()
    {
        using var unnamed = new ApiFactory { UseDockerProvisioner = true, ConfigureExternalAccess = options => options.BindAddress = "0.0.0.0" };
        using var named = new ApiFactory
        {
            UseDockerProvisioner = true,
            ConfigureExternalAccess = options => (options.BindAddress, options.AdvertisedHost) = ("0.0.0.0", "db.example.com")
        };

        foreach (var (factory, host, template) in new[]
                 {
                     (unnamed, (string?)null, "postgresql://postgres:<password>@<server-address>:15432/app"),
                     (named, "db.example.com", "postgresql://postgres:<password>@db.example.com:15432/app")
                 })
        {
            using var client = factory.CreateClient();
            var instanceId = await factory.CreateRunningInstanceAsync(client);
            var databaseId = await factory.CreateReadyDatabaseAsync(client, instanceId, "app");

            var external = (await (await EnableAsync(client, instanceId)).ReadJsonAsync(HttpStatusCode.OK)).GetProperty("external");
            var database = await (await client.GetAsync(DatabaseConnectionUrl(databaseId))).ReadJsonAsync(HttpStatusCode.OK);

            Assert.Equal("0.0.0.0", external.GetProperty("bindAddress").GetString());
            Assert.Equal(host, external.TryGetProperty("host", out var value) ? value.GetString() : null);
            Assert.Equal(template, database.GetProperty("connectionStrings").GetProperty("external").GetString());
            Assert.Equal("0.0.0.0", factory.Docker.Containers[DockerResourceNaming.ContainerName(instanceId)].PortBindings!.Single().HostAddress);
        }
    }

    // --- Who may -------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyAnAdministrator_ChangesExternalAccess_AndEverySignedInUser_SeesWhereAnInstanceIs()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        using var anonymous = _factory.CreateAnonymousClient();
        using var viewer = _factory.CreateClientAs(UserRole.Viewer);
        using var op = _factory.CreateClientAs(UserRole.Operator);

        await (await EnableAsync(anonymous, instanceId)).AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        await (await anonymous.DeleteAsync(ExternalAccessUrl(instanceId))).AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        await (await anonymous.GetAsync(ConnectionUrl(instanceId))).AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        await (await anonymous.GetAsync(DatabaseConnectionUrl(databaseId))).AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        foreach (var client in new[] { viewer, op })
        {
            await (await EnableAsync(client, instanceId)).AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
            await (await client.DeleteAsync(ExternalAccessUrl(instanceId))).AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
            await (await client.GetAsync(ConnectionUrl(instanceId))).ReadJsonAsync(HttpStatusCode.OK);
            await (await client.GetAsync(DatabaseConnectionUrl(databaseId))).ReadJsonAsync(HttpStatusCode.OK);
        }

        Assert.Equal((false, null), await RecordAsync(instanceId));
        Assert.Empty(Published(instanceId));

        // An administrator may, and is the only one whose request reaches Docker.
        Assert.Equal(15432, await EnabledPortAsync(instanceId));
    }

    [Fact]
    public async Task InstanceResponses_AreAsTheyWere()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        await EnabledPortAsync(instanceId);

        var instance = await _client.GetInstanceAsync(instanceId);

        Assert.Equal(
            ["cpu", "createdAt", "engine", "error", "id", "memoryMb", "name", "status", "storageGb", "updatedAt", "version"],
            instance.EnumerateObject().Select(p => p.Name).Order());
    }
}
