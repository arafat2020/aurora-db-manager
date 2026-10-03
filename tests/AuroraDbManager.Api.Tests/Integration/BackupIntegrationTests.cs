using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using AuroraDbManager.Api.Infrastructure.Backups;
using AuroraDbManager.Api.Infrastructure.Docker;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Integration;

/// <summary>
/// Backups of real PostgreSQL and MySQL containers: the whole application with the real backup
/// managers running the real <c>pg_dump</c> and <c>mysqldump</c>, writing to a real directory.
/// Opt-in: see <see cref="DockerFactAttribute"/>. Each test uses its own network and backup
/// directory and removes everything it created.
/// </summary>
/// <remarks>
/// Like the database managers, the dump programs connect to the container's address on the
/// Docker network; no port is published. The tests have to run where that network is reachable
/// and where the two programs are installed, which <c>tests/run-docker-integration-tests.sh</c>
/// takes care of. No restore is involved: an artifact is checked by reading it, with
/// <c>pg_restore --list</c> for PostgreSQL and as text for MySQL.
/// </remarks>
[Trait("Category", "DockerIntegration")]
[Collection(BackupIntegrationCollection.Name)]
public sealed class BackupIntegrationTests : IAsyncLifetime
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable(DockerFactAttribute.EnableVariable) == "1";
    private static readonly bool InContainer = File.Exists("/.dockerenv");

    private const string Psql = "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -v ON_ERROR_STOP=1";
    private const string Mysql = "mysql -h 127.0.0.1 -uroot -p\"$MYSQL_ROOT_PASSWORD\"";

    private readonly string _network = $"aurora-db-test-{Guid.NewGuid():N}";
    private readonly List<Guid> _instanceIds = [];
    private readonly SystemProcessRunner _processes = new();
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

    // --- PostgreSQL ---------------------------------------------------------------------------

    [DockerFact]
    public async Task Postgres_Backup_IsACustomFormatArchive_OfTheDatabasesSchemaAndData()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync("postgres", "16", "shop");
        await PostgresAsync(instanceId, "shop", "create table customers (id int primary key, name text); insert into customers values (1, 'alice'), (2, 'bob');");

        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId);
        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("completed", backup.Status());

        var path = _factory.BackupFilePath(instanceId, databaseId, backupId, "dump");
        Assert.Equal([path], _factory.BackupFiles());
        Assert.Equal(new FileInfo(path).Length, backup.GetProperty("sizeBytes").GetInt64());
        AssertPrivate(path);

        // The archive is one pg_restore can read, and holds the table and its rows.
        var listing = await RunAsync("pg_restore", "--list", path);
        Assert.Contains("TABLE public customers", listing);
        Assert.Contains("TABLE DATA public customers", listing);
        var data = await RunAsync("pg_restore", "--data-only", "--file=-", path);
        Assert.Contains("alice", data);
        Assert.Contains("bob", data);
    }

    [DockerFact]
    public async Task Postgres_RepeatedBackup_ProducesASecondArtifact_WithTheNewData_AndKeepsTheFirst()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync("postgres", "16", "shop");
        await PostgresAsync(instanceId, "shop", "create table customers (name text); insert into customers values ('alice');");
        var first = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var firstPath = _factory.BackupFilePath(instanceId, databaseId, first, "dump");
        var firstBytes = await File.ReadAllBytesAsync(firstPath);

        await PostgresAsync(instanceId, "shop", "insert into customers values ('carol');");
        var second = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        var secondPath = _factory.BackupFilePath(instanceId, databaseId, second, "dump");
        Assert.Equal(new[] { firstPath, secondPath }.Order(), _factory.BackupFiles().Order());
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(firstPath));
        Assert.DoesNotContain("carol", await RunAsync("pg_restore", "--data-only", "--file=-", firstPath));
        Assert.Contains("carol", await RunAsync("pg_restore", "--data-only", "--file=-", secondPath));

        var list = await (await _client.GetAsync(DatabaseBackupsUrl(databaseId))).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal([second, first], list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
    }

    [DockerFact]
    public async Task Postgres_Backup_WhileAClientIsConnectedAndInATransaction_Succeeds_WithACommittedSnapshot()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync("postgres", "16", "shop");
        await PostgresAsync(instanceId, "shop", "create table customers (name text); insert into customers values ('alice');");

        // A client in the middle of a transaction, for longer than the test runs.
        var session = ExecAsync(instanceId, $"{Psql} -d shop -c \"begin; insert into customers values ('uncommitted'); select pg_sleep(120);\"");
        await WaitUntilAsync(async () => await ExecAsync(
            instanceId, $"{Psql} -tAc \"select count(*) from pg_stat_activity where datname = 'shop' and state = 'active'\" | grep -qx 1") == 0);

        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        var data = await RunAsync("pg_restore", "--data-only", "--file=-", _factory.BackupFilePath(instanceId, databaseId, backupId, "dump"));
        Assert.Contains("alice", data);
        Assert.DoesNotContain("uncommitted", data);
        // The client was not disturbed.
        Assert.False(session.IsCompleted);
    }

    // --- MySQL --------------------------------------------------------------------------------

    [DockerFact]
    public async Task Mysql_Backup_IsACompleteSqlDump_OfTheDatabasesSchemaAndData()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync("mysql", "8.4", "shop");
        await MysqlAsync(instanceId, "shop", "create table customers (id int primary key, name varchar(50)); insert into customers values (1, 'alice'), (2, 'bob');");

        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId);
        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("completed", backup.Status());

        var path = _factory.BackupFilePath(instanceId, databaseId, backupId, "sql");
        Assert.Equal([path], _factory.BackupFiles());
        Assert.Equal(new FileInfo(path).Length, backup.GetProperty("sizeBytes").GetInt64());
        AssertPrivate(path);

        var dump = await File.ReadAllTextAsync(path);
        Assert.Contains("CREATE TABLE `customers`", dump);
        Assert.Contains("'alice'", dump);
        Assert.Contains("'bob'", dump);
        Assert.Contains("-- Dump completed", dump);
        // It can be loaded into any database: it neither creates nor selects one.
        Assert.DoesNotContain("CREATE DATABASE", dump);
        Assert.DoesNotContain("USE `shop`", dump);
    }

    [DockerFact]
    public async Task Mysql_RepeatedBackup_WhileAClientIsConnected_Succeeds_WithTheNewData()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync("mysql", "8.4", "shop");
        await MysqlAsync(instanceId, "shop", "create table customers (name varchar(50)) engine=InnoDB; insert into customers values ('alice');");
        var first = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        var session = ExecAsync(instanceId, $"{Mysql} shop -e \"begin; insert into customers values ('uncommitted'); select sleep(120);\"");
        await WaitUntilAsync(async () => await ExecAsync(
            instanceId, $"{Mysql} -N -B -e \"select count(*) from information_schema.processlist where db = 'shop' and info like '%sleep(120)%' and id <> connection_id()\" | grep -qx 1") == 0);
        await MysqlAsync(instanceId, "shop", "insert into customers values ('carol');");

        var second = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        var firstDump = await File.ReadAllTextAsync(_factory.BackupFilePath(instanceId, databaseId, first, "sql"));
        var secondDump = await File.ReadAllTextAsync(_factory.BackupFilePath(instanceId, databaseId, second, "sql"));
        Assert.DoesNotContain("'carol'", firstDump);
        Assert.Contains("'carol'", secondDump);
        Assert.DoesNotContain("uncommitted", secondDump);
        Assert.False(session.IsCompleted);
    }

    // --- Failure ------------------------------------------------------------------------------

    [DockerFact]
    public async Task Postgres_InstanceContainerStopped_BackupFailsSafely_LeavesNoFile_AndTheDatabaseStaysReady()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync("postgres", "16", "shop");
        var containerName = DockerResourceNaming.ContainerName(instanceId);
        await _docker.Containers.StopContainerAsync(containerName, new ContainerStopParameters { WaitBeforeKillSeconds = 30 });

        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("failed", backup.Status());
        Assert.Equal("BACKUP_DATABASE_UNAVAILABLE", backup.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_factory.BackupFiles());
        Assert.Empty(CredentialFiles());
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        // A backup never starts an instance.
        Assert.False((await _docker.Containers.InspectContainerAsync(containerName)).State.Running);
    }

    [DockerFact]
    public async Task Postgres_DumpProgramFails_BackupFailsWithASafeError_AndNothingIsLeftBehind()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync("postgres", "16", "shop");
        // Dropped behind the application's back: pg_dump connects to the server and then fails.
        await PostgresAsync(instanceId, "postgres", "drop database shop;");

        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        await _factory.ProcessJobAsync(jobId);

        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("failed", backup.Status());
        Assert.Equal("BACKUP_DATABASE_UNAVAILABLE", backup.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("pg_dump:", backup.GetRawText() + (await _client.GetJobAsync(jobId)).GetRawText());
        // Neither an artifact, nor a staging file, nor a credential file.
        Assert.Empty(_factory.BackupFiles());
        Assert.Empty(CredentialFiles());
        // What pg_dump said is in the log.
        Assert.Contains(_factory.Logs.Entries, entry => entry.Contains("does not exist", StringComparison.Ordinal));
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
    }

    [DockerFact]
    public async Task Mysql_DumpProgramFails_BackupFailsWithASafeError_AndNothingIsLeftBehind()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync("mysql", "8.4", "shop");
        await MysqlAsync(instanceId, "mysql", "drop database shop;");

        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        await _factory.ProcessJobAsync(jobId);

        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("failed", backup.Status());
        Assert.Equal("BACKUP_DATABASE_UNAVAILABLE", backup.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_factory.BackupFiles());
        Assert.Empty(CredentialFiles());
    }

    // --- Cancellation -------------------------------------------------------------------------

    [DockerFact]
    public async Task Postgres_CancelledWhilePgDumpRuns_KillsIt_LeavesNoFile_AndTheJobCompletesWhenRunAgain()
    {
        var (instanceId, databaseId) = await CreateDatabaseAsync("postgres", "16", "big");
        // Enough incompressible data for the dump to take a while.
        await PostgresAsync(instanceId, "big", "create table filler as select g as id, md5(g::text) || md5((g * 7)::text) || md5((g * 13)::text) as payload from generate_series(1, 3000000) g;");
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        var path = _factory.BackupFilePath(instanceId, databaseId, backupId, "dump");
        using var shutdown = new CancellationTokenSource();

        var processing = _factory.ProcessJobAsync(jobId, shutdown.Token);
        // pg_dump is running and has started to write.
        await WaitUntilAsync(() => Task.FromResult(File.Exists(path + ".partial") && new FileInfo(path + ".partial").Length > 0));
        Assert.NotEmpty(Process.GetProcessesByName("pg_dump"));
        await shutdown.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);

        Assert.Empty(Process.GetProcessesByName("pg_dump"));
        Assert.Empty(_factory.BackupFiles());
        Assert.Empty(CredentialFiles());
        Assert.Equal("running", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Equal("pending", (await _client.GetJobAsync(jobId)).Status());

        // The interrupted job is run again, as job recovery would have it, and finishes the backup.
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId);
        Assert.Equal([path], _factory.BackupFiles());
        Assert.Contains("TABLE DATA public filler", await RunAsync("pg_restore", "--list", path));
    }

    // --- Security -----------------------------------------------------------------------------

    [DockerFact]
    public async Task Backup_NeedsNoPublishedPort_AndLeaksNoPassword()
    {
        var (postgresId, postgresDatabaseId) = await CreateDatabaseAsync("postgres", "16", "shop");
        var (mysqlId, mysqlDatabaseId) = await CreateDatabaseAsync("mysql", "8.4", "shop");
        var postgresBackup = await _factory.CreateCompletedBackupAsync(_client, postgresDatabaseId);
        var mysqlBackup = await _factory.CreateCompletedBackupAsync(_client, mysqlDatabaseId);

        var passwords = new List<string>();
        foreach (var (instanceId, variable) in new[] { (postgresId, "POSTGRES_PASSWORD="), (mysqlId, "MYSQL_ROOT_PASSWORD=") })
        {
            var container = await _docker.Containers.InspectContainerAsync(DockerResourceNaming.ContainerName(instanceId));
            // The backup reached the server over the Docker network: nothing is published on the host.
            Assert.DoesNotContain(container.NetworkSettings.Ports.Values, bindings => bindings is { Count: > 0 });
            passwords.Add(container.Config.Env.Single(entry => entry.StartsWith(variable, StringComparison.Ordinal))[variable.Length..]);
        }

        var visible = new List<string>(_factory.Logs.Entries);
        foreach (var url in new[]
                 {
                     $"{BackupsUrl}/{postgresBackup}", $"{BackupsUrl}/{mysqlBackup}",
                     DatabaseBackupsUrl(postgresDatabaseId), DatabaseBackupsUrl(mysqlDatabaseId),
                     $"{DatabasesUrl}/{postgresDatabaseId}", $"{InstancesUrl}/{postgresId}", $"{InstancesUrl}/{mysqlId}"
                 })
        {
            visible.Add(await _client.GetStringAsync(url));
        }

        foreach (var jobId in await _factory.WithDbAsync(db => db.Jobs.Select(j => j.Id).ToListAsync()))
        {
            visible.Add((await _client.GetJobAsync(jobId)).GetRawText());
        }

        Assert.All(passwords, password => Assert.DoesNotContain(visible, text => text.Contains(password, StringComparison.Ordinal)));
        // The files that carried the passwords to the programs are gone.
        Assert.Empty(CredentialFiles());
        // And the dumps themselves do not contain the administrator's password.
        Assert.All(_factory.BackupFiles(), file =>
            Assert.All(passwords, password => Assert.DoesNotContain(password, File.ReadAllText(file, System.Text.Encoding.Latin1))));
    }

    // --- Helpers ------------------------------------------------------------------------------

    private async Task<(Guid InstanceId, Guid DatabaseId)> CreateDatabaseAsync(string engine, string version, string name)
    {
        var response = await _client.PostAsync(
            InstancesUrl,
            System.Net.Http.Json.JsonContent.Create(ValidInstanceRequest(engine: engine, version: version, memoryMb: engine == "mysql" ? 1024 : 512, storageGb: 1)));
        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        var instanceId = body.GetProperty("instance").GetProperty("id").GetGuid();
        _instanceIds.Add(instanceId);

        await _factory.ProcessJobAsync(body.GetProperty("job").GetProperty("id").GetGuid());
        Assert.Equal("running", (await _client.GetInstanceAsync(instanceId)).Status());
        return (instanceId, await _factory.CreateReadyDatabaseAsync(_client, instanceId, name));
    }

    private async Task AssertJobCompletedAsync(Guid jobId)
    {
        var job = await _client.GetJobAsync(jobId);
        Assert.True(job.Status() == "completed", $"Job is {job.Status()}: {job.GetProperty("error").GetRawText()}");
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
    }

    private async Task PostgresAsync(Guid instanceId, string database, string sql) =>
        Assert.Equal(0, await ExecAsync(instanceId, $"{Psql} -d {database} -c \"{sql}\""));

    private async Task MysqlAsync(Guid instanceId, string database, string sql) =>
        Assert.Equal(0, await ExecAsync(instanceId, $"{Mysql} {database} -e \"{sql}\""));

    /// <summary>Runs a command with the engine's own client inside the instance's container. Tests only.</summary>
    private Task<int> ExecAsync(Guid instanceId, string shellCommand) =>
        _engine.ExecAsync(DockerResourceNaming.ContainerName(instanceId), ["sh", "-c", shellCommand], default);

    /// <summary>Runs a program here, next to the tests, and returns what it printed.</summary>
    private async Task<string> RunAsync(string executable, params string[] arguments)
    {
        var result = await _processes.RunAsync(
            new ProcessRequest(executable, arguments, new Dictionary<string, string>(), TimeSpan.FromMinutes(2)), default);
        Assert.True(result.ExitCode == 0, $"{executable} exited with {result.ExitCode}: {result.StandardError}");
        return result.StandardOutput;
    }

    private static void AssertPrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    private static string[] CredentialFiles() =>
        Directory.GetFiles(Path.GetTempPath(), "aurora-backup-*.credentials");

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
    }
}

/// <summary>
/// The integration tests that run the dump programs do so one class after the other: they check
/// that no credential file is left in the temporary directory, which they share.
/// </summary>
[CollectionDefinition(Name)]
public sealed class BackupIntegrationCollection
{
    public const string Name = "Backup integration";
}
