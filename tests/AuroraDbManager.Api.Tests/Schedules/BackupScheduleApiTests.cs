using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuroraDbManager.Api.Domain.BackupSchedules;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Schedules;

/// <summary>
/// The backup schedule API. The application's clock is a test clock standing at Tuesday
/// 10 March 2026, 10:00 UTC, so every expected next run is a known instant.
/// </summary>
public sealed class BackupScheduleApiTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 3, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly string[] ScheduleFields =
        ["createdAt", "cronExpression", "databaseId", "enabled", "id", "nextRunAt", "timeZoneId", "updatedAt"];

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public BackupScheduleApiTests()
    {
        _factory = new ApiFactory { Clock = _clock };
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static string ScheduleUrl(Guid databaseId) => $"{DatabasesUrl}/{databaseId}/backup-schedule";

    private static DateTimeOffset Utc(int day, int hour, int minute = 0) => new(2026, 3, day, hour, minute, 0, TimeSpan.Zero);

    private async Task<Guid> CreateReadyDatabaseAsync(string name = "app")
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client, name: $"instance-{name}");
        return await _factory.CreateReadyDatabaseAsync(_client, instanceId, name);
    }

    private Task<HttpResponseMessage> PostAsync(Guid databaseId, string? cron = "0 2 * * *", string? timeZone = "UTC", bool? enabled = null) =>
        _client.PostAsJsonAsync(ScheduleUrl(databaseId), new { cronExpression = cron, timeZoneId = timeZone, enabled });

    private Task<HttpResponseMessage> PutAsync(Guid databaseId, string? cron, string? timeZone, bool? enabled = null) =>
        _client.PutAsJsonAsync(ScheduleUrl(databaseId), new { cronExpression = cron, timeZoneId = timeZone, enabled });

    private async Task<JsonElement> GetScheduleAsync(Guid databaseId) =>
        await (await _client.GetAsync(ScheduleUrl(databaseId))).ReadJsonAsync(HttpStatusCode.OK);

    private Task<int> ScheduleCountAsync() => _factory.WithDbAsync(db => db.BackupSchedules.CountAsync());

    // --- Create -------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ReturnsCreatedWithTheSchedule_AndItsNextRunAsAUtcInstant()
    {
        var databaseId = await CreateReadyDatabaseAsync();

        var response = await PostAsync(databaseId, "0 2 * * *", "Asia/Dhaka", enabled: true);

        var body = await response.ReadJsonAsync(HttpStatusCode.Created);
        Assert.Equal(ScheduleFields, body.EnumerateObject().Select(property => property.Name).Order());
        Assert.NotEqual(Guid.Empty, body.GetProperty("id").GetGuid());
        Assert.Equal(databaseId, body.GetProperty("databaseId").GetGuid());
        Assert.Equal("0 2 * * *", body.GetProperty("cronExpression").GetString());
        Assert.Equal("Asia/Dhaka", body.GetProperty("timeZoneId").GetString());
        Assert.True(body.GetProperty("enabled").GetBoolean());
        // 02:00 in Dhaka is 20:00 UTC: the same day, ten hours from now.
        Assert.Equal(Utc(10, 20), body.GetProperty("nextRunAt").GetDateTimeOffset());
        Assert.EndsWith("Z", body.GetProperty("nextRunAt").GetString());
        Assert.Equal(Start, body.GetProperty("createdAt").GetDateTimeOffset());

        Assert.NotNull(response.Headers.Location);
        Assert.Equal(ScheduleUrl(databaseId), response.Headers.Location.AbsolutePath);

        var stored = await _factory.WithDbAsync(db => db.BackupSchedules.AsNoTracking().SingleAsync());
        Assert.Equal(Utc(10, 20).UtcDateTime, stored.NextRunAt);
    }

    [Fact]
    public async Task Create_BacksUpNothing_AndIsNotDueUntilItsFirstOccurrence()
    {
        var databaseId = await CreateReadyDatabaseAsync();

        // Every minute: as close to "now" as a schedule can be.
        await (await PostAsync(databaseId, "* * * * *")).ReadJsonAsync(HttpStatusCode.Created);

        Assert.Equal(0, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
        Assert.Empty(await _factory.RunSchedulerAsync());
        Assert.Equal(Utc(10, 10, 1), (await GetScheduleAsync(databaseId)).GetProperty("nextRunAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Create_EnabledDefaultsToTrue_AndADisabledScheduleHasNoNextRun()
    {
        var first = await CreateReadyDatabaseAsync("first");
        var second = await CreateReadyDatabaseAsync("second");

        var byDefault = await (await _client.PostAsJsonAsync(ScheduleUrl(first), new { cronExpression = "0 2 * * *", timeZoneId = "UTC" })).ReadJsonAsync(HttpStatusCode.Created);
        var disabled = await (await PostAsync(second, enabled: false)).ReadJsonAsync(HttpStatusCode.Created);

        Assert.True(byDefault.GetProperty("enabled").GetBoolean());
        Assert.Equal(Utc(11, 2), byDefault.GetProperty("nextRunAt").GetDateTimeOffset());
        Assert.False(disabled.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, disabled.GetProperty("nextRunAt").ValueKind);
    }

    [Fact]
    public async Task Create_StoresTheExpressionNormalized()
    {
        var databaseId = await CreateReadyDatabaseAsync();

        var body = await (await PostAsync(databaseId, "  0   2 * *   * ")).ReadJsonAsync(HttpStatusCode.Created);

        Assert.Equal("0 2 * * *", body.GetProperty("cronExpression").GetString());
    }

    [Fact]
    public async Task Create_WhenTheDatabaseAlreadyHasASchedule_IsRejected_AndTheExistingOneIsUnchanged()
    {
        var databaseId = await CreateReadyDatabaseAsync();
        await PostAsync(databaseId, "0 2 * * *");

        var response = await PostAsync(databaseId, "0 5 * * *");

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "BACKUP_SCHEDULE_ALREADY_EXISTS");
        Assert.Equal("0 2 * * *", (await GetScheduleAsync(databaseId)).GetProperty("cronExpression").GetString());
        Assert.Equal(1, await ScheduleCountAsync());
    }

    [Fact]
    public async Task Create_RequestedConcurrently_ExactlyOneScheduleExists()
    {
        var databaseId = await CreateReadyDatabaseAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostAsync(databaseId)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        foreach (var rejected in responses.Where(response => response.StatusCode != HttpStatusCode.Created))
        {
            await rejected.AssertErrorAsync(HttpStatusCode.Conflict, "BACKUP_SCHEDULE_ALREADY_EXISTS");
        }

        Assert.Equal(1, await ScheduleCountAsync());
    }

    [Fact]
    public async Task UniqueIndex_AllowsOneSchedulePerDatabase_WithoutTheService()
    {
        var databaseId = await CreateReadyDatabaseAsync();

        Task InsertAsync() => _factory.WithDbAsync(async db =>
        {
            db.BackupSchedules.Add(BackupSchedule.Create(databaseId, "0 2 * * *", "UTC", true, Utc(11, 2).UtcDateTime, Start.UtcDateTime));
            return await db.SaveChangesAsync();
        });

        await InsertAsync();
        await Assert.ThrowsAsync<DbUpdateException>(InsertAsync);
    }

    [Fact]
    public async Task ForeignKeyAndCheckConstraint_RejectAScheduleWithoutDatabase_AndADisabledOneWithANextRun()
    {
        var databaseId = await CreateReadyDatabaseAsync();
        await PostAsync(databaseId, enabled: false);

        await Assert.ThrowsAsync<DbUpdateException>(() => _factory.WithDbAsync(async db =>
        {
            db.BackupSchedules.Add(BackupSchedule.Create(Guid.NewGuid(), "0 2 * * *", "UTC", false, null, Start.UtcDateTime));
            return await db.SaveChangesAsync();
        }));
        await Assert.ThrowsAnyAsync<DbException>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"UPDATE backup_schedules SET next_run_at = '2026-03-11 02:00:00' WHERE database_id = {databaseId}")));
    }

    [Fact]
    public async Task Create_UnknownDatabase_ReturnsDatabaseNotFound()
    {
        var response = await PostAsync(Guid.NewGuid());

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
        Assert.Equal(0, await ScheduleCountAsync());
    }

    [Fact]
    public async Task Create_DatabaseThatIsNotReady_IsRejected()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var (creating, _) = await _client.CreateDatabaseAsync(instanceId, "creating");
        var deleting = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "deleting");
        await _client.DeleteDatabaseAsync(deleting);

        foreach (var databaseId in new[] { creating, deleting })
        {
            await (await PostAsync(databaseId)).AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_NOT_READY");
        }

        Assert.Equal(0, await ScheduleCountAsync());
    }

    [Theory]
    [InlineData(InstanceStatus.Stopped)]
    [InlineData(InstanceStatus.Failed)]
    public async Task Create_InstanceNotRunning_IsRejectedAndNotStarted(InstanceStatus status)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        await _factory.SetInstanceStatusAsync(instanceId, status);

        var response = await PostAsync(databaseId);

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "INSTANCE_NOT_READY");
        Assert.Equal(0, await ScheduleCountAsync());
        Assert.Empty(_factory.Provisioner.EnsureRunningInstanceIds);
    }

    [Fact]
    public async Task Create_WhileTheDatabaseIsBeingBackedUp_IsAllowed()
    {
        var databaseId = await CreateReadyDatabaseAsync();
        await _client.CreateBackupAsync(databaseId);

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(databaseId)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0 2 * *")]
    [InlineData("0 0 2 * * *")]
    [InlineData("61 2 * * *")]
    [InlineData("@daily")]
    [InlineData("0 0 30 2 *")]
    [InlineData("whenever")]
    public async Task Create_InvalidCronExpression_IsRejected(string? cron)
    {
        var databaseId = await CreateReadyDatabaseAsync();

        var response = await PostAsync(databaseId, cron, "UTC");

        var error = await response.AssertErrorAsync(HttpStatusCode.BadRequest, "INVALID_CRON_EXPRESSION");
        Assert.DoesNotContain("Cronos", error.GetRawText());
        Assert.DoesNotContain("Exception", error.GetRawText());
        Assert.Equal(0, await ScheduleCountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+06:00")]
    [InlineData("Dhaka")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("Bangladesh Standard Time")]
    public async Task Create_InvalidTimeZone_IsRejected_AndNeverReplacedByUtcOrTheServersZone(string? timeZone)
    {
        var databaseId = await CreateReadyDatabaseAsync();

        var response = await PostAsync(databaseId, "0 2 * * *", timeZone);

        await response.AssertErrorAsync(HttpStatusCode.BadRequest, "INVALID_TIME_ZONE");
        Assert.Equal(0, await ScheduleCountAsync());
    }

    [Fact]
    public async Task Create_MalformedJson_OrAWrongType_ReturnsValidationError()
    {
        var databaseId = await CreateReadyDatabaseAsync();

        var malformed = await _client.PostAsync(ScheduleUrl(databaseId), new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));
        var wrongType = await _client.PostAsJsonAsync(ScheduleUrl(databaseId), new { cronExpression = "0 2 * * *", timeZoneId = "UTC", enabled = "yes" });

        await malformed.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await wrongType.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(0, await ScheduleCountAsync());
    }

    [Fact]
    public async Task Create_IgnoresAnythingElseTheClientSends()
    {
        var databaseId = await CreateReadyDatabaseAsync();

        var response = await _client.PostAsJsonAsync(ScheduleUrl(databaseId), new
        {
            cronExpression = "0 2 * * *",
            timeZoneId = "UTC",
            nextRunAt = "2020-01-01T00:00:00Z",
            storageType = "s3",
            retentionDays = 7,
            databaseId = Guid.NewGuid()
        });

        var body = await response.ReadJsonAsync(HttpStatusCode.Created);
        Assert.Equal(databaseId, body.GetProperty("databaseId").GetGuid());
        Assert.Equal(Utc(11, 2), body.GetProperty("nextRunAt").GetDateTimeOffset());
        Assert.Equal(ScheduleFields, body.EnumerateObject().Select(property => property.Name).Order());
    }

    // --- Get ----------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheSchedule()
    {
        var databaseId = await CreateReadyDatabaseAsync();
        var created = await (await PostAsync(databaseId, "0 3 * * 0", "Europe/Berlin")).ReadJsonAsync(HttpStatusCode.Created);

        var schedule = await GetScheduleAsync(databaseId);

        Assert.Equal(created.GetRawText(), schedule.GetRawText());
        // Sunday 15 March, 03:00 in Berlin (UTC+1 until the end of March).
        Assert.Equal(Utc(15, 2), schedule.GetProperty("nextRunAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Get_DatabaseWithoutSchedule_ReturnsScheduleNotFound_AndUnknownDatabase_DatabaseNotFound()
    {
        var databaseId = await CreateReadyDatabaseAsync();

        await (await _client.GetAsync(ScheduleUrl(databaseId))).AssertErrorAsync(HttpStatusCode.NotFound, "BACKUP_SCHEDULE_NOT_FOUND");
        await (await _client.GetAsync(ScheduleUrl(Guid.NewGuid()))).AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
        await (await _client.GetAsync($"{DatabasesUrl}/not-a-guid/backup-schedule")).AssertErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // --- Update -------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ChangedCronExpression_RecalculatesTheNextRun()
    {
        var databaseId = await CreateReadyDatabaseAsync();
        var created = await (await PostAsync(databaseId, "0 2 * * *")).ReadJsonAsync(HttpStatusCode.Created);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var response = await PutAsync(databaseId, "0 */6 * * *", "UTC");

        var body = await response.ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(created.GetProperty("id").GetGuid(), body.GetProperty("id").GetGuid());
        Assert.Equal("0 */6 * * *", body.GetProperty("cronExpression").GetString());
        Assert.Equal(Utc(10, 12), body.GetProperty("nextRunAt").GetDateTimeOffset());
        Assert.Equal(Start.AddMinutes(5), body.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(Start, body.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(body.GetRawText(), (await GetScheduleAsync(databaseId)).GetRawText());
    }

    [Fact]
    public async Task Update_ChangedTimeZone_RecalculatesTheNextRun()
    {
        var databaseId = await CreateReadyDatabaseAsync();
        await PostAsync(databaseId, "0 2 * * *", "UTC");

        var body = await (await PutAsync(databaseId, "0 2 * * *", "Asia/Dhaka")).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal("Asia/Dhaka", body.GetProperty("timeZoneId").GetString());
        // From 02:00 UTC tomorrow to 02:00 in Dhaka, which is 20:00 UTC today.
        Assert.Equal(Utc(10, 20), body.GetProperty("nextRunAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Update_WithTheSameValues_ChangesNothing()
    {
        var databaseId = await CreateReadyDatabaseAsync();
        var created = await (await PostAsync(databaseId, "0 2 * * *", "UTC")).ReadJsonAsync(HttpStatusCode.Created);
        _clock.Advance(TimeSpan.FromHours(1));

        var body = await (await PutAsync(databaseId, " 0 2 * * * ", "UTC", enabled: true)).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal(created.GetRawText(), body.GetRawText());
    }

    [Fact]
    public async Task Update_Disable_KeepsTheSchedule_WithoutANextRun_AndNothingRuns()
    {
        var databaseId = await CreateReadyDatabaseAsync();
        var created = await (await PostAsync(databaseId, "0 2 * * *")).ReadJsonAsync(HttpStatusCode.Created);

        var body = await (await PutAsync(databaseId, "0 2 * * *", "UTC", enabled: false)).ReadJsonAsync(HttpStatusCode.OK);

        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextRunAt").ValueKind);
        Assert.Equal(created.GetProperty("id").GetGuid(), body.GetProperty("id").GetGuid());
        Assert.Equal(1, await ScheduleCountAsync());

        // Well past the time it would have run.
        _clock.Advance(TimeSpan.FromDays(3));
        Assert.Empty(await _factory.RunSchedulerAsync());
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
    }

    [Fact]
    public async Task Update_ReEnable_StartsFromTheNextOccurrenceAfterThatMoment_WithoutMakingUpForTheTimeItWasDisabled()
    {
        var databaseId = await CreateReadyDatabaseAsync();
        await PostAsync(databaseId, "0 2 * * *");
        await PutAsync(databaseId, "0 2 * * *", "UTC", enabled: false);
        _clock.Advance(TimeSpan.FromDays(3));

        var body = await (await PutAsync(databaseId, "0 2 * * *", "UTC", enabled: true)).ReadJsonAsync(HttpStatusCode.OK);

        Assert.True(body.GetProperty("enabled").GetBoolean());
        // It is 13 March, 10:00: the next 02:00 is on the 14th.
        Assert.Equal(Utc(14, 2), body.GetProperty("nextRunAt").GetDateTimeOffset());
        Assert.Empty(await _factory.RunSchedulerAsync());
    }

    [Fact]
    public async Task Update_OmittedEnabled_MeansEnabled()
    {
        var databaseId = await CreateReadyDatabaseAsync();
        await PostAsync(databaseId, enabled: false);

        var body = await (await _client.PutAsJsonAsync(ScheduleUrl(databaseId), new { cronExpression = "0 2 * * *", timeZoneId = "UTC" })).ReadJsonAsync(HttpStatusCode.OK);

        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(Utc(11, 2), body.GetProperty("nextRunAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Update_InvalidValues_AreRejected_AndTheScheduleIsUnchanged()
    {
        var databaseId = await CreateReadyDatabaseAsync();
        var created = await (await PostAsync(databaseId, "0 2 * * *", "UTC")).ReadJsonAsync(HttpStatusCode.Created);

        await (await PutAsync(databaseId, "not cron", "UTC")).AssertErrorAsync(HttpStatusCode.BadRequest, "INVALID_CRON_EXPRESSION");
        await (await PutAsync(databaseId, "0 2 * * *", "Nowhere/Land")).AssertErrorAsync(HttpStatusCode.BadRequest, "INVALID_TIME_ZONE");

        Assert.Equal(created.GetRawText(), (await GetScheduleAsync(databaseId)).GetRawText());
    }

    [Fact]
    public async Task Update_DoesNotNeedTheDatabaseToBeBackupableRightNow()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        await PostAsync(databaseId);
        await _factory.SetInstanceStatusAsync(instanceId, InstanceStatus.Stopped);

        var response = await PutAsync(databaseId, "0 5 * * *", "UTC", enabled: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithoutASchedule_ReturnsScheduleNotFound_AndUnknownDatabase_DatabaseNotFound()
    {
        var databaseId = await CreateReadyDatabaseAsync();

        await (await PutAsync(databaseId, "0 2 * * *", "UTC")).AssertErrorAsync(HttpStatusCode.NotFound, "BACKUP_SCHEDULE_NOT_FOUND");
        await (await PutAsync(Guid.NewGuid(), "0 2 * * *", "UTC")).AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
        Assert.Equal(0, await ScheduleCountAsync());
    }

    // --- Delete -------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesOnlyTheSchedule_KeepsBackups_DoesNotStopARunningBackup_AndLeavesTheDatabaseAlone()
    {
        var databaseId = await CreateReadyDatabaseAsync();
        var completed = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var (unfinished, unfinishedJobId) = await _client.CreateBackupAsync(databaseId);
        await PostAsync(databaseId);

        var response = await _client.DeleteAsync(ScheduleUrl(databaseId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await (await _client.GetAsync(ScheduleUrl(databaseId))).AssertErrorAsync(HttpStatusCode.NotFound, "BACKUP_SCHEDULE_NOT_FOUND");
        Assert.Equal("completed", (await _client.GetBackupAsync(completed)).Status());
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Single(_factory.BackupFiles());

        // The backup that was under way carries on and finishes.
        Assert.Equal("pending", (await _client.GetJobAsync(unfinishedJobId)).Status());
        await _factory.ProcessJobAsync(unfinishedJobId);
        Assert.Equal("completed", (await _client.GetBackupAsync(unfinished)).Status());
    }

    [Fact]
    public async Task Delete_ThenCreate_GivesANewSchedule()
    {
        var databaseId = await CreateReadyDatabaseAsync();
        var first = await (await PostAsync(databaseId)).ReadJsonAsync(HttpStatusCode.Created);
        await _client.DeleteAsync(ScheduleUrl(databaseId));

        var second = await (await PostAsync(databaseId, "0 5 * * *")).ReadJsonAsync(HttpStatusCode.Created);

        Assert.NotEqual(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Delete_WithoutASchedule_ReturnsScheduleNotFound_AndUnknownDatabase_DatabaseNotFound()
    {
        var databaseId = await CreateReadyDatabaseAsync();

        await (await _client.DeleteAsync(ScheduleUrl(databaseId))).AssertErrorAsync(HttpStatusCode.NotFound, "BACKUP_SCHEDULE_NOT_FOUND");
        await (await _client.DeleteAsync(ScheduleUrl(Guid.NewGuid()))).AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
    }

    // --- The database's and the instance's deletion -------------------------------------------

    [Fact]
    public async Task DeletingTheDatabase_IsNotHeldUpByItsSchedule_AndRemovesIt()
    {
        var databaseId = await CreateReadyDatabaseAsync();
        var other = await CreateReadyDatabaseAsync("other");
        await PostAsync(databaseId);
        await PostAsync(other);

        var deleteJobId = await _client.DeleteDatabaseAsync(databaseId);
        await _factory.ProcessJobAsync(deleteJobId);

        Assert.Equal("completed", (await _client.GetJobAsync(deleteJobId)).Status());
        var remaining = Assert.Single(await _factory.WithDbAsync(db => db.BackupSchedules.AsNoTracking().ToListAsync()));
        Assert.Equal(other, remaining.DatabaseId);
        await (await _client.GetAsync(ScheduleUrl(databaseId))).AssertErrorAsync(HttpStatusCode.NotFound, "DATABASE_NOT_FOUND");
    }

    [Fact]
    public async Task DeletingTheInstance_IsNotHeldUpBySchedules_AndRemovesThem()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        await PostAsync(databaseId);

        var response = await _client.DeleteAsync($"{InstancesUrl}/{instanceId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await ScheduleCountAsync());
    }
}
