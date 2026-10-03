using System.Text.Json;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Restores;
using AuroraDbManager.Api.Tests.Backups;
using Microsoft.EntityFrameworkCore;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Restores;

/// <summary>
/// The restore_database handler with the real restore managers and the real backup storage, run
/// through the real job processor with <see cref="ApiFactory.ProcessJobAsync"/>. The restore
/// programs and the target database are fakes that record what they were given. Jobs get three
/// attempts with no delay in between.
/// </summary>
public sealed class RestoreJobTests : IDisposable
{
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddHours(-1);

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public RestoreJobTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private FakeDumpTools Tools => _factory.DumpTools;

    private List<string> Sql => _factory.RestoreSql.Statements;

    private async Task<(Guid InstanceId, Guid DatabaseId, Guid BackupId, string ArtifactPath)> CreateBackedUpDatabaseAsync(
        string engine = "postgres", string name = "orders")
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client, name: $"instance-{name}", engine: engine);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, name);
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        return (instanceId, databaseId, backupId, _factory.BackupFilePath(instanceId, databaseId, backupId, engine == "postgres" ? "dump" : "sql"));
    }

    private async Task<string?> JobErrorCodeAsync(Guid jobId)
    {
        var error = (await _client.GetJobAsync(jobId)).GetProperty("error");
        return error.ValueKind == JsonValueKind.Null ? null : error.GetProperty("code").GetString();
    }

    private async Task<JsonElement> RestoreAsync(Guid backupId)
    {
        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await _factory.ProcessJobAsync(jobId);
        return await _client.GetJobAsync(jobId);
    }

    // --- PostgreSQL ---------------------------------------------------------------------------

    [Fact]
    public async Task Postgres_Restore_FetchesAndChecksTheArtifact_ThenEmptiesTheDatabase_LoadsIt_AndVerifies_InThatOrder()
    {
        var (_, databaseId, backupId, artifactPath) = await CreateBackedUpDatabaseAsync();
        var timeline = new List<string>();
        Tools.OnRun = run => timeline.Add(run.Kind);
        _factory.RestoreSql.StatementFailure = sql =>
        {
            timeline.Add(sql switch
            {
                PostgreSqlRestoreManager.TerminateSessionsSql => "sql: end sessions",
                PostgreSqlRestoreManager.SetLockTimeoutSql => "sql: lock timeout",
                PostgreSqlRestoreManager.EmptyDatabaseSql => "sql: empty database",
                "SELECT 1" => "sql: verify",
                _ => $"sql: {sql}"
            });
            return null;
        };

        var job = await RestoreAsync(backupId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("error").ValueKind);
        Assert.Equal(
            [
                // Nothing touches the database until the artifact has been read by pg_restore itself.
                FakeDumpTools.PgRestoreList,
                "sql: end sessions",
                "sql: lock timeout",
                "sql: empty database",
                FakeDumpTools.PgRestore,
                "sql: verify"
            ],
            timeline);

        // No status was touched: the job is the record of the restore.
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal("completed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.True(File.Exists(artifactPath));
    }

    [Fact]
    public async Task Postgres_Restore_RunsPgRestoreIntoTheExistingDatabase_WithoutOwnersPrivilegesOrCreate()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync("postgres", "orders");

        await RestoreAsync(backupId);

        var listing = Assert.Single(Tools.RunsOf(FakeDumpTools.PgRestoreList));
        Assert.Equal("pg_restore", listing.Executable);
        Assert.Equal(["--list", listing.InputPath], listing.Arguments);
        Assert.Empty(listing.Environment);

        var run = Assert.Single(Tools.RunsOf(FakeDumpTools.PgRestore));
        Assert.Equal("pg_restore", run.Executable);
        Assert.Equal(
            [
                "--dbname=orders",
                $"--host={FakeInstanceEndpoints.Host}",
                "--port=5432",
                "--username=postgres",
                "--no-password",
                "--no-owner",
                "--no-privileges",
                "--clean",
                "--if-exists",
                "--single-transaction",
                "--exit-on-error",
                run.InputPath
            ],
            run.Arguments);
        Assert.DoesNotContain("--create", run.Arguments);
        Assert.Equal(["PGCONNECT_TIMEOUT", "PGPASSFILE"], run.Environment.Keys.Order());
        // Six hours by default: longer than a backup is given.
        Assert.Equal(TimeSpan.FromHours(6), run.Timeout);
        Assert.False(run.InputIsStandardInput);

        // What it was given to load is the backup, byte for byte.
        Assert.Equal(FakeDumpTools.PostgresDump, run.InputContent);
    }

    [Fact]
    public async Task Postgres_Restore_EmptiesTheTargetDatabaseItself_AndEndsOnlyThatDatabasesSessions()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync("postgres", "orders");

        await RestoreAsync(backupId);

        // Both connections, to empty and to verify, are to the target database, as the administrator.
        Assert.Equal(2, _factory.RestoreSql.ConnectionStrings.Count);
        Assert.All(_factory.RestoreSql.ConnectionStrings, connectionString =>
        {
            var connection = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
            Assert.Equal("orders", connection["Database"]);
            Assert.Equal(FakeInstanceEndpoints.Host, connection["Host"]);
            Assert.Equal("postgres", connection["Username"]);
            Assert.Equal("false", connection["Pooling"].ToString(), ignoreCase: true);
        });

        Assert.Equal(
            [
                PostgreSqlRestoreManager.TerminateSessionsSql,
                PostgreSqlRestoreManager.SetLockTimeoutSql,
                PostgreSqlRestoreManager.EmptyDatabaseSql,
                "SELECT 1"
            ],
            Sql);

        // Sessions of the connected database only, and never this one.
        Assert.Contains("datname = current_database()", PostgreSqlRestoreManager.TerminateSessionsSql);
        Assert.Contains("pid <> pg_backend_pid()", PostgreSqlRestoreManager.TerminateSessionsSql);
        // The database is emptied in place: no database is dropped or created, and no name comes from outside.
        Assert.Contains("DROP SCHEMA %I CASCADE", PostgreSqlRestoreManager.EmptyDatabaseSql);
        Assert.Contains("CREATE SCHEMA public", PostgreSqlRestoreManager.EmptyDatabaseSql);
        Assert.DoesNotContain("DATABASE", PostgreSqlRestoreManager.EmptyDatabaseSql.ToUpperInvariant().Replace("PG_DATABASE_OWNER", string.Empty));
        Assert.All(Sql, statement => Assert.DoesNotContain("orders", statement));
        Assert.Equal(0, _factory.RestoreSql.OpenConnections);
    }

    // --- MySQL --------------------------------------------------------------------------------

    [Fact]
    public async Task Mysql_Restore_StreamsTheScriptIntoTheSelectedDatabase_AndNoOther()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync("mysql", "orders");
        var timeline = new List<string>();
        Tools.OnRun = run => timeline.Add(run.Kind);
        _factory.RestoreSql.StatementFailure = sql =>
        {
            if (sql is "SET SESSION foreign_key_checks = 0" or "SELECT 1")
            {
                timeline.Add($"sql: {sql}");
            }

            return null;
        };

        var job = await RestoreAsync(backupId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(
            [FakeDumpTools.MySqlVersion, "sql: SET SESSION foreign_key_checks = 0", FakeDumpTools.MySql, "sql: SELECT 1"],
            timeline);

        Assert.Equal(["--version"], Assert.Single(Tools.RunsOf(FakeDumpTools.MySqlVersion)).Arguments);

        var run = Assert.Single(Tools.RunsOf(FakeDumpTools.MySql));
        Assert.Equal("mysql", run.Executable);
        Assert.Equal(
            [
                $"--defaults-extra-file={run.CredentialPath}",
                $"--host={FakeInstanceEndpoints.Host}",
                "--port=3306",
                "--protocol=TCP",
                "--user=root",
                "--connect-timeout=10",
                "--default-character-set=utf8mb4",
                "--one-database",
                "--database=orders"
            ],
            run.Arguments);
        Assert.Empty(run.Environment);

        // The client acts on the named database only once the input has selected it; then comes
        // the script, through standard input, from the file, and it is the backup byte for byte.
        Assert.Equal("USE `orders`;\n", run.StandardInputText);
        Assert.True(run.InputIsStandardInput);
        Assert.Equal(FakeDumpTools.MysqlDump, run.InputContent);
        Assert.DoesNotContain(run.Arguments, argument => argument.Contains(run.InputPath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mysql_Restore_EndsTheDatabasesOtherSessions_AndDropsEverythingInIt_BeforeLoading()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync("mysql", "orders");
        _factory.RestoreSql.Scalars = sql => sql switch
        {
            MySqlRestoreManager.OtherSessionsSql => "41\0not-a-number\052",
            MySqlRestoreManager.ViewsSql => "`open_orders`, `we``ird view`",
            MySqlRestoreManager.TablesSql => "`customers`, `orders`",
            MySqlRestoreManager.RoutinesSql => "PROCEDURE IF EXISTS `archive`\0FUNCTION IF EXISTS `total`",
            MySqlRestoreManager.EventsSql => "EVENT IF EXISTS `nightly`",
            "SELECT 1" => 1,
            _ => null
        };

        var job = await RestoreAsync(backupId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(
            [
                "SET SESSION group_concat_max_len = 4294967295",
                "SET SESSION lock_wait_timeout = 60",
                MySqlRestoreManager.OtherSessionsSql,
                // Only what is a session id is ever put into a statement.
                "KILL 41",
                "KILL 52",
                "SET SESSION foreign_key_checks = 0",
                MySqlRestoreManager.ViewsSql,
                "DROP VIEW IF EXISTS `open_orders`, `we``ird view`",
                MySqlRestoreManager.TablesSql,
                "DROP TABLE IF EXISTS `customers`, `orders`",
                MySqlRestoreManager.RoutinesSql,
                "DROP PROCEDURE IF EXISTS `archive`",
                "DROP FUNCTION IF EXISTS `total`",
                MySqlRestoreManager.EventsSql,
                "DROP EVENT IF EXISTS `nightly`",
                "SELECT 1"
            ],
            Sql);

        // Sessions and objects of the connected database only; the database itself is never dropped.
        Assert.Contains("db = DATABASE()", MySqlRestoreManager.OtherSessionsSql);
        Assert.Contains("id <> CONNECTION_ID()", MySqlRestoreManager.OtherSessionsSql);
        Assert.All(
            new[] { MySqlRestoreManager.ViewsSql, MySqlRestoreManager.TablesSql, MySqlRestoreManager.RoutinesSql, MySqlRestoreManager.EventsSql },
            listing => Assert.Contains("= DATABASE()", listing));
        Assert.DoesNotContain(Sql, statement => statement.Contains("DROP DATABASE", StringComparison.OrdinalIgnoreCase)
            || statement.Contains("CREATE DATABASE", StringComparison.OrdinalIgnoreCase));
        Assert.All(_factory.RestoreSql.ConnectionStrings, connectionString =>
            Assert.Equal("orders", new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString }["Database"]));
    }

    [Fact]
    public async Task Mysql_Restore_OfAnEmptyDatabase_DropsNothing()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync("mysql");

        await RestoreAsync(backupId);

        Assert.DoesNotContain(Sql, statement => statement.StartsWith("DROP", StringComparison.Ordinal) || statement.StartsWith("KILL", StringComparison.Ordinal));
    }

    // --- The artifact -------------------------------------------------------------------------

    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    public async Task Restore_NeverChangesTheBackup_AndLeavesNothingBehind(string engine)
    {
        var (_, _, backupId, artifactPath) = await CreateBackedUpDatabaseAsync(engine);
        var before = await File.ReadAllBytesAsync(artifactPath);
        var writtenAt = File.GetLastWriteTimeUtc(artifactPath);
        var backupBefore = (await _client.GetBackupAsync(backupId)).GetRawText();

        var job = await RestoreAsync(backupId);

        Assert.Equal("completed", job.Status());
        // The artifact is where it was, as it was; the restore worked on a copy.
        Assert.Equal([artifactPath], _factory.BackupFiles());
        Assert.Equal(before, await File.ReadAllBytesAsync(artifactPath));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(artifactPath));
        Assert.Equal(backupBefore, (await _client.GetBackupAsync(backupId)).GetRawText());

        var run = Tools.Runs.Last(candidate => candidate.Kind is FakeDumpTools.PgRestore or FakeDumpTools.MySql);
        Assert.NotEqual(artifactPath, run.InputPath);
        Assert.StartsWith(_factory.RestoreStagingRoot, run.InputPath);
        // The copy, its directory and the credential file are gone.
        Assert.Empty(_factory.RestoreStagingEntries());
        Assert.False(File.Exists(run.CredentialPath));
    }

    [Fact]
    public async Task Restore_StagesTheArtifactInADirectoryOfItsJob_UnderAFinalName_NeverAsPartial()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        Tools.Block(FakeDumpTools.PgRestore);

        var processing = _factory.ProcessJobAsync(jobId);
        await Tools.WaitForRunAsync();

        var directory = Path.Combine(_factory.RestoreStagingRoot, jobId.ToString("D"));
        Assert.Equal(new[] { directory, Path.Combine(directory, "artifact.dump") }.Order(), _factory.RestoreStagingEntries().Order());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(Path.Combine(directory, "artifact.dump")));
            Assert.Equal(OwnerOnlyFile | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        }

        // Kept apart from where backups are staged and stored.
        Assert.Empty(_factory.StagingFiles());
        Assert.Equal("running", (await _client.GetJobAsync(jobId)).Status());

        Tools.Release();
        await processing.WaitAsync(TestTimeout);
        Assert.Empty(_factory.RestoreStagingEntries());
    }

    [Fact]
    public async Task ArtifactMissing_FailsAsArtifactNotFound_AndTheDatabaseIsNeverTouched()
    {
        var (_, databaseId, backupId, artifactPath) = await CreateBackedUpDatabaseAsync();
        File.Delete(artifactPath);

        var job = await RestoreAsync(backupId);

        Assert.Equal("failed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        Assert.Equal("RESTORE_ARTIFACT_NOT_FOUND", job.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(_factory.BackupRoot, job.GetRawText());

        // Nothing was run against the database, and nothing emptied it.
        Assert.Empty(Tools.RunsOf(FakeDumpTools.PgRestoreList));
        Assert.Empty(Tools.RunsOf(FakeDumpTools.PgRestore));
        Assert.Empty(Sql);
        Assert.Empty(_factory.RestoreStagingEntries());
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
    }

    [Theory]
    [InlineData("postgres", "truncated")]
    [InlineData("postgres", "grown")]
    [InlineData("postgres", "not an archive")]
    [InlineData("mysql", "truncated")]
    [InlineData("mysql", "cut short")]
    public async Task ArtifactIsNotTheBackupThatWasStored_FailsAsArtifactInvalid_AndTheDatabaseIsNeverTouched(string engine, string damage)
    {
        var (_, _, backupId, artifactPath) = await CreateBackedUpDatabaseAsync(engine);
        var original = await File.ReadAllBytesAsync(artifactPath);
        await File.WriteAllBytesAsync(artifactPath, damage switch
        {
            "truncated" => original[..^3],
            "grown" => [.. original, .. "extra"u8],
            // Same size as recorded, but no longer a dump of the engine.
            "not an archive" => [.. "XXXXX"u8, .. original[5..]],
            _ => [.. original[..^30], .. new byte[30]]
        });

        var job = await RestoreAsync(backupId);

        Assert.Equal("failed", job.Status());
        Assert.Equal("RESTORE_ARTIFACT_INVALID", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(Tools.RunsOf(FakeDumpTools.PgRestore));
        Assert.Empty(Tools.RunsOf(FakeDumpTools.MySql));
        Assert.Empty(Sql);
        Assert.Empty(_factory.RestoreStagingEntries());
    }

    [Fact]
    public async Task Postgres_ArchiveThatPgRestoreCannotRead_FailsAsArtifactInvalid_BeforeTheDatabaseIsEmptied()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        // The right size and the right first bytes, and still not an archive pg_restore can read.
        Tools.FailNext(FakeDumpTools.PgRestoreList, 3, "pg_restore: error: could not read from input file: end of file");

        var job = await RestoreAsync(backupId);

        Assert.Equal("RESTORE_ARTIFACT_INVALID", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(3, Tools.RunsOf(FakeDumpTools.PgRestoreList).Count);
        Assert.Empty(Tools.RunsOf(FakeDumpTools.PgRestore));
        Assert.Empty(Sql);
    }

    [Theory]
    [InlineData(";     TOC Entries: -1482184792\n; Selected TOC Entries:\n")]
    [InlineData(";     TOC Entries: 0\n")]
    [InlineData("; no entry count at all\n")]
    [InlineData("")]
    public async Task Postgres_ArchiveThatPgRestoreListsWithoutErrorButWithoutEntries_IsNotRestored_AndTheDatabaseIsNotEmptied(string listing)
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        // What pg_restore really does with some damaged archives: exit code 0, and nothing in them.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Tools.Script(FakeDumpTools.PgRestoreList, _ => new Infrastructure.Backups.ProcessResult(0, listing, string.Empty, TimedOut: false));
        }

        var job = await RestoreAsync(backupId);

        Assert.Equal("failed", job.Status());
        Assert.Equal("RESTORE_ARTIFACT_INVALID", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(Tools.RunsOf(FakeDumpTools.PgRestore));
        Assert.Empty(Sql);
    }

    [Fact]
    public async Task Postgres_ArchiveNewerThanTheInstalledPgRestore_FailsAsToolUnavailable_BeforeTheDatabaseIsEmptied()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        Tools.FailNext(FakeDumpTools.PgRestoreList, 3, "pg_restore: error: unsupported version (1.16) in file header");

        var job = await RestoreAsync(backupId);

        Assert.Equal("RESTORE_TOOL_UNAVAILABLE", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(Sql);
    }

    [Theory]
    [InlineData("postgres", "pg_restore")]
    [InlineData("mysql", "mysql")]
    public async Task RestoreProgramNotInstalled_FailsAsToolUnavailable_AndTheDatabaseIsNeverEmptied(string engine, string tool)
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync(engine);
        Tools.Missing = true;

        var job = await RestoreAsync(backupId);

        Assert.Equal("RESTORE_TOOL_UNAVAILABLE", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal($"The restore program {tool} is not available on the server.", job.GetProperty("error").GetProperty("message").GetString());
        // Having no program is found out before anything is removed.
        Assert.Empty(Sql);
        Assert.Empty(_factory.RestoreStagingEntries());
    }

    // --- Failures after the database has been emptied -----------------------------------------

    [Fact]
    public async Task RestoreProgramFailsOnce_TheNextAttemptStartsOver_FetchesAndEmptiesAgain_AndCompletes()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        Tools.FailNext(FakeDumpTools.PgRestore, 1);

        var job = await RestoreAsync(backupId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(2, job.GetProperty("attempt").GetInt32());
        // Each attempt is complete in itself: it does not assume anything the failed one left.
        Assert.Equal(2, Tools.RunsOf(FakeDumpTools.PgRestoreList).Count);
        Assert.Equal(2, Tools.RunsOf(FakeDumpTools.PgRestore).Count);
        Assert.Equal(2, Sql.Count(statement => statement == PostgreSqlRestoreManager.EmptyDatabaseSql));
        Assert.Equal(1, Sql.Count(statement => statement == "SELECT 1"));
        Assert.Empty(_factory.RestoreStagingEntries());
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    public async Task RestoreProgramKeepsFailing_TheJobFailsWithASafeError_TheDatabaseStaysReady_AndTheBackupIsUntouched(string engine)
    {
        var (_, databaseId, backupId, artifactPath) = await CreateBackedUpDatabaseAsync(engine);
        var kind = engine == "postgres" ? FakeDumpTools.PgRestore : FakeDumpTools.MySql;
        var tool = engine == "postgres" ? "pg_restore" : "mysql";
        Tools.FailNext(kind, 3, $"{tool}: error: raw-tool-detail could not execute query at /var/lib/secret");

        var job = await RestoreAsync(backupId);

        Assert.Equal("failed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        Assert.Equal(3, Tools.RunsOf(kind).Count);
        Assert.Equal("RESTORE_PROCESS_FAILED", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal($"The restore program {tool} failed.", job.GetProperty("error").GetProperty("message").GetString());
        // The program's own words are for the log, not for clients.
        Assert.DoesNotContain("raw-tool-detail", job.GetRawText());
        Assert.Contains(_factory.Logs.Entries, entry => entry.Contains("raw-tool-detail", StringComparison.Ordinal));

        // Never reported as restored: no verification happened.
        Assert.DoesNotContain("SELECT 1", Sql);
        // There is no restoring or failed status for a database; the job says what happened.
        var database = await _client.GetDatabaseAsync(databaseId);
        Assert.Equal("ready", database.Status());
        Assert.Equal(JsonValueKind.Null, database.GetProperty("error").ValueKind);
        Assert.Equal("completed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.True(File.Exists(artifactPath));
        Assert.Empty(_factory.RestoreStagingEntries());
    }

    [Theory]
    [InlineData("postgres", "pg_restore: error: connection to server at \"10.20.30.40\", port 5432 failed: Connection refused", "RESTORE_CONNECTION_FAILED")]
    [InlineData("postgres", "pg_restore: error: connection to server at \"10.20.30.40\", port 5432 failed: FATAL:  database \"orders\" does not exist", "RESTORE_DATABASE_UNAVAILABLE")]
    [InlineData("postgres", "pg_restore: error: could not execute query: ERROR:  out of memory", "RESTORE_PROCESS_FAILED")]
    [InlineData("mysql", "ERROR 2003 (HY000): Can't connect to MySQL server on '10.20.30.40:3306' (111)", "RESTORE_CONNECTION_FAILED")]
    [InlineData("mysql", "ERROR 1045 (28000): Access denied for user 'root'@'172.18.0.3' (using password: YES)", "RESTORE_CONNECTION_FAILED")]
    [InlineData("mysql", "ERROR 1049 (42000): Unknown database 'orders'", "RESTORE_DATABASE_UNAVAILABLE")]
    [InlineData("mysql", "ERROR 1064 (42000) at line 31: You have an error in your SQL syntax", "RESTORE_PROCESS_FAILED")]
    public async Task RestoreProgramFailure_IsClassifiedFromItsDiagnostics_WhichNeverReachTheClient(string engine, string standardError, string expectedCode)
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync(engine);
        Tools.FailNext(engine == "postgres" ? FakeDumpTools.PgRestore : FakeDumpTools.MySql, 3, standardError);

        var job = await RestoreAsync(backupId);

        Assert.Equal(expectedCode, job.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("10.20.30.40", job.GetRawText());
        Assert.DoesNotContain("172.18.0.3", job.GetRawText());
        Assert.DoesNotContain("line 31", job.GetRawText());
    }

    [Fact]
    public async Task RestoreProgramRunsLongerThanAllowed_FailsAsTimeout()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Tools.Script(FakeDumpTools.PgRestore, _ => new Infrastructure.Backups.ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));
        }

        var job = await RestoreAsync(backupId);

        Assert.Equal("RESTORE_OPERATION_TIMEOUT", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_factory.RestoreStagingEntries());
    }

    [Fact]
    public async Task DatabaseCannotBeConnectedTo_FailsAsConnectionFailed_BeforeTheRestoreProgramRuns()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        _factory.RestoreSql.OpenFailure = new IOException("raw-driver-detail: Host=10.20.30.40;Password=hunter2");

        var job = await RestoreAsync(backupId);

        Assert.Equal("RESTORE_CONNECTION_FAILED", job.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("hunter2", job.GetRawText());
        Assert.DoesNotContain("raw-driver-detail", job.GetRawText());
        Assert.Empty(Tools.RunsOf(FakeDumpTools.PgRestore));
    }

    [Fact]
    public async Task EmptyingTheDatabaseFails_ForInstanceOnALockHeldByAReconnectedClient_TheAttemptFails_AndTheRetryCompletes()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        var failures = 0;
        _factory.RestoreSql.StatementFailure = sql =>
            sql == PostgreSqlRestoreManager.EmptyDatabaseSql && failures++ == 0
                ? new InvalidOperationException("raw-driver-detail: canceling statement due to lock timeout")
                : null;

        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        Tools.Block(FakeDumpTools.PgRestore);
        var processing = _factory.ProcessJobAsync(jobId);
        // The first attempt failed while emptying and never reached pg_restore; the second is now held at it.
        await Tools.WaitForRunAsync();

        var running = await _client.GetJobAsync(jobId);
        Assert.Equal(2, running.GetProperty("attempt").GetInt32());
        Assert.Equal("RESTORE_PROCESS_FAILED", running.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("The database could not be emptied before the restore.", running.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("raw-driver-detail", running.GetRawText());

        Tools.Release();
        await processing.WaitAsync(TestTimeout);

        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
        Assert.Single(Tools.RunsOf(FakeDumpTools.PgRestore));
    }

    [Fact]
    public async Task RestoredDatabaseCannotBeUsed_FailsAsVerificationFailed_AndIsNotReportedAsRestored()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        _factory.RestoreSql.StatementFailure = sql => sql == "SELECT 1" ? new IOException("raw-driver-detail") : null;

        var job = await RestoreAsync(backupId);

        Assert.Equal("failed", job.Status());
        Assert.Equal("RESTORE_VERIFICATION_FAILED", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(3, Tools.RunsOf(FakeDumpTools.PgRestore).Count);
    }

    // --- State and ownership ------------------------------------------------------------------

    [Fact]
    public async Task InstanceServerCannotBeLocated_FailsAsDatabaseUnavailable_WithoutFetchingOrRunningAnything()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        var runsBefore = Tools.RunCount;
        _factory.Endpoints.Failure = new DatabaseOperationException(
            DatabaseErrorCodes.DatabaseEngineUnavailable, "The instance's database server is not running.");

        var job = await RestoreAsync(backupId);

        Assert.Equal("RESTORE_DATABASE_UNAVAILABLE", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(runsBefore, Tools.RunCount);
        Assert.Empty(Sql);
        Assert.Empty(_factory.RestoreStagingEntries());
    }

    [Theory]
    [InlineData(InstanceStatus.Stopped)]
    [InlineData(InstanceStatus.Failed)]
    public async Task InstanceNoLongerRunning_FailsWithoutTouchingTheDatabaseOrStartingTheInstance(InstanceStatus status)
    {
        var (instanceId, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await _factory.SetInstanceStatusAsync(instanceId, status);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("RESTORE_DATABASE_UNAVAILABLE", await JobErrorCodeAsync(jobId));
        Assert.Empty(Sql);
        Assert.Empty(_factory.Provisioner.EnsureRunningInstanceIds);
    }

    [Fact]
    public async Task InstanceComesBackDuringRetries_RestoreCompletes()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        Tools.FailNext(FakeDumpTools.PgRestore, 2, "pg_restore: error: connection to server at \"10.20.30.40\", port 5432 failed: Connection refused");

        var job = await RestoreAsync(backupId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
    }

    [Fact]
    public async Task JobNamesAnotherDatabaseThanTheBackups_IsNeverRestoredIntoIt()
    {
        var (instanceId, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        var other = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "other");
        // A restore job that claims the backup for a database it was not made of.
        var jobId = await InsertRestoreJobAsync(instanceId, other, backupId);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("failed", (await _client.GetJobAsync(jobId)).Status());
        Assert.Equal("RESTORE_INVALID_STATE", await JobErrorCodeAsync(jobId));
        Assert.Empty(Tools.RunsOf(FakeDumpTools.PgRestore));
        Assert.Empty(Sql);
        Assert.Empty(_factory.RestoreSql.ConnectionStrings);
    }

    [Fact]
    public async Task JobNamesAnotherInstanceThanTheDatabases_FailsWithoutTouchingAnything()
    {
        var (_, databaseId, backupId, _) = await CreateBackedUpDatabaseAsync();
        var otherInstance = await _factory.CreateRunningInstanceAsync(_client, "other-instance");
        var jobId = await InsertRestoreJobAsync(otherInstance, databaseId, backupId);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("RESTORE_INVALID_STATE", await JobErrorCodeAsync(jobId));
        Assert.Empty(Sql);
    }

    [Fact]
    public async Task BackupMetadataIsGone_FailsWithoutTouchingAnything()
    {
        var (instanceId, databaseId, _, _) = await CreateBackedUpDatabaseAsync();
        var jobId = await InsertRestoreJobAsync(instanceId, databaseId, Guid.NewGuid());

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("RESTORE_INVALID_STATE", await JobErrorCodeAsync(jobId));
        Assert.Empty(Sql);
    }

    [Fact]
    public async Task BackupThatIsNotCompleted_IsNotRestoredEvenIfAJobSaysSo()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var (backupId, backupJobId) = await _client.CreateBackupAsync(databaseId);
        Tools.FailNextRuns(3);
        await _factory.ProcessJobAsync(backupJobId);
        var jobId = await InsertRestoreJobAsync(instanceId, databaseId, backupId);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("RESTORE_INVALID_STATE", await JobErrorCodeAsync(jobId));
        Assert.Empty(Sql);
    }

    // --- Cancellation and recovery ------------------------------------------------------------

    [Theory]
    [InlineData(FakeDumpTools.PgRestoreList)]
    [InlineData(FakeDumpTools.PgRestore)]
    public async Task Cancelled_BeforeOrDuringTheLoad_LeavesNoFilesAndNoFailure_AndTheJobRestoresFromScratchWhenRunAgain(string cancelDuring)
    {
        var (_, _, backupId, artifactPath) = await CreateBackedUpDatabaseAsync();
        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        using var shutdown = new CancellationTokenSource();
        Tools.Block(cancelDuring);

        var processing = _factory.ProcessJobAsync(jobId, shutdown.Token);
        await Tools.WaitForRunAsync();
        var interrupted = Tools.Runs[^1];
        await shutdown.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TestTimeout));

        // Interrupted, not failed: nothing on disk, the job pending again with no error.
        Assert.Empty(_factory.RestoreStagingEntries());
        Assert.True(interrupted.CredentialPath.Length == 0 || !File.Exists(interrupted.CredentialPath));
        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("pending", job.Status());
        Assert.Equal(0, job.GetProperty("attempt").GetInt32());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("error").ValueKind);
        Assert.True(File.Exists(artifactPath));
        var emptiedBefore = Sql.Count(statement => statement == PostgreSqlRestoreManager.EmptyDatabaseSql);
        Assert.Equal(cancelDuring == FakeDumpTools.PgRestore ? 1 : 0, emptiedBefore);

        // The existing recovery path: the job is simply run again, and does everything again.
        Tools.Release(runs: 10);
        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
        Assert.Equal(emptiedBefore + 1, Sql.Count(statement => statement == PostgreSqlRestoreManager.EmptyDatabaseSql));
        Assert.Equal(FakeDumpTools.PostgresDump, Tools.RunsOf(FakeDumpTools.PgRestore)[^1].InputContent);
        Assert.Empty(_factory.RestoreStagingEntries());
    }

    [Fact]
    public async Task InterruptedRestore_WhoseLeaseExpired_IsRecovered_DiscardsWhatItLeftOnDisk_AndRunsAgain()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);

        // The process died in the middle of the download: the job is running under a lease nobody
        // renews, and half a file is in its directory. Even one with a final name is not trusted.
        await _factory.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);
        var directory = Path.Combine(_factory.RestoreStagingRoot, jobId.ToString("D"));
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "artifact.dump.partial"), "PGDMP half a downl"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(directory, "artifact.dump"), "PGDMP left by someone"u8.ToArray());

        Assert.Equal([jobId], await _factory.RecoverJobsAsync(includePending: false));
        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        // What was loaded is the backup, fetched again, not either leftover.
        Assert.Equal(FakeDumpTools.PostgresDump, Assert.Single(Tools.RunsOf(FakeDumpTools.PgRestore)).InputContent);
        Assert.Empty(_factory.RestoreStagingEntries());
    }

    [Fact]
    public async Task Restart_PendingAndInterruptedRestores_AreExecutedByTheWorker()
    {
        using var database = new TempDatabase();
        var backupRoot = Path.Combine(Path.GetTempPath(), $"aurora-restore-restart-{Guid.NewGuid():N}");
        try
        {
            Guid pendingJobId, interruptedJobId;
            using (var before = new ApiFactory { DatabasePath = database.Path, BackupRootPath = backupRoot })
            using (var client = before.CreateClient())
            {
                var instanceId = await before.CreateRunningInstanceAsync(client);
                var first = await before.CreateReadyDatabaseAsync(client, instanceId, "first");
                var second = await before.CreateReadyDatabaseAsync(client, instanceId, "second");
                pendingJobId = await ApiFactory.RequestRestoreAsync(client, await before.CreateCompletedBackupAsync(client, first));
                interruptedJobId = await ApiFactory.RequestRestoreAsync(client, await before.CreateCompletedBackupAsync(client, second));
                await before.SimulateAbandonedExecutionAsync(interruptedJobId, leaseExpiresAt: LongAgo);
            }

            using var after = new ApiFactory { DatabasePath = database.Path, BackupRootPath = backupRoot, RunWorker = true };
            using var restarted = after.CreateClient();

            Assert.Equal("completed", (await restarted.WaitForFinishedJobAsync(pendingJobId)).Status());
            var interrupted = await restarted.WaitForFinishedJobAsync(interruptedJobId);
            Assert.Equal("completed", interrupted.Status());
            Assert.Equal(1, interrupted.GetProperty("attempt").GetInt32());
            Assert.Equal(2, after.DumpTools.RunsOf(FakeDumpTools.PgRestore).Count);
            Assert.Empty(after.RestoreStagingEntries());
        }
        finally
        {
            if (Directory.Exists(backupRoot))
            {
                Directory.Delete(backupRoot, recursive: true);
            }
        }
    }

    // --- Security -----------------------------------------------------------------------------

    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    public async Task Password_ReachesTheRestoreProgramOnlyThroughAPrivateFile_AndAppearsNowhereElse(string engine)
    {
        var (instanceId, databaseId, backupId, _) = await CreateBackedUpDatabaseAsync(engine, "orders");
        var password = await _factory.AdminPasswordAsync(instanceId);

        var job = await RestoreAsync(backupId);

        Assert.Equal("completed", job.Status());
        var restoreRuns = Tools.Runs.Where(run => run.Kind is not (FakeDumpTools.PgDump or FakeDumpTools.MySqlDump)).ToList();
        Assert.Equal(2, restoreRuns.Count);

        // Not on any command line, not in any environment, and no shell anywhere.
        Assert.All(restoreRuns, run =>
        {
            Assert.DoesNotContain(run.Arguments, argument => argument.Contains(password, StringComparison.Ordinal));
            Assert.DoesNotContain(run.Environment, variable =>
                variable.Value.Contains(password, StringComparison.Ordinal) || variable.Key is "PGPASSWORD" or "MYSQL_PWD");
            Assert.DoesNotContain(run.Executable, new[] { "sh", "bash", "/bin/sh", "/bin/bash", "cmd", "cmd.exe" });
            Assert.DoesNotContain(run.Arguments, argument => argument is "-c" or "/c" or "-e" || argument.StartsWith("--execute", StringComparison.Ordinal));
        });

        // In a file only the API's user can read, gone once the program has exited.
        var load = restoreRuns[^1];
        Assert.Equal(
            engine == "postgres" ? $"{FakeInstanceEndpoints.Host}:5432:orders:postgres:{password}\n" : $"[client]\npassword=\"{password}\"\n",
            load.CredentialContent);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(OwnerOnlyFile, load.CredentialFileMode);
        }

        Assert.False(File.Exists(load.CredentialPath));

        // The driver's connection needs it; no statement and nothing a client can read contains it.
        Assert.All(_factory.RestoreSql.ConnectionStrings, connectionString => Assert.Contains(password, connectionString));
        Assert.All(Sql, statement => Assert.DoesNotContain(password, statement));
        string[] responses =
        [
            job.GetRawText(),
            (await _client.GetBackupAsync(backupId)).GetRawText(),
            (await _client.GetDatabaseAsync(databaseId)).GetRawText(),
            (await _client.GetInstanceAsync(instanceId)).GetRawText()
        ];
        Assert.All(responses, response => Assert.DoesNotContain(password, response));
        Assert.All(responses, response => Assert.DoesNotContain(_factory.RestoreStagingRoot, response));

        Assert.NotEmpty(_factory.Logs.Entries);
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(password, StringComparison.Ordinal));
        var stored = await _factory.GetJobEntityAsync(job.GetProperty("id").GetGuid());
        Assert.DoesNotContain(password, $"{stored.ErrorCode}{stored.ErrorMessage}");
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    public async Task Password_EchoedByAFailingRestoreProgram_IsRemovedBeforeItsDiagnosticsAreLogged(string engine)
    {
        var (instanceId, _, backupId, _) = await CreateBackedUpDatabaseAsync(engine);
        var password = await _factory.AdminPasswordAsync(instanceId);
        var kind = engine == "postgres" ? FakeDumpTools.PgRestore : FakeDumpTools.MySql;
        Tools.FailNext(kind, 3, $"tool: error: raw-tool-detail could not log in with password {password}");

        var job = await RestoreAsync(backupId);

        Assert.Equal("failed", job.Status());
        Assert.All(Tools.RunsOf(kind), run => Assert.False(File.Exists(run.CredentialPath)));
        Assert.Contains(_factory.Logs.Entries, entry => entry.Contains("raw-tool-detail", StringComparison.Ordinal) && entry.Contains("***", StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(password, job.GetRawText());
    }

    [Fact]
    public async Task Restore_NeverTouchesTheMetadataDatabase_ExceptForItsOwnJob()
    {
        var (_, _, backupId, _) = await CreateBackedUpDatabaseAsync();
        var before = await MetadataSnapshotAsync();

        var job = await RestoreAsync(backupId);

        Assert.Equal("completed", job.Status());
        // Instances, databases and backups are exactly as they were; one job was added.
        Assert.Equal(before, await MetadataSnapshotAsync());
        // The connections the restore opened are to the instance's server, at the instance's
        // endpoint, not to wherever the application keeps its own data.
        Assert.All(_factory.RestoreSql.ConnectionStrings, connectionString => Assert.Contains(FakeInstanceEndpoints.Host, connectionString));
    }

    [Fact]
    public async Task ToolPathsAndTimeouts_ComeFromConfiguration()
    {
        using var factory = new ApiFactory
        {
            ConfigureBackups = options =>
            {
                options.Tools.PgRestorePath = "/opt/pg17/bin/pg_restore";
                options.Restore.TimeoutSeconds = 900;
                options.Restore.LockTimeoutSeconds = 5;
            }
        };
        using var client = factory.CreateClient();
        var instanceId = await factory.CreateRunningInstanceAsync(client);
        var databaseId = await factory.CreateReadyDatabaseAsync(client, instanceId, "app");
        var backupId = await factory.CreateCompletedBackupAsync(client, databaseId);

        await factory.ProcessJobAsync(await ApiFactory.RequestRestoreAsync(client, backupId));

        var run = Assert.Single(factory.DumpTools.RunsOf(FakeDumpTools.PgRestore));
        Assert.Equal("/opt/pg17/bin/pg_restore", run.Executable);
        Assert.Equal(TimeSpan.FromSeconds(900), run.Timeout);
        Assert.StartsWith(factory.RestoreStagingRoot, run.InputPath);
    }

    [Fact]
    public void Options_ValidateRestoreSettings()
    {
        Infrastructure.Backups.BackupOptions Valid() => new() { Local = new Infrastructure.Backups.LocalBackupOptions { RootPath = "/backups" } };

        Assert.Null(Valid().Validate());

        var noTool = Valid();
        noTool.Tools.PgRestorePath = "";
        Assert.Contains("PgRestorePath", noTool.Validate());

        var noTimeout = Valid();
        noTimeout.Restore.TimeoutSeconds = 0;
        Assert.Contains("Backups:Restore:TimeoutSeconds", noTimeout.Validate());

        var noLockTimeout = Valid();
        noLockTimeout.Restore.LockTimeoutSeconds = 0;
        Assert.Contains("Backups:Restore:LockTimeoutSeconds", noLockTimeout.Validate());
    }

    // --- Helpers ------------------------------------------------------------------------------

    /// <summary>Stores a pending restore job directly, with whatever ids the test wants it to name.</summary>
    private Task<Guid> InsertRestoreJobAsync(Guid instanceId, Guid databaseId, Guid backupId) =>
        _factory.WithDbAsync(async db =>
        {
            var job = Job.Create(JobType.RestoreDatabase, instanceId, 3, DateTime.UtcNow, databaseId, backupId);
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            return job.Id;
        });

    /// <summary>Everything in the metadata database except jobs, as text.</summary>
    private Task<string> MetadataSnapshotAsync() =>
        _factory.WithDbAsync(async db => JsonSerializer.Serialize(new
        {
            Instances = await db.Instances.AsNoTracking().OrderBy(i => i.Id).ToListAsync(),
            Databases = await db.Databases.AsNoTracking().OrderBy(d => d.Id).ToListAsync(),
            Backups = await db.Backups.AsNoTracking().OrderBy(b => b.Id).ToListAsync(),
            Secrets = await db.InstanceSecrets.AsNoTracking().OrderBy(s => s.InstanceId).ToListAsync()
        }));
}
