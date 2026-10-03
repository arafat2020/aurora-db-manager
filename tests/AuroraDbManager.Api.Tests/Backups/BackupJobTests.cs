using System.Text.Json;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Backups;
using Microsoft.EntityFrameworkCore;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Backups;

/// <summary>
/// The backup_database handler with the real backup managers and the real local storage, run
/// through the real job processor with <see cref="ApiFactory.ProcessJobAsync"/>. Only the dump
/// programs are fake. Jobs get three attempts with no delay in between.
/// </summary>
public sealed class BackupJobTests : IDisposable
{
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public BackupJobTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private FakeDumpTools Tools => _factory.DumpTools;

    private async Task<(Guid InstanceId, Guid DatabaseId)> CreateReadyDatabaseAsync(string engine = "postgres", string name = "app")
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client, engine: engine);
        return (instanceId, await _factory.CreateReadyDatabaseAsync(_client, instanceId, name));
    }

    private async Task<string?> JobErrorCodeAsync(Guid jobId)
    {
        var error = (await _client.GetJobAsync(jobId)).GetProperty("error");
        return error.ValueKind == JsonValueKind.Null ? null : error.GetProperty("code").GetString();
    }

    // --- Success ------------------------------------------------------------------------------

    [Fact]
    public async Task Postgres_Success_RunsPgDumpInCustomFormat_AndCompletesTheBackupWithItsArtifact()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync("postgres", "orders");
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Equal(backupId, job.GetProperty("backupId").GetGuid());
        Assert.Equal(databaseId, job.GetProperty("databaseId").GetGuid());

        var path = _factory.BackupFilePath(instanceId, databaseId, backupId, "dump");
        var run = Assert.Single(Tools.Runs);
        Assert.Equal("pg_dump", run.Executable);
        Assert.Equal(
            [
                "--format=custom",
                $"--file={path}.partial",
                $"--host={FakeInstanceEndpoints.Host}",
                "--port=5432",
                "--username=postgres",
                "--no-password",
                "--dbname=orders"
            ],
            run.Arguments);
        Assert.Equal(["PGCONNECT_TIMEOUT", "PGPASSFILE"], run.Environment.Keys.Order());
        Assert.Equal(TimeSpan.FromHours(1), run.Timeout);

        // The artifact is where the id-based layout puts it, and is what the program wrote.
        Assert.Equal([path], _factory.BackupFiles());
        Assert.Equal(FakeDumpTools.PostgresDump, await File.ReadAllBytesAsync(path));

        var stored = await _factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync());
        Assert.Equal(BackupStatus.Completed, stored.Status);
        Assert.Equal(path, stored.Path);
        Assert.Equal(new FileInfo(path).Length, stored.SizeBytes);
        Assert.NotNull(stored.CompletedAt);

        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
    }

    [Fact]
    public async Task Mysql_Success_RunsMysqldumpWithStructuredArguments_AndCompletesTheBackup()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync("mysql", "orders");
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());

        var path = _factory.BackupFilePath(instanceId, databaseId, backupId, "sql");
        var run = Assert.Single(Tools.Runs);
        Assert.Equal("mysqldump", run.Executable);
        Assert.Equal(
            [
                $"--defaults-extra-file={run.CredentialPath}",
                $"--host={FakeInstanceEndpoints.Host}",
                "--port=3306",
                "--protocol=TCP",
                "--user=root",
                "--single-transaction",
                "--routines",
                "--triggers",
                "--events",
                "--hex-blob",
                $"--result-file={path}.partial",
                "--",
                "orders"
            ],
            run.Arguments);
        Assert.Empty(run.Environment);

        Assert.Equal([path], _factory.BackupFiles());
        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("completed", backup.Status());
        Assert.Equal(FakeDumpTools.MysqlDump.Length, backup.GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task Success_ArtifactAndItsDirectoriesArePrivate_AndNoStagingFileIsLeft()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        var path = _factory.BackupFilePath(instanceId, databaseId, backupId, "dump");
        Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(path));
        // The program wrote into a file that was already private; it never created one itself.
        Assert.Equal(OwnerOnlyFile, Assert.Single(Tools.Runs).OutputFileMode);
        Assert.Equal(OwnerOnlyFile | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        Assert.Equal(OwnerOnlyFile | UnixFileMode.UserExecute, File.GetUnixFileMode(_factory.BackupRoot));
        Assert.DoesNotContain(_factory.BackupFiles(), file => file.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Success_BackupIsRunningWhileTheProgramRuns_AndTheArtifactDoesNotExistYet()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Tools.Block();

        var processing = _factory.ProcessJobAsync(jobId);
        await Tools.WaitForRunAsync();

        // pending → running was committed on its own, before the long-running program started.
        Assert.Equal("running", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Equal("running", (await _client.GetJobAsync(jobId)).Status());
        var path = _factory.BackupFilePath(instanceId, databaseId, backupId, "dump");
        Assert.False(File.Exists(path));
        Assert.Equal([path + ".partial"], _factory.BackupFiles());

        Tools.Release();
        await processing.WaitAsync(TestTimeout);

        Assert.Equal("completed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Equal([path], _factory.BackupFiles());
    }

    [Fact]
    public async Task Success_SizeIsThatOfTheFileOnDisk()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        Tools.Content = [.. FakeDumpTools.PostgresDump, .. new byte[100_000]];

        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        var onDisk = new FileInfo(_factory.BackupFilePath(instanceId, databaseId, backupId, "dump")).Length;
        Assert.Equal(FakeDumpTools.PostgresDump.Length + 100_000, onDisk);
        Assert.Equal(onDisk, (await _client.GetBackupAsync(backupId)).GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task SecondBackupOfADatabase_GetsItsOwnArtifact_AndLeavesTheFirstUntouched()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var first = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        Tools.Content = [.. FakeDumpTools.PostgresDump, .. "more data in the second backup"u8];

        var second = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        var firstPath = _factory.BackupFilePath(instanceId, databaseId, first, "dump");
        var secondPath = _factory.BackupFilePath(instanceId, databaseId, second, "dump");
        Assert.Equal(new[] { firstPath, secondPath }.Order(), _factory.BackupFiles().Order());
        Assert.Equal(FakeDumpTools.PostgresDump, await File.ReadAllBytesAsync(firstPath));
        Assert.Equal(Path.GetDirectoryName(firstPath), Path.GetDirectoryName(secondPath));
    }

    // --- Failure ------------------------------------------------------------------------------

    [Fact]
    public async Task AttemptFails_BackupStaysRunningWhileTheJobRetries_ThenCompletes()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Tools.FailNextRuns(1);
        Tools.Block();

        var processing = _factory.ProcessJobAsync(jobId);
        await Tools.WaitForRunAsync();
        Tools.Release();
        // The first attempt has failed and the second is now held inside the program.
        await Tools.WaitForRunAsync();

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("running", job.Status());
        Assert.Equal(2, job.GetProperty("attempt").GetInt32());
        Assert.Equal("BACKUP_PROCESS_FAILED", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("running", (await _client.GetBackupAsync(backupId)).Status());

        Tools.Release();
        await processing.WaitAsync(TestTimeout);

        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
        Assert.Equal("completed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Equal(2, Tools.RunCount);
        Assert.Single(_factory.BackupFiles());
    }

    [Fact]
    public async Task EveryAttemptFails_FailsJobAndBackupWithASafeError_LeavesNoFile_AndTheDatabaseStaysReady()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Tools.FailAllRuns("pg_dump: error: raw-tool-detail at /var/lib/secret/path");

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("failed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        Assert.Equal(3, Tools.RunCount);

        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("failed", backup.Status());
        Assert.Equal("BACKUP_PROCESS_FAILED", backup.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("The backup program pg_dump failed.", backup.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, backup.GetProperty("sizeBytes").ValueKind);

        // The program's own words are for the log, not for clients.
        Assert.DoesNotContain("raw-tool-detail", job.GetRawText() + backup.GetRawText());
        Assert.Contains(_factory.Logs.Entries, entry => entry.Contains("raw-tool-detail", StringComparison.Ordinal));

        Assert.Empty(_factory.BackupFiles());
        // A failed backup says nothing about the database.
        var database = await _client.GetDatabaseAsync(databaseId);
        Assert.Equal("ready", database.Status());
        Assert.Equal(JsonValueKind.Null, database.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task ProgramWritesPartOfADumpAndFails_ThePartIsRemoved_AndNeverBecomesTheArtifact()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Tools.WritePartOfADumpThenFail();
        }

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("failed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Empty(_factory.BackupFiles());
    }

    [Theory]
    [InlineData("postgres", "", "BACKUP_ARTIFACT_INVALID")]
    [InlineData("postgres", "not an archive at all", "BACKUP_ARTIFACT_INVALID")]
    [InlineData("postgres", "PGDM", "BACKUP_ARTIFACT_INVALID")]
    [InlineData("mysql", "", "BACKUP_ARTIFACT_INVALID")]
    [InlineData("mysql", "-- MySQL dump 10.13\nCREATE TABLE `t` (`id` int);\nINSERT INTO `t` VALUES (1),(2", "BACKUP_ARTIFACT_INVALID")]
    public async Task ProgramReportsSuccessButWroteNoCompleteDump_IsAFailure_AndLeavesNoFile(string engine, string content, string expectedCode)
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync(engine);
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Tools.Content = System.Text.Encoding.ASCII.GetBytes(content);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal(expectedCode, await JobErrorCodeAsync(jobId));
        Assert.Equal("failed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Empty(_factory.BackupFiles());
    }

    [Theory]
    [InlineData("postgres", "pg_dump")]
    [InlineData("mysql", "mysqldump")]
    public async Task ProgramIsNotInstalled_FailsAsToolUnavailable(string engine, string tool)
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync(engine);
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Tools.Missing = true;

        await _factory.ProcessJobAsync(jobId);

        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("failed", backup.Status());
        Assert.Equal("BACKUP_TOOL_UNAVAILABLE", backup.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal($"The backup program {tool} is not available on the server.", backup.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(_factory.BackupFiles());
    }

    [Fact]
    public async Task ProgramRunsLongerThanAllowed_FailsAsTimeout_AndLeavesNoFile()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (_, jobId) = await _client.CreateBackupAsync(databaseId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Tools.TimeOutNextRun();
        }

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("BACKUP_OPERATION_TIMEOUT", await JobErrorCodeAsync(jobId));
        Assert.Empty(_factory.BackupFiles());
    }

    [Theory]
    [InlineData("postgres", "pg_dump: error: connection to server at \"10.20.30.40\", port 5432 failed: Connection refused", "BACKUP_CONNECTION_FAILED")]
    [InlineData("postgres", "pg_dump: error: connection to server at \"10.20.30.40\", port 5432 failed: FATAL:  password authentication failed for user \"postgres\"", "BACKUP_CONNECTION_FAILED")]
    [InlineData("postgres", "pg_dump: error: connection to server at \"10.20.30.40\", port 5432 failed: FATAL:  database \"app\" does not exist", "BACKUP_DATABASE_UNAVAILABLE")]
    [InlineData("postgres", "pg_dump: error: aborting because of server version mismatch\npg_dump: detail: server version: 17.2; pg_dump version: 16.4", "BACKUP_TOOL_UNAVAILABLE")]
    [InlineData("postgres", "pg_dump: error: query failed: ERROR:  permission denied for table t", "BACKUP_PROCESS_FAILED")]
    [InlineData("mysql", "mysqldump: Got error: 2003: Can't connect to MySQL server on '10.20.30.40:3306' (111) when trying to connect", "BACKUP_CONNECTION_FAILED")]
    [InlineData("mysql", "mysqldump: Got error: 1045: Access denied for user 'root'@'172.18.0.3' (using password: YES) when trying to connect", "BACKUP_CONNECTION_FAILED")]
    [InlineData("mysql", "mysqldump: Got error: 1049: Unknown database 'app' when selecting the database", "BACKUP_DATABASE_UNAVAILABLE")]
    [InlineData("mysql", "mysqldump: Error 1317: Query execution was interrupted when dumping table `t` at row: 12", "BACKUP_PROCESS_FAILED")]
    public async Task ProgramFailure_IsClassifiedFromItsDiagnostics_WhichNeverReachTheClient(string engine, string standardError, string expectedCode)
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync(engine);
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Tools.FailAllRuns(standardError);

        await _factory.ProcessJobAsync(jobId);

        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal(expectedCode, backup.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("10.20.30.40", backup.GetRawText() + (await _client.GetJobAsync(jobId)).GetRawText());
        Assert.DoesNotContain("172.18.0.3", backup.GetRawText());
    }

    [Fact]
    public async Task InstanceServerCannotBeLocated_FailsAsDatabaseUnavailable_WithoutRunningTheProgram()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        _factory.Endpoints.Failure = new DatabaseOperationException(
            DatabaseErrorCodes.DatabaseEngineUnavailable, "The instance's database server is not running.");

        await _factory.ProcessJobAsync(jobId);

        var backup = await _client.GetBackupAsync(backupId);
        Assert.Equal("BACKUP_DATABASE_UNAVAILABLE", backup.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, Tools.RunCount);
        Assert.Empty(_factory.BackupFiles());
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
    }

    [Theory]
    [InlineData(InstanceStatus.Stopped)]
    [InlineData(InstanceStatus.Failed)]
    public async Task InstanceNoLongerRunning_FailsWithoutRunningTheProgramOrStartingTheInstance(InstanceStatus status)
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        await _factory.SetInstanceStatusAsync(instanceId, status);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("BACKUP_DATABASE_UNAVAILABLE", await JobErrorCodeAsync(jobId));
        Assert.Equal("failed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Equal(0, Tools.RunCount);
        Assert.Empty(_factory.Provisioner.EnsureRunningInstanceIds);
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
    }

    [Fact]
    public async Task InstanceComesBackDuringRetries_BackupCompletes()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Tools.FailNextRuns(2, "pg_dump: error: connection to server at \"10.20.30.40\", port 5432 failed: Connection refused");

        await _factory.ProcessJobAsync(jobId);

        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(3, job.GetProperty("attempt").GetInt32());
        Assert.Equal("completed", (await _client.GetBackupAsync(backupId)).Status());
    }

    [Fact]
    public async Task StorageCannotBeWritten_FailsAsStorageFailure_WithoutRunningTheProgram()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        // A file where the backup root should be.
        await File.WriteAllTextAsync(_factory.BackupRoot, "not a directory");

        try
        {
            await _factory.ProcessJobAsync(jobId);

            var backup = await _client.GetBackupAsync(backupId);
            Assert.Equal("BACKUP_STORAGE_FAILED", backup.GetProperty("error").GetProperty("code").GetString());
            Assert.DoesNotContain(_factory.BackupRoot, backup.GetRawText());
            Assert.Equal(0, Tools.RunCount);
        }
        finally
        {
            File.Delete(_factory.BackupRoot);
        }
    }

    // --- Ownership and state ------------------------------------------------------------------

    [Fact]
    public async Task DatabaseNotReadyWhenTheJobRuns_FailsTheBackup_WithoutRunningTheProgram()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var (databaseId, _) = await _client.CreateDatabaseAsync(instanceId, "app");
        var (backupId, jobId) = await InsertBackupAndJobAsync(databaseId, jobInstanceId: instanceId, completeCreateJob: true);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("BACKUP_DATABASE_UNAVAILABLE", await JobErrorCodeAsync(jobId));
        Assert.Equal("failed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Equal(0, Tools.RunCount);
        Assert.Equal("creating", (await _client.GetDatabaseAsync(databaseId)).Status());
    }

    [Fact]
    public async Task JobNamesAnotherInstanceThanTheDatabases_FailsWithoutTouchingTheBackupOrRunningTheProgram()
    {
        var (_, databaseId) = await CreateReadyDatabaseAsync();
        var other = await _factory.CreateRunningInstanceAsync(_client, "other");
        var (backupId, jobId) = await InsertBackupAndJobAsync(databaseId, jobInstanceId: other);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("failed", (await _client.GetJobAsync(jobId)).Status());
        Assert.Equal("BACKUP_INVALID_STATE", await JobErrorCodeAsync(jobId));
        Assert.Equal(0, Tools.RunCount);
        Assert.Empty(_factory.BackupFiles());
        // The backup is not the job's to fail either.
        Assert.Equal("pending", (await _client.GetBackupAsync(backupId)).Status());
    }

    [Fact]
    public async Task JobNamesAnotherDatabaseThanTheBackups_FailsWithoutRunningTheProgram()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var backedUp = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var other = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "other");
        var (backupId, jobId) = await InsertBackupAndJobAsync(backedUp, jobInstanceId: instanceId, jobDatabaseId: other);

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("BACKUP_INVALID_STATE", await JobErrorCodeAsync(jobId));
        Assert.Equal(0, Tools.RunCount);
        Assert.Equal("pending", (await _client.GetBackupAsync(backupId)).Status());
    }

    [Fact]
    public async Task BackupMetadataIsGone_FailsWithoutRunningTheProgram()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var jobId = await _factory.WithDbAsync(async db =>
        {
            var job = Job.Create(JobType.BackupDatabase, instanceId, 3, DateTime.UtcNow, databaseId, Guid.NewGuid());
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            return job.Id;
        });

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("BACKUP_INVALID_STATE", await JobErrorCodeAsync(jobId));
        Assert.Equal(0, Tools.RunCount);
    }

    [Fact]
    public async Task FailedBackup_IsNotRevivedByALaterJob()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, firstJobId) = await _client.CreateBackupAsync(databaseId);
        Tools.FailNextRuns(3);
        await _factory.ProcessJobAsync(firstJobId);
        var secondJobId = await _factory.WithDbAsync(async db =>
        {
            var job = Job.Create(JobType.BackupDatabase, instanceId, 3, DateTime.UtcNow, databaseId, backupId);
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            return job.Id;
        });

        await _factory.ProcessJobAsync(secondJobId);

        Assert.Equal("BACKUP_INVALID_STATE", await JobErrorCodeAsync(secondJobId));
        Assert.Equal("failed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Equal(3, Tools.RunCount);
        Assert.Empty(_factory.BackupFiles());
    }

    // --- Cancellation -------------------------------------------------------------------------

    [Fact]
    public async Task Cancelled_WhileTheProgramRuns_LeavesNoFileAndNoCredentials_AndTheJobRunsAgainLater()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync();
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        using var shutdown = new CancellationTokenSource();
        Tools.Block();

        var processing = _factory.ProcessJobAsync(jobId, shutdown.Token);
        await Tools.WaitForRunAsync();
        var interrupted = Assert.Single(Tools.Runs);
        Assert.True(File.Exists(interrupted.CredentialPath));
        Assert.True(File.Exists(interrupted.OutputPath));

        await shutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TestTimeout));

        // Nothing is left on disk, and nothing was marked completed or failed.
        Assert.Empty(_factory.BackupFiles());
        Assert.False(File.Exists(interrupted.CredentialPath));
        Assert.Equal("running", (await _client.GetBackupAsync(backupId)).Status());
        var job = await _client.GetJobAsync(jobId);
        Assert.Equal("pending", job.Status());
        Assert.Equal(0, job.GetProperty("attempt").GetInt32());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("error").ValueKind);

        // The existing recovery path: the job is simply run again.
        Tools.Release(runs: 10);
        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
        Assert.Equal("completed", (await _client.GetBackupAsync(backupId)).Status());
        Assert.Equal([_factory.BackupFilePath(instanceId, databaseId, backupId, "dump")], _factory.BackupFiles());
    }

    // --- Security -----------------------------------------------------------------------------

    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    public async Task Password_ReachesTheProgramOnlyThroughAPrivateFile_ThatIsGoneAfterwards(string engine)
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync(engine, "orders");
        var password = await _factory.AdminPasswordAsync(instanceId);
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);

        await _factory.ProcessJobAsync(jobId);

        var run = Assert.Single(Tools.Runs);

        // Not on the command line, where other users of the machine could read it.
        Assert.DoesNotContain(run.Arguments, argument => argument.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(password, run.Executable);
        // Not in the environment either.
        Assert.DoesNotContain(run.Environment, variable =>
            variable.Value.Contains(password, StringComparison.Ordinal) || variable.Key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
            || variable.Key is "PGPASSWORD" or "MYSQL_PWD");

        // In a file only the API's user can read, in the format the program expects...
        Assert.Equal(
            engine == "postgres"
                ? $"{FakeInstanceEndpoints.Host}:5432:orders:postgres:{password}\n"
                : $"[client]\npassword=\"{password}\"\n",
            run.CredentialContent);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(OwnerOnlyFile, run.CredentialFileMode);
        }

        // ...kept apart from the backups, and gone as soon as the program has exited.
        Assert.False(run.CredentialPath.StartsWith(_factory.BackupRoot, StringComparison.Ordinal));
        Assert.False(File.Exists(run.CredentialPath));

        // Never in what a client can read...
        string[] responses =
        [
            (await _client.GetBackupAsync(backupId)).GetRawText(),
            (await _client.GetJobAsync(jobId)).GetRawText(),
            await _client.GetStringAsync(DatabaseBackupsUrl(databaseId)),
            (await _client.GetDatabaseAsync(databaseId)).GetRawText(),
            (await _client.GetInstanceAsync(instanceId)).GetRawText()
        ];
        Assert.All(responses, response => Assert.DoesNotContain(password, response));

        // ...nor in anything the application logged, nor in the artifact's path.
        Assert.NotEmpty(_factory.Logs.Entries);
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.BackupFiles(), file => file.Contains(password, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    public async Task Password_EchoedByAFailingProgram_IsRemovedBeforeItsDiagnosticsAreLogged_AndCredentialsAreStillCleanedUp(string engine)
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync(engine);
        var password = await _factory.AdminPasswordAsync(instanceId);
        var (backupId, jobId) = await _client.CreateBackupAsync(databaseId);
        Tools.FailAllRuns($"tool: error: raw-tool-detail could not log in with password {password} (using password: YES)");

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal(3, Tools.RunCount);
        Assert.All(Tools.Runs, run => Assert.False(File.Exists(run.CredentialPath)));
        // A fresh credential file per attempt.
        Assert.Equal(3, Tools.Runs.Select(run => run.CredentialPath).Distinct().Count());

        // The diagnostics were logged, with the password blanked out.
        Assert.Contains(_factory.Logs.Entries, entry => entry.Contains("raw-tool-detail", StringComparison.Ordinal) && entry.Contains("***", StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(password, (await _client.GetBackupAsync(backupId)).GetRawText() + (await _client.GetJobAsync(jobId)).GetRawText());
    }

    [Fact]
    public async Task DatabaseName_IsOneArgumentToTheProgram_NeverPartOfAPathOrACommandString()
    {
        var (instanceId, databaseId) = await CreateReadyDatabaseAsync("postgres", "orders_2026");

        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);

        var run = Assert.Single(Tools.Runs);
        Assert.Single(run.Arguments, argument => argument.Contains("orders_2026", StringComparison.Ordinal));
        Assert.DoesNotContain("orders_2026", _factory.BackupFilePath(instanceId, databaseId, backupId, "dump"));
        // No shell is ever the program, and no argument is a command line.
        Assert.DoesNotContain(run.Executable, new[] { "sh", "bash", "/bin/sh", "/bin/bash", "cmd", "cmd.exe" });
        Assert.DoesNotContain(run.Arguments, argument => argument is "-c" or "/c");
    }

    [Fact]
    public async Task ToolPathsAndTimeouts_ComeFromConfiguration()
    {
        using var factory = new ApiFactory
        {
            ConfigureBackups = options =>
            {
                options.Tools.PgDumpPath = "/opt/pg17/bin/pg_dump";
                options.TimeoutSeconds = 120;
                options.ConnectTimeoutSeconds = 3;
            }
        };
        using var client = factory.CreateClient();
        var instanceId = await factory.CreateRunningInstanceAsync(client);
        var databaseId = await factory.CreateReadyDatabaseAsync(client, instanceId, "app");

        await factory.CreateCompletedBackupAsync(client, databaseId);

        var run = Assert.Single(factory.DumpTools.Runs);
        Assert.Equal("/opt/pg17/bin/pg_dump", run.Executable);
        Assert.Equal(TimeSpan.FromSeconds(120), run.Timeout);
        Assert.Equal("3", run.Environment["PGCONNECT_TIMEOUT"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Options_WithoutARootPath_AreInvalid(string rootPath)
    {
        var options = new BackupOptions { Local = new LocalBackupOptions { RootPath = rootPath } };

        Assert.NotNull(options.Validate());
    }

    [Fact]
    public void Options_ValidateToolsAndTimeouts()
    {
        BackupOptions Valid() => new() { Local = new LocalBackupOptions { RootPath = "/var/lib/aurora/backups" } };

        Assert.Null(Valid().Validate());

        var noTool = Valid();
        noTool.Tools.PgDumpPath = "";
        Assert.NotNull(noTool.Validate());

        var noTimeout = Valid();
        noTimeout.TimeoutSeconds = 0;
        Assert.NotNull(noTimeout.Validate());

        var noConnectTimeout = Valid();
        noConnectTimeout.ConnectTimeoutSeconds = 0;
        Assert.NotNull(noConnectTimeout.Validate());
    }

    // --- Helpers ------------------------------------------------------------------------------

    /// <summary>Stores a pending backup and its job directly, with whatever ids the test wants the job to name.</summary>
    private Task<(Guid BackupId, Guid JobId)> InsertBackupAndJobAsync(
        Guid databaseId, Guid jobInstanceId, Guid? jobDatabaseId = null, bool completeCreateJob = false) =>
        _factory.WithDbAsync(async db =>
        {
            if (completeCreateJob)
            {
                // A database has one unfinished job at most; get its creation job out of the way.
                await db.Jobs.Where(j => j.DatabaseId == databaseId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(j => j.Status, JobStatus.Completed));
            }

            var backup = Backup.Create(databaseId, BackupStorageType.Local, DateTime.UtcNow);
            var job = Job.Create(JobType.BackupDatabase, jobInstanceId, 3, DateTime.UtcNow, jobDatabaseId ?? databaseId, backup.Id);
            db.Backups.Add(backup);
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            return (backup.Id, job.Id);
        });
}
