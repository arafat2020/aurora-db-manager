using System.Data.Common;
using System.Net;
using System.Runtime.InteropServices;
using AuroraDbManager.Api.Application.Credentials;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Docker;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Integration;

/// <summary>
/// Rotating the administrator password of real PostgreSQL and MySQL containers: the whole
/// application with the real provisioner, secret store, managers and dump programs. Opt-in: see
/// <see cref="DockerFactAttribute"/>. Each test uses its own network and removes everything it created.
/// </summary>
/// <remarks>
/// <para>
/// What the server accepts is found out the way a client finds out: by connecting to it over the
/// Docker network, with the engine's own driver, from this process. Inside a container that would
/// prove nothing for PostgreSQL, whose image trusts connections from the container itself.
/// </para>
/// <para>
/// No password is ever written anywhere by these tests: not to the output, not into an assertion's
/// message. Assertions about passwords compare them and report a sentence.
/// </para>
/// </remarks>
[Trait("Category", "DockerIntegration")]
public sealed class CredentialRotationIntegrationTests : IAsyncLifetime
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable(DockerFactAttribute.EnableVariable) == "1";
    private static readonly bool InContainer = File.Exists("/.dockerenv");
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddHours(-1);

    // A range of its own, away from the default one and from the other tests'.
    private const int RangeStart = 25450;
    private const int RangeEnd = 25469;

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
            await _docker.Networks.ConnectNetworkAsync(_network, new NetworkConnectParameters { Container = Environment.MachineName });
        }

        // No worker: each test runs the jobs itself, so nothing depends on timing.
        _factory = new ApiFactory
        {
            RealDockerNetwork = _network,
            ConfigureExternalAccess = settings => (settings.PortRangeStart, settings.PortRangeEnd) = (RangeStart, RangeEnd)
        };
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
            await _engine.RemoveContainerAsync(DockerResourceNaming.ReplacedContainerName(instanceId), default);
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

    // --- The whole life of a rotated password -------------------------------------------------

    [DockerFact]
    public Task Postgres_Rotate_TheOldPasswordStopsWorking_TheNewOneIsAurorasOwn_AndEverythingGoesOnWorking() =>
        RotationLifecycleAsync("postgres", "16");

    [DockerFact]
    public Task Mysql_Rotate_TheOldPasswordStopsWorking_TheNewOneIsAurorasOwn_AndEverythingGoesOnWorking() =>
        RotationLifecycleAsync("mysql", "8.4");

    [DockerFact]
    public Task Postgres15_Rotate_Works() => RotatesAndStaysInSyncAsync("postgres", "15");

    [DockerFact]
    public Task Postgres17_Rotate_Works() => RotatesAndStaysInSyncAsync("postgres", "17");

    [DockerFact]
    public Task Mysql80_Rotate_Works() => RotatesAndStaysInSyncAsync("mysql", "8.0");

    private async Task RotatesAndStaysInSyncAsync(string engine, string version)
    {
        var instanceId = await CreateRunningInstanceAsync(engine, version);
        var original = await _factory.AdminPasswordAsync(instanceId);

        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId, attempt: 1);
        var rotated = await _factory.AdminPasswordAsync(instanceId);
        Assert.False(rotated == original, "The password was not replaced.");
        Assert.True(await AcceptsAsync(instanceId, rotated), "The server does not accept the stored password.");
        Assert.False(await AcceptsAsync(instanceId, original), "The server still accepts the old password.");
        await _factory.CreateReadyDatabaseAsync(_client, instanceId, "after_rotation");

        // What is handed out, once, is that password: a client that is given it gets in.
        var given = await RetrieveResultAsync(instanceId, jobId);
        Assert.True(given == rotated, "The one-time result is not the stored password.");
        Assert.True(await AcceptsAsync(instanceId, given), "The server does not accept the password that was handed out.");
        await (await _client.PostAsync(ApiFactory.RotationResultUrl(instanceId, jobId), content: null))
            .AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_RESULT_ALREADY_RETRIEVED");
    }

    // --- What it is for: a client outside Aurora, connecting with what it was handed ------------

    [DockerFact]
    public Task Postgres_ExternalClient_ConnectsThroughThePublishedPort_WithTheOneTimePassword_AndNotWithTheOldOne() =>
        ExternalClientUsesTheOneTimeResultAsync("postgres", "16");

    [DockerFact]
    public Task Mysql_ExternalClient_ConnectsThroughThePublishedPort_WithTheOneTimePassword_AndNotWithTheOldOne() =>
        ExternalClientUsesTheOneTimeResultAsync("mysql", "8.4");

    private async Task ExternalClientUsesTheOneTimeResultAsync(string engine, string version)
    {
        // 1. An instance, with data, published on a host port.
        var instanceId = await CreateRunningInstanceAsync(engine, version);
        var containerName = DockerResourceNaming.ContainerName(instanceId);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "shop");
        await ExecuteAsync(instanceId, "shop", "CREATE TABLE customers (id INT PRIMARY KEY, name VARCHAR(40))");
        await ExecuteAsync(instanceId, "shop", "INSERT INTO customers VALUES (1, 'Ada'), (2, 'Grace')");
        var port = await EnableExternalAccessAsync(instanceId);
        var original = await _factory.AdminPasswordAsync(instanceId);
        Assert.True(await ConnectsFromOutsideAsync(engine, port, original), "The published port does not answer to begin with.");
        var container = await _docker.Containers.InspectContainerAsync(containerName);
        var volume = await _docker.Volumes.InspectAsync(DockerResourceNaming.VolumeName(instanceId));
        var backupBefore = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        // 3-4. Rotate through Aurora, and take the new credential from the one-time result.
        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(jobId);
        await AssertJobCompletedAsync(jobId, attempt: 1);
        var given = await RetrieveResultAsync(instanceId, jobId);

        // 5-7. Host, published port, username and that password get a client in; the old password does not.
        Assert.True(await ConnectsFromOutsideAsync(engine, port, given), "An external client cannot connect with the password it was handed.");
        Assert.False(await ConnectsFromOutsideAsync(engine, port, original), "An external client can still connect with the old password.");
        var after = await _docker.Containers.InspectContainerAsync(containerName);
        Assert.Equal(container.ID, after.ID);
        Assert.Equal(container.State.StartedAt, after.State.StartedAt);
        Assert.Equal(volume.CreatedAt, (await _docker.Volumes.InspectAsync(DockerResourceNaming.VolumeName(instanceId))).CreatedAt);

        // 8. Aurora's own work goes on, with the same password it handed out.
        await _factory.CreateCompletedBackupAsync(_client, databaseId);
        await _factory.ProcessJobAsync(await _client.DeleteDatabaseAsync(await _factory.CreateReadyDatabaseAsync(_client, instanceId, "scratch")));
        var restoreJobId = await ApiFactory.RequestRestoreAsync(_client, backupBefore);
        await _factory.ProcessJobAsync(restoreJobId);
        await AssertJobCompletedAsync(restoreJobId, attempt: 1);
        Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(instanceId, "shop", "SELECT COUNT(*) FROM customers")));

        // 9-10. The server is restarted and the instance reconciled: the password is still the one handed out.
        await _docker.Containers.RestartContainerAsync(containerName, new ContainerRestartParameters { WaitBeforeKillSeconds = 30 });
        await WaitUntilAsync(() => ConnectsFromOutsideAsync(engine, port, given));
        await _factory.ReconcileAsync();
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
        Assert.True(await ConnectsFromOutsideAsync(engine, port, given), "After a restart and reconciliation the handed-out password no longer works.");
        Assert.False(await ConnectsFromOutsideAsync(engine, port, original), "After a restart the old password works again.");

        // 11-12. The container is replaced, twice, by the external-access flow: still that password.
        Assert.Equal(HttpStatusCode.OK, (await _client.DeleteAsync($"{InstancesUrl}/{instanceId}/external-access")).StatusCode);
        port = await EnableExternalAccessAsync(instanceId);
        Assert.NotEqual(container.ID, (await _docker.Containers.InspectContainerAsync(containerName)).ID);
        await WaitUntilAsync(() => ConnectsFromOutsideAsync(engine, port, given));
        Assert.False(await ConnectsFromOutsideAsync(engine, port, original), "The replaced container accepts the old password.");
        Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(instanceId, "shop", "SELECT COUNT(*) FROM customers")));

        // 13. Rotate again: the new one-time result works, and the one before it has stopped working.
        var secondJobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(secondJobId);
        await AssertJobCompletedAsync(secondJobId, attempt: 1);
        var givenAgain = await RetrieveResultAsync(instanceId, secondJobId);
        Assert.False(givenAgain == given, "The second rotation handed out the first password.");
        Assert.True(await ConnectsFromOutsideAsync(engine, port, givenAgain), "An external client cannot connect with the second password it was handed.");
        Assert.False(await ConnectsFromOutsideAsync(engine, port, given), "An external client can still connect with the password before last.");
        await _factory.CreateCompletedBackupAsync(_client, databaseId);

        // Handed out in those two responses, and in nothing else that was said or logged.
        foreach (var secret in new[] { original, given, givenAgain })
        {
            Assert.False(_factory.Logs.Entries.Any(entry => entry.Contains(secret, StringComparison.Ordinal)), "A log entry carries a password.");
            foreach (var url in new[] { $"{InstancesUrl}/{instanceId}/credentials", $"{JobsUrl}/{jobId}", $"{JobsUrl}/{secondJobId}", $"{InstancesUrl}/{instanceId}/connection" })
            {
                Assert.False((await _client.GetStringAsync(url)).Contains(secret, StringComparison.Ordinal), "A response other than the one-time result carries a password.");
            }
        }
    }

    private async Task RotationLifecycleAsync(string engine, string version)
    {
        var instanceId = await CreateRunningInstanceAsync(engine, version);
        var containerName = DockerResourceNaming.ContainerName(instanceId);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "shop");
        await ExecuteAsync(instanceId, "shop", "CREATE TABLE customers (id INT PRIMARY KEY, name VARCHAR(40))");
        await ExecuteAsync(instanceId, "shop", "INSERT INTO customers VALUES (1, 'Ada'), (2, 'Grace')");
        var firstBackupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        var original = await _factory.AdminPasswordAsync(instanceId);
        Assert.True(await AcceptsAsync(instanceId, original), "The server does not accept the password it was provisioned with.");
        var before = await _docker.Containers.InspectContainerAsync(containerName);
        var volumeBefore = await _docker.Volumes.InspectAsync(DockerResourceNaming.VolumeName(instanceId));

        // --- Rotate ---
        var response = await _client.PostAsync($"{InstancesUrl}/{instanceId}/credentials/rotate", content: null);
        var accepted = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var jobId = System.Text.Json.JsonDocument.Parse(accepted).RootElement.GetProperty("job").GetProperty("id").GetGuid();
        await _factory.ProcessJobAsync(jobId);
        await AssertJobCompletedAsync(jobId, attempt: 1);

        var rotated = await _factory.AdminPasswordAsync(instanceId);
        Assert.False(rotated == original, "The password was not replaced.");
        Assert.True(await AcceptsAsync(instanceId, rotated), "The server does not accept the stored password.");
        Assert.False(await AcceptsAsync(instanceId, original), "The server still accepts the old password.");
        Assert.Null(await _factory.AdminPasswordReplacementAsync(instanceId));

        if (engine == "mysql")
        {
            // The container's environment still has the password the server was initialized with.
            // It no longer opens either of the administrator's accounts: the one for connections
            // from anywhere, and the one for connections over the server's own socket.
            Assert.NotEqual(0, await ExecAsync(instanceId, "mysql -h 127.0.0.1 -uroot -p\"$MYSQL_ROOT_PASSWORD\" -e 'select 1' >/dev/null 2>&1"));
            Assert.NotEqual(0, await ExecAsync(instanceId, "mysql -uroot -p\"$MYSQL_ROOT_PASSWORD\" -e 'select 1' >/dev/null 2>&1"));
        }

        // The same container, never stopped, on the same volume, with the data it had.
        var after = await _docker.Containers.InspectContainerAsync(containerName);
        Assert.Equal(before.ID, after.ID);
        Assert.Equal(before.State.StartedAt, after.State.StartedAt);
        Assert.Equal(0, after.RestartCount);
        Assert.Equal(before.Config.Env, after.Config.Env);
        Assert.Equal(volumeBefore.CreatedAt, (await _docker.Volumes.InspectAsync(DockerResourceNaming.VolumeName(instanceId))).CreatedAt);
        Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(instanceId, "shop", "SELECT COUNT(*) FROM customers")));

        // --- Everything Aurora does with the password still works ---
        var health = await (await _client.GetAsync(InstanceHealthUrl(instanceId))).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal("healthy", health.Status());

        var scratchId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "scratch");
        await _factory.ProcessJobAsync(await _client.DeleteDatabaseAsync(scratchId));
        await (await _client.GetAsync($"{DatabasesUrl}/{scratchId}")).AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");

        await ExecuteAsync(instanceId, "shop", "INSERT INTO customers VALUES (3, 'Edsger')");
        var secondBackupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        // A backup made before the rotation is restored after it: the data comes back, and the
        // password does not. A dump holds a database, not the server's accounts.
        var restoreJobId = await ApiFactory.RequestRestoreAsync(_client, firstBackupId);
        await _factory.ProcessJobAsync(restoreJobId);
        await AssertJobCompletedAsync(restoreJobId, attempt: 1);
        Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(instanceId, "shop", "SELECT COUNT(*) FROM customers")));
        Assert.True(await AcceptsAsync(instanceId, rotated), "Restoring a backup changed the administrator's password.");
        Assert.False(await AcceptsAsync(instanceId, original), "Restoring a backup brought the old password back.");

        restoreJobId = await ApiFactory.RequestRestoreAsync(_client, secondBackupId);
        await _factory.ProcessJobAsync(restoreJobId);
        await AssertJobCompletedAsync(restoreJobId, attempt: 1);
        Assert.Equal(3L, Convert.ToInt64(await ScalarAsync(instanceId, "shop", "SELECT COUNT(*) FROM customers")));

        // --- The server is restarted, and reconciled ---
        await _docker.Containers.RestartContainerAsync(containerName, new ContainerRestartParameters { WaitBeforeKillSeconds = 30 });
        await WaitUntilAsync(async () => await TryAcceptsAsync(instanceId, rotated) == true);
        Assert.False(await AcceptsAsync(instanceId, original), "Restarting the server brought the old password back.");

        await _factory.ReconcileAsync();
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == rotated, "Reconciliation changed the stored password.");
        Assert.Equal(before.ID, (await _docker.Containers.InspectContainerAsync(containerName)).ID);
        Assert.True(await AcceptsAsync(instanceId, rotated), "Reconciliation changed the server's password.");

        // --- The container is replaced, for external access, on the same volume ---
        var enabled = await _client.PostAsync($"{InstancesUrl}/{instanceId}/external-access", content: null);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var replaced = await _docker.Containers.InspectContainerAsync(containerName);
        Assert.NotEqual(before.ID, replaced.ID);
        Assert.True(await AcceptsAsync(instanceId, rotated), "The replaced container's server does not accept the stored password.");
        Assert.False(await AcceptsAsync(instanceId, original), "The replaced container's server accepts the old password.");
        Assert.Equal(3L, Convert.ToInt64(await ScalarAsync(instanceId, "shop", "SELECT COUNT(*) FROM customers")));
        await _factory.CreateCompletedBackupAsync(_client, databaseId);

        // --- And a second rotation works like the first ---
        var secondJobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(secondJobId);
        await AssertJobCompletedAsync(secondJobId, attempt: 1);
        var again = await _factory.AdminPasswordAsync(instanceId);
        Assert.True(await AcceptsAsync(instanceId, again), "The server does not accept the stored password.");
        Assert.False(await AcceptsAsync(instanceId, rotated), "The server still accepts the password before last.");

        // --- None of it was ever shown ---
        var shown = new List<string> { accepted };
        foreach (var url in new[]
                 {
                     $"{InstancesUrl}/{instanceId}/credentials", $"{InstancesUrl}/{instanceId}", $"{InstancesUrl}/{instanceId}/connection",
                     $"{DatabasesUrl}/{databaseId}/connection", $"{JobsUrl}/{jobId}", $"{JobsUrl}/{secondJobId}", JobsUrl
                 })
        {
            shown.Add(await _client.GetStringAsync(url));
        }

        foreach (var secret in new[] { original, rotated, again })
        {
            Assert.False(shown.Any(body => body.Contains(secret, StringComparison.Ordinal)), "A response carries a password.");
            Assert.False(_factory.Logs.Entries.Any(entry => entry.Contains(secret, StringComparison.Ordinal)), "A log entry carries a password.");
        }
    }

    // --- When something goes wrong in the middle ------------------------------------------------

    [DockerFact]
    public Task Postgres_StoreFailsAfterTheServerChanged_NothingIsLost_AndTheNextRotationFinishesIt() =>
        StoreFailureIsRecoveredAsync("postgres", "16");

    [DockerFact]
    public Task Mysql_StoreFailsAfterTheServerChanged_NothingIsLost_AndTheNextRotationFinishesIt() =>
        StoreFailureIsRecoveredAsync("mysql", "8.4");

    private async Task StoreFailureIsRecoveredAsync(string engine, string version)
    {
        var instanceId = await CreateRunningInstanceAsync(engine, version);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "shop");
        var original = await _factory.AdminPasswordAsync(instanceId);
        _factory.SecretFaults.FailNextPromotions(int.MaxValue);

        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(jobId);

        // Not success: the job says the store failed, and the two passwords are known to differ.
        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal("CREDENTIAL_ROTATION_SECRET_STORE_FAILED", job.GetProperty("error").GetProperty("code").GetString());
        var replacement = await _factory.AdminPasswordReplacementAsync(instanceId);
        Assert.NotNull(replacement);
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == original, "The stored password changed although storing failed.");
        Assert.True(await AcceptsAsync(instanceId, replacement), "The server does not accept the replacement that was kept.");
        Assert.False(await AcceptsAsync(instanceId, original), "The server still accepts the old password.");
        Assert.Equal("incomplete", (await (await _client.GetAsync($"{InstancesUrl}/{instanceId}/credentials")).ReadJsonAsync(HttpStatusCode.OK)).GetProperty("rotation").GetString());

        // The store works again. The next rotation does not make another password: it finishes this one.
        _factory.SecretFaults.FailNextPromotions(0);
        var secondJobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(secondJobId);

        await AssertJobCompletedAsync(secondJobId, attempt: 1);
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == replacement, "The rotation did not finish with the replacement it had.");
        Assert.True(await AcceptsAsync(instanceId, replacement), "The server does not accept the stored password.");
        await _factory.CreateCompletedBackupAsync(_client, databaseId);
    }

    [DockerFact]
    public Task Postgres_ProcessDiesAfterTheServerChanged_RecoveryFinishesWithTheSamePassword() =>
        InterruptedRotationIsRecoveredAsync("postgres", "16");

    [DockerFact]
    public Task Mysql_ProcessDiesAfterTheServerChanged_RecoveryFinishesWithTheSamePassword() =>
        InterruptedRotationIsRecoveredAsync("mysql", "8.4");

    private async Task InterruptedRotationIsRecoveredAsync(string engine, string version)
    {
        var instanceId = await CreateRunningInstanceAsync(engine, version);
        var original = await _factory.AdminPasswordAsync(instanceId);
        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        var replacement = (await _factory.AdminPasswordReplacementAsync(instanceId))!;

        // An execution claims the job, changes the server's password, and dies before it can
        // store it: the job is left running under a lease nobody renews.
        await WithManagerAsync(instanceId, (manager, instance) => manager.ChangeAdminPasswordAsync(instance, original, replacement, default));
        await _factory.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);
        Assert.False(await AcceptsAsync(instanceId, await _factory.AdminPasswordAsync(instanceId)), "The stored password should be out of date at this point.");

        Assert.Equal([jobId], await _factory.RecoverJobsAsync(includePending: false));
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId, attempt: 1);
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == replacement, "Recovery made up another password.");
        Assert.True(await AcceptsAsync(instanceId, replacement), "The server does not accept the stored password.");
        Assert.False(await AcceptsAsync(instanceId, original), "The server still accepts the old password.");
    }

    [DockerFact]
    public async Task Postgres_ServerStopped_RotationFailsSafely_StartsNothing_AndWorksOnceTheServerIsBack()
    {
        var instanceId = await CreateRunningInstanceAsync("postgres", "16");
        var containerName = DockerResourceNaming.ContainerName(instanceId);
        var original = await _factory.AdminPasswordAsync(instanceId);
        await _docker.Containers.StopContainerAsync(containerName, new ContainerStopParameters { WaitBeforeKillSeconds = 30 });

        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal("DATABASE_ENGINE_UNAVAILABLE", job.GetProperty("error").GetProperty("code").GetString());
        // A rotation never starts an instance.
        Assert.False((await _docker.Containers.InspectContainerAsync(containerName)).State.Running);
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == original, "The stored password changed although nothing was rotated.");

        await _docker.Containers.StartContainerAsync(containerName, new ContainerStartParameters());
        await WaitUntilAsync(async () => await TryAcceptsAsync(instanceId, original) == true);

        var secondJobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(secondJobId);
        await AssertJobCompletedAsync(secondJobId, attempt: 1);
        Assert.True(await AcceptsAsync(instanceId, await _factory.AdminPasswordAsync(instanceId)), "The server does not accept the stored password.");
        Assert.False(await AcceptsAsync(instanceId, original), "The server still accepts the old password.");
    }

    [DockerFact]
    public Task Postgres_RotationsRequestedAtOnce_OneIsCarriedOut_AndThePasswordsAgree() =>
        ConcurrentRotationsAgreeAsync("postgres", "16");

    [DockerFact]
    public Task Mysql_RotationsRequestedAtOnce_OneIsCarriedOut_AndThePasswordsAgree() =>
        ConcurrentRotationsAgreeAsync("mysql", "8.4");

    private async Task ConcurrentRotationsAgreeAsync(string engine, string version)
    {
        var instanceId = await CreateRunningInstanceAsync(engine, version);
        var original = await _factory.AdminPasswordAsync(instanceId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(
            () => _client.PostAsync($"{InstancesUrl}/{instanceId}/credentials/rotate", content: null))));

        var accepted = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        Assert.All(responses.Where(response => response != accepted), response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));
        var jobId = (await accepted.ReadJsonAsync()).GetProperty("job").GetProperty("id").GetGuid();

        // Two executions go for the one job, as two workers would.
        await Task.WhenAll(_factory.ProcessJobAsync(jobId), _factory.ProcessJobAsync(jobId));

        await AssertJobCompletedAsync(jobId, attempt: 1);
        var rotated = await _factory.AdminPasswordAsync(instanceId);
        Assert.True(await AcceptsAsync(instanceId, rotated), "The server does not accept the stored password.");
        Assert.False(await AcceptsAsync(instanceId, original), "The server still accepts the old password.");
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

    /// <summary>The new password of a completed rotation, from the one response that has it.</summary>
    private async Task<string> RetrieveResultAsync(Guid instanceId, Guid jobId)
    {
        var response = await _client.PostAsync(ApiFactory.RotationResultUrl(instanceId, jobId), content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal((await InstanceAsync(instanceId)).Engine == InstanceEngine.Postgres ? "postgres" : "root", body.GetProperty("username").GetString());
        return body.GetProperty("password").GetString()!;
    }

    private async Task<int> EnableExternalAccessAsync(Guid instanceId)
    {
        var response = await _client.PostAsync($"{InstancesUrl}/{instanceId}/external-access", content: null);
        var body = await response.ReadJsonAsync(HttpStatusCode.OK);
        return body.GetProperty("external").GetProperty("port").GetInt32();
    }

    /// <summary>
    /// Whether the engine's own client, in a container of its own on the host's network, gets in
    /// through the published port as the administrator with that password: what a client outside
    /// Aurora and outside the instance network does. The password travels in the client's own
    /// environment variable, and nothing the client prints is kept.
    /// </summary>
    private async Task<bool> ConnectsFromOutsideAsync(string engine, int port, string password)
    {
        var name = $"aurora-db-test-client-{Guid.NewGuid():N}";
        var (image, variable, command) = engine == "postgres"
            ? ("postgres:16", "PGPASSWORD", $"psql -h 127.0.0.1 -p {port} -U postgres -d postgres -Atc 'select 1' >/dev/null 2>&1")
            : ("mysql:8.4", "MYSQL_PWD", $"mysql -h 127.0.0.1 -P {port} -uroot --connect-timeout=10 -e 'select 1' >/dev/null 2>&1");
        try
        {
            await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = name,
                Image = image,
                Entrypoint = ["sh", "-c", command],
                Env = [$"{variable}={password}", "PGCONNECT_TIMEOUT=10"],
                HostConfig = new HostConfig { NetworkMode = "host" }
            });
            await _docker.Containers.StartContainerAsync(name, new ContainerStartParameters());
            return (await _docker.Containers.WaitContainerAsync(name)).StatusCode == 0;
        }
        finally
        {
            await _engine.RemoveContainerAsync(name, default);
        }
    }

    private async Task AssertJobCompletedAsync(Guid jobId, int attempt)
    {
        var job = await _client.GetJobAsync(jobId);
        Assert.True(job.Status() == "completed", $"Job is {job.Status()}: {job.GetProperty("error").GetRawText()}");
        Assert.Equal(attempt, job.GetProperty("attempt").GetInt32());
    }

    private Task<Instance> InstanceAsync(Guid instanceId) =>
        _factory.WithDbAsync(db => db.Instances.AsNoTracking().SingleAsync(i => i.Id == instanceId));

    /// <summary>Calls the instance's real credential manager directly, the way a job attempt would.</summary>
    private async Task WithManagerAsync(Guid instanceId, Func<IAdminCredentialManager, Instance, Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var instance = await InstanceAsync(instanceId);
        var manager = scope.ServiceProvider.GetServices<IAdminCredentialManager>().Single(candidate => candidate.Engine == instance.Engine);
        await action(manager, instance);
    }

    /// <summary>A connection of the engine's driver to the instance's server, as its administrator with that password. Not opened.</summary>
    private async Task<DbConnection> ConnectionAsync(Guid instanceId, string password, string? database = null)
    {
        var instance = await InstanceAsync(instanceId);
        var endpoint = await _factory.Services.GetRequiredService<IInstanceEndpointResolver>().ResolveAsync(instance, default);

        return instance.Engine == InstanceEngine.Postgres
            ? new NpgsqlConnection(new NpgsqlConnectionStringBuilder
            {
                Host = endpoint.Host,
                Port = endpoint.Port,
                Username = "postgres",
                Password = password,
                Database = database ?? "postgres",
                Timeout = 10,
                Pooling = false
            }.ConnectionString)
            : new MySqlConnection(new MySqlConnectionStringBuilder
            {
                Server = endpoint.Host,
                Port = (uint)endpoint.Port,
                UserID = "root",
                Password = password,
                Database = database ?? string.Empty,
                ConnectionTimeout = 10,
                Pooling = false
            }.ConnectionString);
    }

    /// <summary>
    /// Whether the server lets its administrator in with that password; null if it could not be
    /// asked. Found out with a driver connection of this test's own, not with anything of Aurora's.
    /// </summary>
    private async Task<bool?> TryAcceptsAsync(Guid instanceId, string password)
    {
        try
        {
            await using var connection = await ConnectionAsync(instanceId, password);
            await connection.OpenAsync();
            return true;
        }
        catch (PostgresException exception) when (exception.SqlState is "28P01" or "28000")
        {
            return false;
        }
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.AccessDenied)
        {
            return false;
        }
        catch (Exception exception) when (exception is DbException or DatabaseOperationException or IOException or TimeoutException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<bool> AcceptsAsync(Guid instanceId, string password) =>
        await TryAcceptsAsync(instanceId, password) ?? throw new InvalidOperationException("The database server could not be asked.");

    /// <summary>Runs a query as the administrator, with the password Aurora has stored: the way Aurora itself gets in.</summary>
    private async Task<object?> ScalarAsync(Guid instanceId, string database, string sql)
    {
        await using var connection = await ConnectionAsync(instanceId, await _factory.AdminPasswordAsync(instanceId), database);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private Task ExecuteAsync(Guid instanceId, string database, string sql) => ScalarAsync(instanceId, database, sql);

    private Task<int> ExecAsync(Guid instanceId, string shellCommand) =>
        _engine.ExecAsync(DockerResourceNaming.ContainerName(instanceId), ["sh", "-c", shellCommand], default);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
        }
    }
}
