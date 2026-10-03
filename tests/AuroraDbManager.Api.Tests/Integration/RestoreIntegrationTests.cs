using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AuroraDbManager.Api.Infrastructure.Docker;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Integration;

/// <summary>
/// Restores into real PostgreSQL and MySQL containers from local backup storage: the whole
/// application with the real backup and restore managers running the real <c>pg_dump</c>,
/// <c>pg_restore</c>, <c>mysqldump</c> and <c>mysql</c>. Opt-in: see <see cref="DockerFactAttribute"/>.
/// Each test uses its own network and directories and removes everything it created.
/// </summary>
/// <remarks>
/// As for backups, the programs connect to the container's address on the Docker network; no
/// port is published, and nothing is done through <c>docker exec</c> by the application. The
/// tests themselves use the engine's client inside the container to put data in and to look at
/// what a restore left, independently of the application. Run with
/// <c>tests/run-docker-integration-tests.sh</c>.
/// </remarks>
[Trait("Category", "DockerIntegration")]
[Collection(BackupIntegrationCollection.Name)]
public sealed class RestoreIntegrationTests : IAsyncLifetime
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable(DockerFactAttribute.EnableVariable) == "1";
    private static readonly bool InContainer = File.Exists("/.dockerenv");
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddHours(-1);

    private const string Psql = "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -v ON_ERROR_STOP=1";
    private const string Mysql = "mysql -h 127.0.0.1 -uroot -p\"$MYSQL_ROOT_PASSWORD\"";

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
    public async Task Postgres_Restore_MakesTheExistingDatabaseContainExactlyWhatTheBackupContains()
    {
        var instanceId = await CreateInstanceAsync("postgres", "16");
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "shop");
        await PostgresAsync(instanceId, "shop",
            "create table customers (id int primary key, name text not null); insert into customers values (1, 'alice'), (2, 'bob');"
            + " create index customers_name on customers (name); create view customer_names as select name from customers;"
            + " create schema reporting; create table reporting.totals (n int); insert into reporting.totals values (2);");
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var artifact = _factory.BackupFilePath(instanceId, databaseId, backupId, "dump");
        var artifactHash = await HashAsync(artifact);

        // Everything a restore has to undo: changed, deleted and added rows; a changed table; and
        // tables, a schema and a large object that did not exist when the backup was made.
        await PostgresAsync(instanceId, "shop",
            "delete from customers where id = 1; update customers set name = 'robert' where id = 2; insert into customers values (3, 'carol');"
            + " alter table customers add column note text; drop view customer_names; drop table reporting.totals;"
            + " create table added_later (id int); insert into added_later values (1);"
            + " create schema added_schema; create table added_schema.t (id int); select lo_create(0);");

        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId);
        // The backup's rows, table definition, index, view and schema are back...
        await AssertPostgresAsync(instanceId, "shop", "select string_agg(id || ':' || name, ',' order by id) from customers", "1:alice,2:bob");
        await AssertPostgresAsync(instanceId, "shop", "select count(*) from information_schema.columns where table_name = 'customers'", "2");
        await AssertPostgresAsync(instanceId, "shop", "select count(*) from pg_indexes where indexname = 'customers_name'", "1");
        await AssertPostgresAsync(instanceId, "shop", "select string_agg(name, ',' order by name) from customer_names", "alice,bob");
        await AssertPostgresAsync(instanceId, "shop", "select n from reporting.totals", "2");
        // ...and nothing that was created after the backup is left.
        await AssertPostgresAsync(instanceId, "shop", "select to_regclass('public.added_later') is null", "t");
        await AssertPostgresAsync(instanceId, "shop", "select count(*) from pg_namespace where nspname = 'added_schema'", "0");
        await AssertPostgresAsync(instanceId, "shop", "select count(*) from pg_largeobject_metadata", "0");
        // The same database, not a new one beside it; what was restored belongs to the administrator.
        await AssertPostgresAsync(instanceId, "postgres", "select string_agg(datname, ',' order by datname) from pg_database where not datistemplate", "postgres,shop");
        await AssertPostgresAsync(instanceId, "shop", "select tableowner from pg_tables where tablename = 'customers'", "postgres");

        // The backup was only read, and nothing of the restore is left on disk.
        Assert.Equal(artifactHash, await HashAsync(artifact));
        Assert.Equal([artifact], _factory.BackupFiles());
        Assert.Empty(_factory.RestoreStagingEntries());
        Assert.Empty(CredentialFiles());
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal("completed", (await _client.GetBackupAsync(backupId)).Status());
    }

    [DockerFact]
    public async Task Postgres_Restore_CanBeRepeated_AndRestoresAnEarlierBackupOverALaterState()
    {
        var instanceId = await CreateInstanceAsync("postgres", "16");
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "shop");
        await PostgresAsync(instanceId, "shop", "create table customers (name text); insert into customers values ('alice');");
        var first = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        await PostgresAsync(instanceId, "shop", "insert into customers values ('bob');");
        var second = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        foreach (var (backupId, expected) in new[] { (first, "alice"), (second, "alice,bob"), (first, "alice"), (first, "alice") })
        {
            var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
            await _factory.ProcessJobAsync(jobId);

            await AssertJobCompletedAsync(jobId);
            await AssertPostgresAsync(instanceId, "shop", "select string_agg(name, ',' order by name) from customers", expected);
        }
    }

    [DockerFact]
    public async Task Postgres_Restore_EndsTheSessionsOfTheTargetDatabase_AndLeavesOtherDatabasesSessionsAlone()
    {
        var instanceId = await CreateInstanceAsync("postgres", "16");
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "shop");
        await _factory.CreateReadyDatabaseAsync(_client, instanceId, "other");
        await PostgresAsync(instanceId, "shop", "create table customers (name text); insert into customers values ('alice');");
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        await PostgresAsync(instanceId, "shop", "delete from customers;");

        // A client of the target database, in a transaction that holds a lock on the table the
        // restore has to drop; and a client of another database of the same instance.
        var blocking = ExecAsync(instanceId, $"{Psql} -d shop -c \"begin; lock table customers in access exclusive mode; select pg_sleep(300);\"");
        var unrelated = ExecAsync(instanceId, $"{Psql} -d other -c \"select pg_sleep(300);\"");
        await WaitUntilAsync(async () => await ExecAsync(
            instanceId, $"{Psql} -tAc \"select count(*) from pg_stat_activity where datname in ('shop', 'other') and state = 'active'\" | grep -qx 2") == 0);

        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId);
        await AssertPostgresAsync(instanceId, "shop", "select string_agg(name, ',') from customers", "alice");
        // The target's client was disconnected, deliberately; the other database's client was not.
        Assert.NotEqual(0, await blocking.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(unrelated.IsCompleted);
        await AssertPostgresAsync(instanceId, "postgres", "select count(*) from pg_stat_activity where datname = 'other' and state = 'active'", "1");
    }

    [DockerFact]
    public async Task Postgres_ArtifactMissing_FailsAsArtifactNotFound_AndTheDatabaseIsLeftExactlyAsItWas()
    {
        var (instanceId, databaseId, backupId) = await PostgresWithBackupThenChangedAsync();
        File.Delete(_factory.BackupFilePath(instanceId, databaseId, backupId, "dump"));

        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        Assert.Equal("RESTORE_ARTIFACT_NOT_FOUND", job.GetProperty("error").GetProperty("code").GetString());
        // Nothing was emptied: the data that was there before the request is still there.
        await AssertPostgresAsync(instanceId, "shop", "select string_agg(name, ',' order by name) from customers", "carol");
        Assert.Empty(_factory.RestoreStagingEntries());
    }

    [DockerFact]
    public async Task Postgres_ArtifactCorrupted_FailsAsArtifactInvalid_BeforeAnythingIsRemovedFromTheDatabase()
    {
        var (instanceId, databaseId, backupId) = await PostgresWithBackupThenChangedAsync();
        var artifact = _factory.BackupFilePath(instanceId, databaseId, backupId, "dump");
        // The recorded size, the archive signature and the format version are intact; the table of
        // contents and everything after it are not.
        var bytes = await File.ReadAllBytesAsync(artifact);
        Array.Fill<byte>(bytes, 0x58, 16, bytes.Length - 16);
        await File.WriteAllBytesAsync(artifact, bytes);

        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal("RESTORE_ARTIFACT_INVALID", job.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("pg_restore:", job.GetRawText());
        await AssertPostgresAsync(instanceId, "shop", "select string_agg(name, ',' order by name) from customers", "carol");
        Assert.Empty(_factory.RestoreStagingEntries());
        Assert.Empty(CredentialFiles());
    }

    [DockerFact]
    public async Task Postgres_RestoreFails_TheJobFailsWithASafeError_AndRestoringAgainSucceeds()
    {
        var (instanceId, _, backupId) = await PostgresWithBackupThenChangedAsync();
        // The database refuses every write: the restore cannot empty it.
        await PostgresAsync(instanceId, "postgres", "alter database shop set default_transaction_read_only = on;");

        var failedJobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await _factory.ProcessJobAsync(failedJobId);

        var failed = await _client.GetJobAsync(failedJobId);
        Assert.Equal("failed", failed.Status());
        Assert.Equal(3, failed.GetProperty("attempt").GetInt32());
        Assert.Equal("RESTORE_PROCESS_FAILED", failed.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("read-only", failed.GetRawText());
        Assert.Empty(_factory.RestoreStagingEntries());
        Assert.Empty(CredentialFiles());

        // The cause removed, a new restore of the same backup goes through.
        await PostgresAsync(instanceId, "postgres", "alter database shop reset default_transaction_read_only;");
        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId);
        await AssertPostgresAsync(instanceId, "shop", "select string_agg(name, ',' order by name) from customers", "alice,bob");
    }

    [DockerFact]
    public async Task Postgres_InstanceContainerStopped_RestoreFailsSafely_AndNothingIsStarted()
    {
        var (instanceId, _, backupId) = await PostgresWithBackupThenChangedAsync();
        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        var containerName = DockerResourceNaming.ContainerName(instanceId);
        await _docker.Containers.StopContainerAsync(containerName, new ContainerStopParameters { WaitBeforeKillSeconds = 30 });

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal("RESTORE_DATABASE_UNAVAILABLE", job.GetProperty("error").GetProperty("code").GetString());
        Assert.False((await _docker.Containers.InspectContainerAsync(containerName)).State.Running);
        Assert.Empty(_factory.RestoreStagingEntries());
    }

    [DockerFact]
    public async Task Postgres_CancelledWhilePgRestoreRuns_KillsIt_LeavesTheDatabaseEmptyNotHalfRestored_AndRunningTheJobAgainRestoresIt()
    {
        var instanceId = await CreateInstanceAsync("postgres", "16");
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "big");
        // Enough data for the load to take a while.
        await PostgresAsync(instanceId, "big", "create table filler as select g as id, md5(g::text) || md5((g * 7)::text) as payload from generate_series(1, 2000000) g; create index filler_payload on filler (payload);");
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        using var shutdown = new CancellationTokenSource();

        var processing = _factory.ProcessJobAsync(jobId, shutdown.Token);
        // The database has been emptied and pg_restore is loading into it.
        await WaitUntilAsync(async () =>
            await ExecAsync(instanceId, $"{Psql} -d big -tAc \"select to_regclass('public.filler') is null\" | grep -qx t") == 0
            && Process.GetProcessesByName("pg_restore").Length > 0);
        await shutdown.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);

        Assert.Empty(Process.GetProcessesByName("pg_restore"));
        Assert.Empty(_factory.RestoreStagingEntries());
        Assert.Empty(CredentialFiles());
        var interrupted = await _client.GetJobAsync(jobId);
        Assert.Equal("pending", interrupted.Status());
        // The load was one transaction and was rolled back: the database is empty, not half loaded.
        await WaitUntilAsync(async () => await ExecAsync(
            instanceId, $"{Psql} -tAc \"select count(*) from pg_stat_activity where datname = 'big'\" | grep -qx 0") == 0);
        await AssertPostgresAsync(instanceId, "big", "select count(*) from pg_tables where schemaname = 'public'", "0");

        // The interrupted job is run again, as job recovery would have it, and restores everything.
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId);
        await AssertPostgresAsync(instanceId, "big", "select count(*) from filler", "2000000");
        await AssertPostgresAsync(instanceId, "big", "select count(*) from pg_indexes where indexname = 'filler_payload'", "1");
    }

    [DockerFact]
    public async Task Postgres_RestoreInterruptedByACrash_IsRecoveredThroughItsLease_AndCompletesWithoutLosingAnAttempt()
    {
        var (instanceId, _, backupId) = await PostgresWithBackupThenChangedAsync();
        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);

        // The process died after it had emptied the database and while it was loading: the job is
        // running under a lease nobody renews, half a download lies in its directory, and the
        // database holds neither the old data nor the backup's.
        await _factory.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);
        var directory = Path.Combine(_factory.RestoreStagingRoot, jobId.ToString("D"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "artifact.dump.partial"), "PGDMP half a download");
        await PostgresAsync(instanceId, "shop", "drop table customers; create table half_restored (id int);");

        Assert.Equal([jobId], await _factory.RecoverJobsAsync(includePending: false));
        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        await AssertPostgresAsync(instanceId, "shop", "select string_agg(name, ',' order by name) from customers", "alice,bob");
        await AssertPostgresAsync(instanceId, "shop", "select to_regclass('public.half_restored') is null", "t");
        Assert.Empty(_factory.RestoreStagingEntries());
    }

    // --- MySQL --------------------------------------------------------------------------------

    [DockerFact]
    public async Task Mysql_Restore_MakesTheExistingDatabaseContainExactlyWhatTheBackupContains()
    {
        var instanceId = await CreateInstanceAsync("mysql", "8.4");
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "shop");
        await MysqlAsync(instanceId, "shop",
            "create table customers (id int primary key, name varchar(50) not null) engine=InnoDB; insert into customers values (1, 'alice'), (2, 'bob');"
            + " create table orders (id int primary key, customer_id int not null, foreign key (customer_id) references customers (id)) engine=InnoDB; insert into orders values (10, 1);"
            + " create view customer_names as select name from customers;"
            + " create procedure count_customers() select count(*) from customers;");
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var artifact = _factory.BackupFilePath(instanceId, databaseId, backupId, "sql");
        var artifactHash = await HashAsync(artifact);

        await MysqlAsync(instanceId, "shop",
            "delete from orders; delete from customers where id = 1; update customers set name = 'robert' where id = 2; insert into customers values (3, 'carol');"
            + " alter table customers add column note varchar(10); drop view customer_names; drop procedure count_customers;"
            + " create table added_later (id int); create view added_view as select 1 as one;"
            + " create procedure added_procedure() select 1; create function added_function() returns int deterministic return 1;");

        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId);
        // The backup's rows, table definitions, view and procedure are back...
        await AssertMysqlAsync(instanceId, "shop", "select group_concat(concat(id, ':', name) order by id) from customers", "1:alice,2:bob");
        await AssertMysqlAsync(instanceId, "shop", "select count(*) from orders", "1");
        await AssertMysqlAsync(instanceId, "shop", "select count(*) from information_schema.columns where table_schema = 'shop' and table_name = 'customers'", "2");
        await AssertMysqlAsync(instanceId, "shop", "select group_concat(name order by name) from customer_names", "alice,bob");
        await AssertMysqlAsync(instanceId, "shop", "select group_concat(routine_name order by routine_name) from information_schema.routines where routine_schema = 'shop'", "count_customers");
        // ...and nothing that was created after the backup is left.
        await AssertMysqlAsync(instanceId, "shop", "select group_concat(table_name order by table_name) from information_schema.tables where table_schema = 'shop'", "customer_names,customers,orders");
        // The same database, not a new one beside it; the server's own databases untouched.
        await AssertMysqlAsync(instanceId, "mysql", "select group_concat(schema_name order by schema_name) from information_schema.schemata", "information_schema,mysql,performance_schema,shop,sys");

        Assert.Equal(artifactHash, await HashAsync(artifact));
        Assert.Equal([artifact], _factory.BackupFiles());
        Assert.Empty(_factory.RestoreStagingEntries());
        Assert.Empty(CredentialFiles());
    }

    [DockerFact]
    public async Task Mysql_Restore_EndsTheSessionsOfTheTargetDatabase_AndLeavesOtherDatabasesSessionsAlone()
    {
        var instanceId = await CreateInstanceAsync("mysql", "8.4");
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "shop");
        await _factory.CreateReadyDatabaseAsync(_client, instanceId, "other");
        await MysqlAsync(instanceId, "shop", "create table customers (name varchar(50)) engine=InnoDB; insert into customers values ('alice');");
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        await MysqlAsync(instanceId, "shop", "delete from customers;");

        // A client of the target database, in a transaction that has read the table the restore
        // has to drop, which holds a lock on it; and a client of another database of the instance.
        var blocking = ExecAsync(instanceId, $"{Mysql} shop -e \"begin; select * from customers; select sleep(300);\"");
        var unrelated = ExecAsync(instanceId, $"{Mysql} other -e \"select sleep(300);\"");
        await WaitUntilAsync(async () => await ExecAsync(
            instanceId, $"{Mysql} -N -B -e \"select count(*) from information_schema.processlist where db in ('shop', 'other') and info like '%sleep(300)%' and id <> connection_id()\" | grep -qx 2") == 0);

        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId);
        await AssertMysqlAsync(instanceId, "shop", "select group_concat(name) from customers", "alice");
        Assert.NotEqual(0, await blocking.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(unrelated.IsCompleted);
        await AssertMysqlAsync(instanceId, "mysql", "select count(*) from information_schema.processlist where db = 'other' and info like '%sleep(300)%' and id <> connection_id()", "1");
    }

    [DockerFact]
    public async Task Mysql_ArtifactMissingOrCutShort_FailsBeforeAnythingIsRemovedFromTheDatabase()
    {
        var instanceId = await CreateInstanceAsync("mysql", "8.4");
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "shop");
        await MysqlAsync(instanceId, "shop", "create table customers (name varchar(50)); insert into customers values ('alice');");
        var cutShort = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var missing = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        await MysqlAsync(instanceId, "shop", "delete from customers; insert into customers values ('carol');");

        // One dump that lost its end but kept its size, and one that is gone.
        var cutShortPath = _factory.BackupFilePath(instanceId, databaseId, cutShort, "sql");
        var bytes = await File.ReadAllBytesAsync(cutShortPath);
        Array.Fill<byte>(bytes, (byte)' ', bytes.Length - 200, 200);
        await File.WriteAllBytesAsync(cutShortPath, bytes);
        File.Delete(_factory.BackupFilePath(instanceId, databaseId, missing, "sql"));

        foreach (var (backupId, expectedCode) in new[] { (cutShort, "RESTORE_ARTIFACT_INVALID"), (missing, "RESTORE_ARTIFACT_NOT_FOUND") })
        {
            var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
            await _factory.ProcessJobAsync(jobId);

            var job = await _client.GetJobAsync(jobId);
            Assert.Equal("failed", job.Status());
            Assert.Equal(expectedCode, job.GetProperty("error").GetProperty("code").GetString());
            await AssertMysqlAsync(instanceId, "shop", "select group_concat(name) from customers", "carol");
        }

        Assert.Empty(_factory.RestoreStagingEntries());
    }

    [DockerFact]
    public async Task Mysql_RestoreProgramFails_TheJobFailsWithASafeError_TheDatabaseMayBeEmpty_AndAnotherRestoreRepairsIt()
    {
        var instanceId = await CreateInstanceAsync("mysql", "8.4");
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "shop");
        await MysqlAsync(instanceId, "shop", "create table customers (name varchar(50)); insert into customers values ('alice'), ('bob');");
        var broken = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var good = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        // A dump of the right size that ends as a dump should, and is not valid SQL in between:
        // nothing can tell before the mysql client is actually given it.
        var brokenPath = _factory.BackupFilePath(instanceId, databaseId, broken, "sql");
        var script = await File.ReadAllTextAsync(brokenPath);
        Assert.Contains("CREATE TABLE", script);
        await File.WriteAllTextAsync(brokenPath, script.Replace("CREATE TABLE", "CREATE TABXX"));

        var failedJobId = await ApiFactory.RequestRestoreAsync(_client, broken);
        await _factory.ProcessJobAsync(failedJobId);

        var failed = await _client.GetJobAsync(failedJobId);
        Assert.Equal("failed", failed.Status());
        Assert.Equal(3, failed.GetProperty("attempt").GetInt32());
        Assert.Equal("RESTORE_PROCESS_FAILED", failed.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("TABXX", failed.GetRawText());
        Assert.DoesNotContain("ERROR 1064", failed.GetRawText());
        Assert.Contains(_factory.Logs.Entries, entry => entry.Contains("1064", StringComparison.Ordinal));
        Assert.Empty(_factory.RestoreStagingEntries());
        Assert.Empty(CredentialFiles());
        // The residual limitation: the database was emptied before the load failed. Its status is
        // still ready; the failed job is what says so.
        await AssertMysqlAsync(instanceId, "shop", "select count(*) from information_schema.tables where table_schema = 'shop'", "0");
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());

        var jobId = await ApiFactory.RequestRestoreAsync(_client, good);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobCompletedAsync(jobId);
        await AssertMysqlAsync(instanceId, "shop", "select group_concat(name order by name) from customers", "alice,bob");
    }

    [DockerFact]
    public async Task Mysql_RestoreInterruptedByACrash_IsRecoveredThroughItsLease_AndCompletes()
    {
        var instanceId = await CreateInstanceAsync("mysql", "8.4");
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "shop");
        await MysqlAsync(instanceId, "shop", "create table customers (name varchar(50)); insert into customers values ('alice'), ('bob'); create table orders (id int);");
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);

        // The process died half way through the load: one of the backup's tables is there, the
        // other is not, and the job is running under a lease nobody renews.
        await _factory.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);
        await MysqlAsync(instanceId, "shop", "drop table orders; delete from customers; create table half_restored (id int);");

        Assert.Equal([jobId], await _factory.RecoverJobsAsync(includePending: false));
        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        await AssertMysqlAsync(instanceId, "shop", "select group_concat(name order by name) from customers", "alice,bob");
        await AssertMysqlAsync(instanceId, "shop", "select group_concat(table_name order by table_name) from information_schema.tables where table_schema = 'shop'", "customers,orders");
    }

    // --- Security -----------------------------------------------------------------------------

    [DockerFact]
    public async Task Restore_NeedsNoPublishedPort_AndLeaksNoPassword()
    {
        var postgresId = await CreateInstanceAsync("postgres", "16");
        var mysqlId = await CreateInstanceAsync("mysql", "8.4");
        var jobIds = new List<Guid>();
        foreach (var instanceId in new[] { postgresId, mysqlId })
        {
            var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "shop");
            var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
            var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
            await _factory.ProcessJobAsync(jobId);
            await AssertJobCompletedAsync(jobId);
            jobIds.Add(jobId);
        }

        var passwords = new List<string>();
        foreach (var (instanceId, variable) in new[] { (postgresId, "POSTGRES_PASSWORD="), (mysqlId, "MYSQL_ROOT_PASSWORD=") })
        {
            var container = await _docker.Containers.InspectContainerAsync(DockerResourceNaming.ContainerName(instanceId));
            Assert.DoesNotContain(container.NetworkSettings.Ports.Values, bindings => bindings is { Count: > 0 });
            passwords.Add(container.Config.Env.Single(entry => entry.StartsWith(variable, StringComparison.Ordinal))[variable.Length..]);
        }

        var visible = new List<string>(_factory.Logs.Entries);
        foreach (var jobId in jobIds)
        {
            visible.Add((await _client.GetJobAsync(jobId)).GetRawText());
        }

        Assert.All(passwords, password => Assert.DoesNotContain(visible, text => text.Contains(password, StringComparison.Ordinal)));
        Assert.Empty(CredentialFiles());
        Assert.Empty(_factory.RestoreStagingEntries());
    }

    // --- Helpers ------------------------------------------------------------------------------

    private async Task<Guid> CreateInstanceAsync(string engine, string version)
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

    /// <summary>A PostgreSQL database backed up with alice and bob in it, which now holds only carol.</summary>
    private async Task<(Guid InstanceId, Guid DatabaseId, Guid BackupId)> PostgresWithBackupThenChangedAsync()
    {
        var instanceId = await CreateInstanceAsync("postgres", "16");
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "shop");
        await PostgresAsync(instanceId, "shop", "create table customers (name text); insert into customers values ('alice'), ('bob');");
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        await PostgresAsync(instanceId, "shop", "delete from customers; insert into customers values ('carol');");
        return (instanceId, databaseId, backupId);
    }

    private async Task AssertJobCompletedAsync(Guid jobId)
    {
        var job = await _client.GetJobAsync(jobId);
        Assert.True(job.Status() == "completed", $"Job is {job.Status()}: {job.GetProperty("error").GetRawText()}");
    }

    private async Task PostgresAsync(Guid instanceId, string database, string sql) =>
        Assert.Equal(0, await ExecAsync(instanceId, $"{Psql} -d {database} -c \"{sql}\""));

    private async Task MysqlAsync(Guid instanceId, string database, string sql) =>
        Assert.Equal(0, await ExecAsync(instanceId, $"{Mysql} {database} -e \"{sql}\""));

    /// <summary>Asks the server itself, with its own client inside the container, and compares the single value it prints.</summary>
    private async Task AssertPostgresAsync(Guid instanceId, string database, string query, string expected) =>
        Assert.True(
            await ExecAsync(instanceId, $"{Psql} -d {database} -tAc \"{query}\" | grep -qxF -- '{expected}'") == 0,
            $"Expected '{expected}' from: {query}");

    private async Task AssertMysqlAsync(Guid instanceId, string database, string query, string expected) =>
        Assert.True(
            await ExecAsync(instanceId, $"{Mysql} {database} -N -B -e \"{query}\" | grep -qxF -- '{expected}'") == 0,
            $"Expected '{expected}' from: {query}");

    /// <summary>Runs a command with the engine's own client inside the instance's container. Tests only.</summary>
    private Task<int> ExecAsync(Guid instanceId, string shellCommand) =>
        _engine.ExecAsync(DockerResourceNaming.ContainerName(instanceId), ["sh", "-c", shellCommand], default);

    private static async Task<string> HashAsync(string path) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

    private static string[] CredentialFiles() =>
        Directory.GetFiles(Path.GetTempPath(), "aurora-backup-*.credentials");

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
    }
}
