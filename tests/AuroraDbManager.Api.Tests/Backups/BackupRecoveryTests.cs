using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Tests.Backups;

/// <summary>
/// Backup jobs under the existing job recovery. A "restart" disposes one factory, losing its
/// in-memory queue like a stopped process, and starts a second one on the same system database
/// and the same backup directory.
/// </summary>
public sealed class BackupRecoveryTests : IDisposable
{
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddHours(-1);

    private readonly TempDatabase _database = new();
    private readonly string _backupRoot = Path.Combine(Path.GetTempPath(), $"aurora-recovery-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_backupRoot))
        {
            Directory.Delete(_backupRoot, recursive: true);
        }
    }

    private ApiFactory StoppedWorker() => new() { DatabasePath = _database.Path, BackupRootPath = _backupRoot };

    private ApiFactory RestartedApplication() => new() { DatabasePath = _database.Path, BackupRootPath = _backupRoot, RunWorker = true };

    /// <summary>
    /// Leaves behind what a process that died in the middle of a backup leaves: a job that is
    /// running under a lease nobody renews, and a backup that is running.
    /// </summary>
    private static async Task<(Guid InstanceId, Guid DatabaseId, Guid BackupId, Guid JobId)> InterruptedBackupAsync(ApiFactory factory, HttpClient client)
    {
        var instanceId = await factory.CreateRunningInstanceAsync(client);
        var databaseId = await factory.CreateReadyDatabaseAsync(client, instanceId, "app");
        var (backupId, jobId) = await client.CreateBackupAsync(databaseId);

        await factory.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);
        await factory.WithDbAsync(async db =>
        {
            (await db.Backups.SingleAsync(b => b.Id == backupId)).MarkRunning();
            return await db.SaveChangesAsync();
        });

        return (instanceId, databaseId, backupId, jobId);
    }

    [Fact]
    public async Task Restart_BackupInterruptedBeforeItWroteAnything_IsRunAgainAndCompletes_WithoutLosingAnAttempt()
    {
        Guid instanceId, databaseId, backupId, jobId;
        using (var before = StoppedWorker())
        using (var client = before.CreateClient())
        {
            (instanceId, databaseId, backupId, jobId) = await InterruptedBackupAsync(before, client);
        }

        using var after = RestartedApplication();
        using var restarted = after.CreateClient();
        var job = await restarted.WaitForFinishedJobAsync(jobId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(1, job.GetProperty("attempt").GetInt32());
        Assert.Equal("completed", (await restarted.GetBackupAsync(backupId)).Status());
        Assert.Equal(1, after.DumpTools.RunCount);
        Assert.Equal([after.BackupFilePath(instanceId, databaseId, backupId, "dump")], after.BackupFiles());
    }

    [Fact]
    public async Task Restart_BackupInterruptedAfterTheArtifactWasFinalized_AdoptsTheArtifact_WithoutRunningTheProgramAgain()
    {
        Guid instanceId, databaseId, backupId, jobId;
        string path;
        byte[] finished = [.. FakeDumpTools.PostgresDump, .. "written by the attempt that was interrupted"u8];
        using (var before = StoppedWorker())
        using (var client = before.CreateClient())
        {
            (instanceId, databaseId, backupId, jobId) = await InterruptedBackupAsync(before, client);

            // The program finished and the artifact was moved into place; then the process died,
            // before the backup and the job could be marked completed.
            path = before.BackupFilePath(instanceId, databaseId, backupId, "dump");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, finished);
        }

        using var after = RestartedApplication();
        using var restarted = after.CreateClient();
        var job = await restarted.WaitForFinishedJobAsync(jobId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(0, after.DumpTools.RunCount);
        var backup = await restarted.GetBackupAsync(backupId);
        Assert.Equal("completed", backup.Status());
        Assert.Equal(finished.Length, backup.GetProperty("sizeBytes").GetInt64());
        // One artifact, the one that was already there, untouched.
        Assert.Equal([path], after.BackupFiles());
        Assert.Equal(finished, await File.ReadAllBytesAsync(path));
        Assert.Contains(after.Logs.Entries, entry => entry.Contains("adopted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_BackupInterruptedWhileWriting_DiscardsTheUnfinishedFile_AndWritesTheBackupAgain()
    {
        Guid instanceId, databaseId, backupId, jobId;
        string path;
        using (var before = StoppedWorker())
        using (var client = before.CreateClient())
        {
            (instanceId, databaseId, backupId, jobId) = await InterruptedBackupAsync(before, client);

            // The process died while the program was writing: only the staging file exists. It
            // even looks like the start of a real archive, and still must not be trusted.
            path = before.BackupFilePath(instanceId, databaseId, backupId, "dump");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path + ".partial", [.. FakeDumpTools.PostgresDump, .. new byte[5000]]);
        }

        using var after = RestartedApplication();
        using var restarted = after.CreateClient();
        var job = await restarted.WaitForFinishedJobAsync(jobId);

        Assert.Equal("completed", job.Status());
        Assert.Equal(1, after.DumpTools.RunCount);
        Assert.Equal([path], after.BackupFiles());
        // What is there now is what the new run wrote, not the leftover.
        Assert.Equal(FakeDumpTools.PostgresDump, await File.ReadAllBytesAsync(path));
        Assert.Equal(FakeDumpTools.PostgresDump.Length, (await restarted.GetBackupAsync(backupId)).GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task Restart_PendingBackupWhoseQueueEntryWasLost_IsExecuted()
    {
        Guid backupId, jobId;
        using (var before = StoppedWorker())
        using (var client = before.CreateClient())
        {
            var instanceId = await before.CreateRunningInstanceAsync(client);
            var databaseId = await before.CreateReadyDatabaseAsync(client, instanceId, "app");
            (backupId, jobId) = await client.CreateBackupAsync(databaseId);
        }

        using var after = RestartedApplication();
        using var restarted = after.CreateClient();

        Assert.Equal("completed", (await restarted.WaitForFinishedJobAsync(jobId)).Status());
        Assert.Equal("completed", (await restarted.GetBackupAsync(backupId)).Status());
        Assert.Single(after.BackupFiles());
    }

    [Fact]
    public async Task Recovery_BackupJobWithExpiredLease_IsReturnedToPending_WithItsBackupStillRunning()
    {
        using var factory = StoppedWorker();
        using var client = factory.CreateClient();
        var (_, databaseId, backupId, jobId) = await InterruptedBackupAsync(factory, client);

        var recovered = await factory.RecoverJobsAsync(includePending: false);

        Assert.Equal([jobId], recovered);
        var job = await factory.GetJobEntityAsync(jobId);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(backupId, job.BackupId);
        Assert.Equal(databaseId, job.DatabaseId);
        var backup = await factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync());
        Assert.Equal(BackupStatus.Running, backup.Status);
    }

    [Fact]
    public async Task InterruptedBackup_StillBlocksANewBackupAndTheDatabasesDeletion_UntilItFinishes()
    {
        using var factory = StoppedWorker();
        using var client = factory.CreateClient();
        var (_, databaseId, _, jobId) = await InterruptedBackupAsync(factory, client);

        var secondBackup = await client.PostAsync(ApiClientExtensions.DatabaseBackupsUrl(databaseId), content: null);
        var deletion = await client.DeleteAsync($"{ApiClientExtensions.DatabasesUrl}/{databaseId}");

        await secondBackup.AssertErrorAsync(System.Net.HttpStatusCode.Conflict, "BACKUP_OPERATION_IN_PROGRESS");
        await deletion.AssertErrorAsync(System.Net.HttpStatusCode.Conflict, "BACKUP_OPERATION_IN_PROGRESS");

        await factory.RecoverJobsAsync(includePending: false);
        await factory.ProcessJobAsync(jobId);

        await client.CreateBackupAsync(databaseId);
    }
}
