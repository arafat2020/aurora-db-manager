using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuroraDbManager.Api.Application.BackupSchedules;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Backups;
using AuroraDbManager.Api.Tests.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Schedules;

/// <summary>
/// The scheduler: what a pass does with schedules that are due, missed, or not due. The
/// application's clock is a test clock that starts at Tuesday 10 March 2026, 10:00 UTC and is
/// moved by the tests; a pass is made with <see cref="ApiFactory.RunSchedulerAsync"/>. A
/// "restart" disposes one factory and starts another on the same system database.
/// </summary>
public sealed class BackupSchedulerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 3, 10, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly TempDatabase _database = new();
    private readonly string _backupRoot = Path.Combine(Path.GetTempPath(), $"aurora-scheduler-tests-{Guid.NewGuid():N}");
    private readonly FakeS3ObjectStore _objects = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }

        _database.Dispose();
        if (Directory.Exists(_backupRoot))
        {
            Directory.Delete(_backupRoot, recursive: true);
        }
    }

    private (ApiFactory Factory, HttpClient Client) Application(BackupStorageType defaultStorage = BackupStorageType.Local, bool runWorker = false)
    {
        var factory = new ApiFactory
        {
            Clock = _clock,
            DatabasePath = _database.Path,
            BackupRootPath = _backupRoot,
            ObjectStore = _objects,
            RunWorker = runWorker,
            ConfigureBackups = options =>
            {
                options.StorageType = defaultStorage;
                options.S3.Bucket = FakeS3ObjectStore.Bucket;
                options.S3.Region = "us-east-1";
            }
        };
        var client = factory.CreateClient();
        _disposables.Add(factory);
        _disposables.Add(client);
        return (factory, client);
    }

    private static string ScheduleUrl(Guid databaseId) => $"{DatabasesUrl}/{databaseId}/backup-schedule";

    private static DateTimeOffset Utc(int day, int hour, int minute = 0) => new(2026, 3, day, hour, minute, 0, TimeSpan.Zero);

    private void MoveClockTo(DateTimeOffset instant) => _clock.SetUtcNow(instant);

    /// <summary>A ready database with a schedule: daily at 02:00 UTC unless said otherwise, first due on 11 March.</summary>
    private static async Task<(Guid InstanceId, Guid DatabaseId)> ScheduledDatabaseAsync(
        ApiFactory factory, HttpClient client, string cron = "0 2 * * *", string timeZone = "UTC", string name = "app", string engine = "postgres")
    {
        var instanceId = await factory.CreateRunningInstanceAsync(client, name: $"instance-{name}", engine: engine);
        var databaseId = await factory.CreateReadyDatabaseAsync(client, instanceId, name);
        var response = await client.PostAsJsonAsync(ScheduleUrl(databaseId), new { cronExpression = cron, timeZoneId = timeZone });
        await response.ReadJsonAsync(HttpStatusCode.Created);
        return (instanceId, databaseId);
    }

    private static async Task<DateTimeOffset?> NextRunAsync(HttpClient client, Guid databaseId)
    {
        var nextRunAt = (await (await client.GetAsync(ScheduleUrl(databaseId))).ReadJsonAsync(HttpStatusCode.OK)).GetProperty("nextRunAt");
        return nextRunAt.ValueKind == JsonValueKind.Null ? null : nextRunAt.GetDateTimeOffset();
    }

    private static Task<List<Backup>> BackupsAsync(ApiFactory factory) =>
        factory.WithDbAsync(db => db.Backups.AsNoTracking().OrderBy(b => b.CreatedAt).ToListAsync());

    private static Task<int> BackupJobCountAsync(ApiFactory factory) =>
        factory.WithDbAsync(db => db.Jobs.CountAsync(j => j.Type == JobType.BackupDatabase));

    // --- Due, not due -------------------------------------------------------------------------

    [Fact]
    public async Task DueSchedule_CreatesAnOrdinaryBackupWithAnOrdinaryBackupJob_AndMovesOnToItsNextOccurrence()
    {
        var (factory, client) = Application();
        var (instanceId, databaseId) = await ScheduledDatabaseAsync(factory, client);
        MoveClockTo(Utc(11, 2).AddSeconds(3));

        var jobIds = await factory.RunSchedulerAsync();

        var jobId = Assert.Single(jobIds);
        var job = await client.GetJobAsync(jobId);
        // No job type of its own: the same job a manual backup gets.
        Assert.Equal("backup_database", job.GetProperty("type").GetString());
        Assert.Equal("pending", job.Status());
        Assert.Equal(instanceId, job.GetProperty("instanceId").GetGuid());
        Assert.Equal(databaseId, job.GetProperty("databaseId").GetGuid());

        var backup = Assert.Single(await BackupsAsync(factory));
        Assert.Equal(job.GetProperty("backupId").GetGuid(), backup.Id);
        Assert.Equal(databaseId, backup.DatabaseId);
        Assert.Equal(BackupStatus.Pending, backup.Status);

        // The occurrence of the 11th has been dealt with; the 12th is next.
        Assert.Equal(Utc(12, 2), await NextRunAsync(client, databaseId));
        // The scheduler itself dumped nothing and stored nothing.
        Assert.Equal(0, factory.DumpTools.RunCount);
        Assert.Empty(factory.BackupFiles());
    }

    [Fact]
    public async Task ScheduledBackup_IsCarriedOutByTheExistingPipeline_AndIsListedLikeAnyOther()
    {
        var (factory, client) = Application();
        var (instanceId, databaseId) = await ScheduledDatabaseAsync(factory, client);
        var manual = await factory.CreateCompletedBackupAsync(client, databaseId);
        MoveClockTo(Utc(11, 2));

        var jobId = Assert.Single(await factory.RunSchedulerAsync());
        await factory.ProcessJobAsync(jobId);

        var job = await client.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        var scheduled = await client.GetBackupAsync(job.GetProperty("backupId").GetGuid());
        Assert.Equal("completed", scheduled.Status());
        // Dumped, stored and checksummed exactly as a manual backup is.
        Assert.Equal("sha256", scheduled.GetProperty("checksumAlgorithm").GetString());
        Assert.Equal(FakeDumpTools.PostgresDump.Length, scheduled.GetProperty("sizeBytes").GetInt64());
        Assert.True(File.Exists(factory.BackupFilePath(instanceId, databaseId, scheduled.GetProperty("id").GetGuid(), "dump")));

        var list = await (await client.GetAsync(DatabaseBackupsUrl(databaseId))).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(
            [scheduled.GetProperty("id").GetGuid(), manual],
            list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        Assert.Equal(
            scheduled.EnumerateObject().Select(property => property.Name),
            (await client.GetBackupAsync(manual)).EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [InlineData(BackupStorageType.Local, "local")]
    [InlineData(BackupStorageType.S3, "s3")]
    public async Task ScheduledBackup_GoesToTheSameDefaultStorageAsAManualOne(BackupStorageType defaultStorage, string expected)
    {
        var (factory, client) = Application(defaultStorage);
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client);
        var manual = await factory.CreateCompletedBackupAsync(client, databaseId);
        MoveClockTo(Utc(11, 2));

        var jobId = Assert.Single(await factory.RunSchedulerAsync());
        await factory.ProcessJobAsync(jobId);

        var scheduled = await client.GetBackupAsync((await client.GetJobAsync(jobId)).GetProperty("backupId").GetGuid());
        Assert.Equal("completed", scheduled.Status());
        Assert.Equal(expected, scheduled.GetProperty("storageType").GetString());
        Assert.Equal(expected, (await client.GetBackupAsync(manual)).GetProperty("storageType").GetString());
        Assert.Equal(expected == "local" ? 2 : 0, factory.BackupFiles().Count);
        Assert.Equal(expected == "s3" ? 2 : 0, _objects.ObjectsIn().Count);
    }

    [Fact]
    public async Task ScheduleInATimeZone_BecomesDueAtThatZonesTime_NotAtUtcsAndNotAtTheServers()
    {
        var (factory, client) = Application();
        // 02:00 in Dhaka is 20:00 UTC.
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client, "0 2 * * *", "Asia/Dhaka");

        MoveClockTo(Utc(10, 19, 59));
        Assert.Empty(await factory.RunSchedulerAsync());

        MoveClockTo(Utc(10, 20));
        Assert.Single(await factory.RunSchedulerAsync());
        Assert.Equal(Utc(11, 20), await NextRunAsync(client, databaseId));
    }

    [Fact]
    public async Task ScheduleThatIsNotDueYet_DoesNothing_UpToTheVeryMomentItIsDue()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client);

        Assert.Empty(await factory.RunSchedulerAsync());
        MoveClockTo(Utc(11, 2).AddMilliseconds(-1));
        Assert.Empty(await factory.RunSchedulerAsync());
        Assert.Empty(await BackupsAsync(factory));
        Assert.Equal(Utc(11, 2), await NextRunAsync(client, databaseId));

        // The clock reaches the scheduled time exactly.
        MoveClockTo(Utc(11, 2));
        Assert.Single(await factory.RunSchedulerAsync());
    }

    [Fact]
    public async Task SameOccurrence_IsNeverRunTwice_HoweverManyPassesFollow()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client);
        MoveClockTo(Utc(11, 2).AddSeconds(3));
        var jobId = Assert.Single(await factory.RunSchedulerAsync());

        for (var pass = 0; pass < 5; pass++)
        {
            _clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Empty(await factory.RunSchedulerAsync());
        }

        // Not while the backup is unfinished, and not after it has finished either.
        await factory.ProcessJobAsync(jobId);
        _clock.Advance(TimeSpan.FromHours(5));
        Assert.Empty(await factory.RunSchedulerAsync());

        Assert.Single(await BackupsAsync(factory));
        Assert.Equal(Utc(12, 2), await NextRunAsync(client, databaseId));
    }

    [Fact]
    public async Task EachOccurrenceInTurn_GetsItsOwnBackup()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client, "0 */6 * * *");

        foreach (var occurrence in new[] { Utc(10, 12), Utc(10, 18), Utc(11, 0) })
        {
            MoveClockTo(occurrence.AddSeconds(10));
            await factory.ProcessJobAsync(Assert.Single(await factory.RunSchedulerAsync()));
        }

        var backups = await BackupsAsync(factory);
        Assert.Equal(3, backups.Count);
        Assert.All(backups, backup => Assert.Equal(BackupStatus.Completed, backup.Status));
        Assert.Equal(Utc(11, 6), await NextRunAsync(client, databaseId));
    }

    [Fact]
    public async Task DisabledSchedule_DoesNothing()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client);
        await client.PutAsJsonAsync(ScheduleUrl(databaseId), new { cronExpression = "0 2 * * *", timeZoneId = "UTC", enabled = false });
        MoveClockTo(Utc(12, 9));

        Assert.Empty(await factory.RunSchedulerAsync());
        Assert.Empty(await BackupsAsync(factory));
        Assert.Null(await NextRunAsync(client, databaseId));
    }

    [Fact]
    public async Task SeveralSchedulesDueAtOnce_EachGetsOneBackup_AndOthersAreLeftAlone()
    {
        var (factory, client) = Application();
        var (_, first) = await ScheduledDatabaseAsync(factory, client, "0 2 * * *", name: "first");
        var (_, second) = await ScheduledDatabaseAsync(factory, client, "30 1 * * *", name: "second");
        var (_, notDue) = await ScheduledDatabaseAsync(factory, client, "0 9 * * *", name: "later");
        MoveClockTo(Utc(11, 2, 5));

        var jobIds = await factory.RunSchedulerAsync();

        Assert.Equal(2, jobIds.Count);
        Assert.Equal(new[] { first, second }.Order(), (await BackupsAsync(factory)).Select(backup => backup.DatabaseId).Order());
        Assert.Equal(Utc(11, 9), await NextRunAsync(client, notDue));
        Assert.Equal(Utc(12, 1, 30), await NextRunAsync(client, second));
    }

    // --- Missed occurrences -------------------------------------------------------------------

    [Fact]
    public async Task OccurrenceMissedByHours_IsMadeUpForOnce_AndTheScheduleMovesOnToTheNextFutureOccurrence()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client);
        // Daily at 02:00; it is now 05:00 and nothing has run.
        MoveClockTo(Utc(11, 5));

        Assert.Single(await factory.RunSchedulerAsync());

        Assert.Single(await BackupsAsync(factory));
        Assert.Equal(Utc(12, 2), await NextRunAsync(client, databaseId));
        Assert.Empty(await factory.RunSchedulerAsync());
    }

    [Fact]
    public async Task SevenDaysOfMissedOccurrences_ProduceOneBackup_NotSeven()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client);
        // The server was off from the 10th to the 18th: the 11th to the 18th were all missed.
        MoveClockTo(Utc(18, 5));

        var jobIds = await factory.RunSchedulerAsync();

        Assert.Single(jobIds);
        Assert.Single(await BackupsAsync(factory));
        Assert.Equal(1, await BackupJobCountAsync(factory));
        // Straight to the first occurrence that is still to come.
        Assert.Equal(Utc(19, 2), await NextRunAsync(client, databaseId));

        await factory.ProcessJobAsync(jobIds[0]);
        Assert.Empty(await factory.RunSchedulerAsync());
        Assert.Single(await BackupsAsync(factory));
    }

    [Fact]
    public async Task EveryMinuteSchedule_BehindByADay_ProducesOneBackup_NotFourteenHundred()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client, "* * * * *");
        MoveClockTo(Utc(11, 10).AddSeconds(20));

        Assert.Single(await factory.RunSchedulerAsync());

        Assert.Single(await BackupsAsync(factory));
        Assert.Equal(Utc(11, 10, 1), await NextRunAsync(client, databaseId));
    }

    // --- A database that is busy or unavailable -----------------------------------------------

    [Fact]
    public async Task UnfinishedBackupAlreadyExists_NoSecondBackupIsCreated_AndTheScheduleStillMovesOn()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client);
        var (manualId, manualJobId) = await client.CreateBackupAsync(databaseId);
        MoveClockTo(Utc(11, 2).AddSeconds(5));

        var jobIds = await factory.RunSchedulerAsync();

        Assert.Empty(jobIds);
        Assert.Equal(manualId, Assert.Single(await BackupsAsync(factory)).Id);
        Assert.Equal(1, await BackupJobCountAsync(factory));
        // Not stuck on the occurrence it could not use.
        Assert.Equal(Utc(12, 2), await NextRunAsync(client, databaseId));
        Assert.Empty(await factory.RunSchedulerAsync());

        // The manual backup is untouched and completes as it would have.
        await factory.ProcessJobAsync(manualJobId);
        Assert.Equal("completed", (await client.GetBackupAsync(manualId)).Status());
        Assert.Contains(factory.Logs.Entries, entry => entry.Contains("Scheduled backup skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LongRunningScheduledBackup_OverlappingTheNextOccurrences_CausesNoBacklog()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client, "0 * * * *");
        MoveClockTo(Utc(10, 11));
        var firstJobId = Assert.Single(await factory.RunSchedulerAsync());

        // The 11:00 backup is still unfinished at 12:00, 13:00 and 14:00.
        foreach (var hour in new[] { 12, 13, 14 })
        {
            MoveClockTo(Utc(10, hour).AddSeconds(1));
            Assert.Empty(await factory.RunSchedulerAsync());
        }

        Assert.Single(await BackupsAsync(factory));
        Assert.Equal(Utc(10, 15), await NextRunAsync(client, databaseId));

        // It finishes; the next occurrence gets a backup again, and only one.
        await factory.ProcessJobAsync(firstJobId);
        MoveClockTo(Utc(10, 15));
        Assert.Single(await factory.RunSchedulerAsync());
        Assert.Equal(2, (await BackupsAsync(factory)).Count);
    }

    [Fact]
    public async Task DatabaseBeingRestored_IsNotBackedUpBySchedule_AndTheScheduleMovesOn()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client);
        var backupId = await factory.CreateCompletedBackupAsync(client, databaseId);
        await ApiFactory.RequestRestoreAsync(client, backupId);
        MoveClockTo(Utc(11, 2));

        Assert.Empty(await factory.RunSchedulerAsync());

        Assert.Single(await BackupsAsync(factory));
        Assert.Equal(Utc(12, 2), await NextRunAsync(client, databaseId));
    }

    [Theory]
    [InlineData(InstanceStatus.Stopped)]
    [InlineData(InstanceStatus.Failed)]
    public async Task InstanceNotRunning_OccurrenceIsSkipped_NothingIsStarted_AndALaterOccurrenceRunsOnceItIsBack(InstanceStatus status)
    {
        var (factory, client) = Application();
        var (instanceId, databaseId) = await ScheduledDatabaseAsync(factory, client);
        await factory.SetInstanceStatusAsync(instanceId, status);
        MoveClockTo(Utc(11, 2));

        Assert.Empty(await factory.RunSchedulerAsync());

        Assert.Empty(await BackupsAsync(factory));
        Assert.Equal(Utc(12, 2), await NextRunAsync(client, databaseId));
        Assert.Empty(factory.Provisioner.EnsureRunningInstanceIds);

        await factory.SetInstanceStatusAsync(instanceId, InstanceStatus.Running);
        MoveClockTo(Utc(12, 2));
        Assert.Single(await factory.RunSchedulerAsync());
    }

    [Fact]
    public async Task DatabaseBeingDeleted_IsNotBackedUp_AndOnceItIsGone_SoIsItsSchedule()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client);
        var deleteJobId = await client.DeleteDatabaseAsync(databaseId);
        MoveClockTo(Utc(11, 2));

        Assert.Empty(await factory.RunSchedulerAsync());
        Assert.Empty(await BackupsAsync(factory));

        await factory.ProcessJobAsync(deleteJobId);
        MoveClockTo(Utc(12, 2));

        Assert.Empty(await factory.RunSchedulerAsync());
        Assert.Equal(0, await factory.WithDbAsync(db => db.BackupSchedules.CountAsync()));
    }

    [Fact]
    public async Task FailedScheduledBackup_IsOnTheBackupAndItsJob_AndTheScheduleCarriesOn()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client);
        MoveClockTo(Utc(11, 2));
        var jobId = Assert.Single(await factory.RunSchedulerAsync());
        factory.DumpTools.FailNextRuns(3);

        await factory.ProcessJobAsync(jobId);

        Assert.Equal("failed", (await client.GetJobAsync(jobId)).Status());
        Assert.Equal(BackupStatus.Failed, Assert.Single(await BackupsAsync(factory)).Status);
        // The schedule has no status of its own to change: still enabled, still owing its next run.
        var schedule = await (await client.GetAsync(ScheduleUrl(databaseId))).ReadJsonAsync(HttpStatusCode.OK);
        Assert.True(schedule.GetProperty("enabled").GetBoolean());
        Assert.Equal(Utc(12, 2), schedule.GetProperty("nextRunAt").GetDateTimeOffset());

        MoveClockTo(Utc(12, 2));
        await factory.ProcessJobAsync(Assert.Single(await factory.RunSchedulerAsync()));
        Assert.Equal(BackupStatus.Completed, (await BackupsAsync(factory))[^1].Status);
    }

    // --- Changes while a schedule is due ------------------------------------------------------

    [Fact]
    public async Task ScheduleChangedWhileDue_TheOldOccurrenceIsNotRun_AndTheNewScheduleCountsFromNow()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client);
        MoveClockTo(Utc(11, 5));

        // Due since 02:00, and changed before the scheduler got to it.
        await client.PutAsJsonAsync(ScheduleUrl(databaseId), new { cronExpression = "0 9 * * *", timeZoneId = "UTC" });

        Assert.Empty(await factory.RunSchedulerAsync());
        Assert.Empty(await BackupsAsync(factory));
        Assert.Equal(Utc(11, 9), await NextRunAsync(client, databaseId));
    }

    [Fact]
    public async Task ScheduleDeletedWhileDue_NothingIsRun()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client);
        MoveClockTo(Utc(11, 5));
        await client.DeleteAsync(ScheduleUrl(databaseId));

        Assert.Empty(await factory.RunSchedulerAsync());
        Assert.Empty(await BackupsAsync(factory));
    }

    // --- Passes that overlap ------------------------------------------------------------------

    [Fact]
    public async Task OverlappingPasses_CreateOneBackupPerDueSchedule()
    {
        var (factory, client) = Application();
        var databases = new List<Guid>();
        foreach (var name in new[] { "one", "two", "three" })
        {
            databases.Add((await ScheduledDatabaseAsync(factory, client, name: name)).DatabaseId);
        }

        MoveClockTo(Utc(11, 2).AddSeconds(1));

        var passes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => factory.RunSchedulerAsync()));

        // Eight passes found the same three occurrences due; each was claimed exactly once.
        Assert.Equal(3, passes.Sum(pass => pass.Count));
        var backups = await BackupsAsync(factory);
        Assert.Equal(databases.Order(), backups.Select(backup => backup.DatabaseId).Order());
        Assert.Equal(3, await BackupJobCountAsync(factory));
        foreach (var databaseId in databases)
        {
            Assert.Equal(Utc(12, 2), await NextRunAsync(client, databaseId));
        }
    }

    [Fact]
    public async Task PassThatLosesTheClaim_LeavesNoBackupAndNoJobBehind()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client);
        MoveClockTo(Utc(11, 2));

        // A pass that has read the schedule as due, and is overtaken by another before it saves.
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var overtaken = services.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
        var loaded = await overtaken.BackupSchedules.SingleAsync();
        var winner = Assert.Single(await factory.RunSchedulerAsync());

        loaded.Advance(Utc(12, 2).UtcDateTime, _clock.GetUtcNow().UtcDateTime);
        overtaken.Backups.Add(Backup.Create(databaseId, BackupStorageType.Local, _clock.GetUtcNow().UtcDateTime));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => overtaken.SaveChangesAsync());

        // The claim is part of the same transaction as the backup: refused together.
        var backup = Assert.Single(await BackupsAsync(factory));
        Assert.Equal((await client.GetJobAsync(winner)).GetProperty("backupId").GetGuid(), backup.Id);
    }

    [Fact]
    public async Task ManualBackupSlippingInBetweenTheCheckAndTheSave_IsNotDuplicated_AndTheScheduleStillMovesOn()
    {
        var (factory, client) = Application();
        var (_, databaseId) = await ScheduledDatabaseAsync(factory, client);
        MoveClockTo(Utc(11, 2));

        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var options = new DbContextOptionsBuilder<Infrastructure.Persistence.AppDbContext>(
                services.GetRequiredService<DbContextOptions<Infrastructure.Persistence.AppDbContext>>())
            .AddInterceptors(new BeforeFirstSaveInterceptor(() => client.CreateBackupAsync(databaseId)))
            .Options;
        await using var db = new Infrastructure.Persistence.AppDbContext(options);
        var backups = new Application.Backups.BackupService(
            db,
            services.GetRequiredService<Application.Backups.IBackupStorage>(),
            services.GetRequiredService<Application.Jobs.JobQueue>(),
            services.GetRequiredService<IOptions<Application.Jobs.JobOptions>>(),
            _clock,
            NullLogger<Application.Backups.BackupService>.Instance);
        var scheduler = new BackupScheduler(
            db,
            backups,
            services.GetRequiredService<IScheduleCalculator>(),
            _clock,
            services.GetRequiredService<Application.Monitoring.AuroraMetrics>(),
            services.GetRequiredService<SchedulerHeartbeat>(),
            NullLogger<BackupScheduler>.Instance);

        var jobIds = await scheduler.RunDueAsync(default);

        Assert.Empty(jobIds);
        // One backup: the manual one. The scheduler's was refused by the index, with its job.
        Assert.Single(await BackupsAsync(factory));
        Assert.Equal(1, await BackupJobCountAsync(factory));
        Assert.Equal(Utc(12, 2), await NextRunAsync(client, databaseId));
    }

    // --- Restarts -----------------------------------------------------------------------------

    [Fact]
    public async Task Restart_OccurrenceAlreadyCommittedBeforeTheProcessStopped_IsNotRunAgain_AndItsJobIsRecoveredAndFinished()
    {
        Guid databaseId, jobId;
        {
            var (before, client) = Application();
            (_, databaseId) = await ScheduledDatabaseAsync(before, client);
            MoveClockTo(Utc(11, 2));
            // Claimed, backup and job stored, schedule moved on; the process stops before the job ran.
            jobId = Assert.Single(await before.RunSchedulerAsync());
            client.Dispose();
            before.Dispose();
        }

        var (after, restarted) = Application(runWorker: true);

        // Nothing in memory survived; the database says the occurrence was dealt with.
        Assert.Empty(await after.RunSchedulerAsync());
        // And the job that was never run is found by the existing job recovery, and runs.
        var job = await restarted.WaitForFinishedJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(BackupStatus.Completed, Assert.Single(await BackupsAsync(after)).Status);
        Assert.Equal(Utc(12, 2), await NextRunAsync(restarted, databaseId));
    }

    [Fact]
    public async Task Restart_OccurrenceThatCameDueWhileTheProcessWasDown_IsRunOnceAfterwards()
    {
        Guid databaseId;
        {
            var (before, client) = Application();
            (_, databaseId) = await ScheduledDatabaseAsync(before, client);
            client.Dispose();
            before.Dispose();
        }

        // Down across the 11th, 12th and 13th at 02:00; back on the 13th at 04:00.
        MoveClockTo(Utc(13, 4));
        var (after, restarted) = Application();

        Assert.Single(await after.RunSchedulerAsync());
        Assert.Empty(await after.RunSchedulerAsync());

        Assert.Single(await BackupsAsync(after));
        Assert.Equal(Utc(14, 2), await NextRunAsync(restarted, databaseId));
    }

    // --- The worker ---------------------------------------------------------------------------

    [Fact]
    public async Task Worker_MakesAPassAtStartup_AndThenAtEveryInterval()
    {
        var (factory, client) = Application();
        var (_, first) = await ScheduledDatabaseAsync(factory, client, "0 2 * * *", name: "first");
        var (_, second) = await ScheduledDatabaseAsync(factory, client, "5 2 * * *", name: "second");
        MoveClockTo(Utc(11, 2));
        var worker = new ScheduledBackupWorker(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new BackupOptions { Scheduler = new BackupSchedulerOptions { PollIntervalSeconds = 30 } }),
            _clock,
            NullLogger<ScheduledBackupWorker>.Instance);

        await worker.StartAsync(default);
        try
        {
            // The pass at startup: what is already due does not wait an interval.
            await WaitUntilAsync(async () => (await BackupsAsync(factory)).Count == 1);
            Assert.Equal(first, (await BackupsAsync(factory))[0].DatabaseId);

            // Nothing more happens until the clock has moved by an interval; then the next pass.
            _clock.Advance(TimeSpan.FromMinutes(5));
            await WaitUntilAsync(async () => (await BackupsAsync(factory)).Count == 2);
            Assert.Equal(second, (await BackupsAsync(factory))[1].DatabaseId);
        }
        finally
        {
            await worker.StopAsync(default);
        }

        Assert.Equal(2, (await BackupsAsync(factory)).Count);
    }

    [Fact]
    public void Worker_IsPartOfTheRealApplication_AlongsideTheJobWorker()
    {
        // The test host removes it so tests decide when a pass happens; every real host registers it,
        // with the rest of what a host is made of.
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "AuroraDbManager.Api", "AuroraHost.cs"));

        Assert.Contains("AddHostedService<ScheduledBackupWorker>()", program);
        Assert.Contains("AddHostedService<JobWorker>()", program);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AuroraDbManager.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private sealed class BeforeFirstSaveInterceptor(Func<Task> action) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private bool _ran;

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_ran)
            {
                _ran = true;
                await action();
            }

            return result;
        }
    }
}
