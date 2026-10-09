using System.Net;
using System.Text.Json;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Domain.Users;
using Microsoft.EntityFrameworkCore;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Credentials;

/// <summary>
/// Rotating the password of an instance's database administrator: the whole application with the
/// real secret store, the real job pipeline and the real rotation handler, against database
/// servers that are a dictionary of what each accepts (<see cref="FakeAdminCredentials"/>). No
/// worker: each test runs the jobs itself, and decides what goes wrong and when.
/// </summary>
/// <remarks>
/// What the tests are after is one thing said many ways: whatever happens, the password the store
/// returns and the password the server accepts end up the same, and neither is ever shown. No
/// assertion here prints a password when it fails.
/// </remarks>
public sealed class CredentialRotationTests : IDisposable
{
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddHours(-1);

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public CredentialRotationTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static string RotateUrl(Guid instanceId) => $"{InstancesUrl}/{instanceId}/credentials/rotate";

    private static string CredentialUrl(Guid instanceId) => $"{InstancesUrl}/{instanceId}/credentials";

    private Task<HttpResponseMessage> RotateAsync(Guid instanceId) => _client.PostAsync(RotateUrl(instanceId), content: null);

    private async Task<JsonElement> CredentialAsync(Guid instanceId) =>
        await (await _client.GetAsync(CredentialUrl(instanceId))).ReadJsonAsync(HttpStatusCode.OK);

    /// <summary>Whether the store's password is the one the server accepts. Says so without saying either.</summary>
    private async Task<bool> InSyncAsync(Guid instanceId) =>
        await _factory.AdminPasswordAsync(instanceId) == await _factory.AdminCredentials.PasswordAsync(instanceId);

    private async Task<(Guid InstanceId, string Original)> RunningInstanceAsync(string engine = "postgres")
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client, engine: engine);
        var original = await _factory.AdminPasswordAsync(instanceId);
        Assert.True(await InSyncAsync(instanceId));
        return (instanceId, original);
    }

    private async Task<JsonElement> AssertJobAsync(Guid jobId, string status, int? attempt = null, string? errorCode = null)
    {
        var job = await _client.GetJobAsync(jobId);
        Assert.Equal(status, job.Status());
        if (attempt is not null)
        {
            Assert.Equal(attempt, job.GetProperty("attempt").GetInt32());
        }

        if (errorCode is not null)
        {
            Assert.Equal(errorCode, job.GetProperty("error").GetProperty("code").GetString());
        }

        return job;
    }

    // --- The rotation itself ------------------------------------------------------------------

    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    public async Task Rotate_ChangesTheServersPassword_AndTheStoredOne_ToTheSameNewPassword(string engine)
    {
        var (instanceId, original) = await RunningInstanceAsync(engine);

        var response = await RotateAsync(instanceId);
        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        var jobId = body.GetProperty("job").GetProperty("id").GetGuid();

        // Accepted is not rotated: nothing has changed yet, and the job says so.
        Assert.Equal("rotate_credential", body.GetProperty("job").GetProperty("type").GetString());
        Assert.Equal("pending", body.GetProperty("job").Status());
        Assert.EndsWith($"{JobsUrl}/{jobId}", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal("in_progress", body.GetProperty("credential").GetProperty("rotation").GetString());
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == original);
        Assert.True(await InSyncAsync(instanceId));

        await _factory.ProcessJobAsync(jobId);

        await AssertJobAsync(jobId, "completed", attempt: 1);
        var current = await _factory.AdminPasswordAsync(instanceId);
        Assert.False(current == original, "The password was not replaced.");
        Assert.True(await InSyncAsync(instanceId));
        Assert.Null(await _factory.AdminPasswordReplacementAsync(instanceId));
        Assert.Equal(1, _factory.AdminCredentials.Changes);
        Assert.True(current.Length == 32 && current.All(char.IsAsciiLetterOrDigit), "The new password is not of the generated kind.");

        var credential = await CredentialAsync(instanceId);
        Assert.Equal("idle", credential.GetProperty("rotation").GetString());
        Assert.Equal(engine == "postgres" ? "postgres" : "root", credential.GetProperty("username").GetString());
        Assert.True(credential.GetProperty("managed").GetBoolean());
        Assert.Equal(JsonValueKind.String, credential.GetProperty("lastRotatedAt").ValueKind);
        Assert.Equal(jobId, credential.GetProperty("latestJob").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Credential_BeforeAnyRotation_IsManaged_Idle_AndNeverRotated()
    {
        var (instanceId, _) = await RunningInstanceAsync();

        var credential = await CredentialAsync(instanceId);

        Assert.Equal(instanceId, credential.GetProperty("instanceId").GetGuid());
        Assert.Equal("postgres", credential.GetProperty("engine").GetString());
        Assert.True(credential.GetProperty("managed").GetBoolean());
        Assert.Equal("idle", credential.GetProperty("rotation").GetString());
        Assert.Equal(JsonValueKind.Null, credential.GetProperty("lastRotatedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, credential.GetProperty("latestJob").ValueKind);
        // What a credential is described by, and nothing that could hold a secret.
        Assert.Equal(
            ["engine", "instanceId", "lastRotatedAt", "latestJob", "managed", "result", "rotation", "username"],
            credential.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task Credential_OfAnInstanceStillBeingProvisioned_IsNotManagedYet()
    {
        var (instanceId, _) = await _client.CreateInstanceAsync();

        Assert.False((await CredentialAsync(instanceId)).GetProperty("managed").GetBoolean());
        await (await _client.GetAsync(CredentialUrl(Guid.NewGuid()))).AssertErrorAsync(HttpStatusCode.NotFound, "INSTANCE_NOT_FOUND");
    }

    [Fact]
    public async Task EachRotation_GeneratesAPasswordOfItsOwn()
    {
        var (instanceId, original) = await RunningInstanceAsync();
        var seen = new HashSet<string> { original };

        for (var round = 0; round < 3; round++)
        {
            await _factory.ProcessJobAsync(await ApiFactory.RequestRotationAsync(_client, instanceId));
            Assert.True(seen.Add(await _factory.AdminPasswordAsync(instanceId)), "A rotation reused a password.");
            Assert.True(await InSyncAsync(instanceId));
        }

        Assert.Equal(3, _factory.AdminCredentials.Changes);
    }

    [Fact]
    public async Task RunningAFinishedRotationAgain_ChangesNothing()
    {
        var (instanceId, _) = await RunningInstanceAsync();
        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(jobId);
        var rotated = await _factory.AdminPasswordAsync(instanceId);

        await _factory.ProcessJobAsync(jobId);
        Assert.Empty(await _factory.RecoverJobsAsync(includePending: true));

        Assert.True(await _factory.AdminPasswordAsync(instanceId) == rotated);
        Assert.Equal(1, _factory.AdminCredentials.Changes);
    }

    // --- When it is refused -------------------------------------------------------------------

    [Fact]
    public async Task Rotate_UnknownInstance_Is404_AndAnInstanceThatIsNotRunning_Is409()
    {
        await (await RotateAsync(Guid.NewGuid())).AssertErrorAsync(HttpStatusCode.NotFound, "INSTANCE_NOT_FOUND");

        var (provisioningId, _) = await _client.CreateInstanceAsync(name: "still-provisioning");
        await (await RotateAsync(provisioningId)).AssertErrorAsync(HttpStatusCode.Conflict, "INSTANCE_NOT_READY");

        var (instanceId, _) = await RunningInstanceAsync();
        foreach (var status in new[] { InstanceStatus.Stopped, InstanceStatus.Failed })
        {
            await _factory.SetInstanceStatusAsync(instanceId, status);
            await (await RotateAsync(instanceId)).AssertErrorAsync(HttpStatusCode.Conflict, "INSTANCE_NOT_READY");
        }

        // Refused means nothing was prepared either.
        Assert.Null(await _factory.AdminPasswordReplacementAsync(instanceId));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Jobs.CountAsync(job => job.Type == JobType.RotateCredential)));
    }

    [Fact]
    public async Task Rotate_InstanceThatStoppedAfterTheRequest_FailsTheJob_AndChangesNothing()
    {
        var (instanceId, original) = await RunningInstanceAsync();
        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.SetInstanceStatusAsync(instanceId, InstanceStatus.Stopped);

        await _factory.ProcessJobAsync(jobId);

        await AssertJobAsync(jobId, "failed", attempt: 3, errorCode: "INSTANCE_NOT_READY");
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == original);
        Assert.Equal(0, _factory.AdminCredentials.Changes);
        // Nothing was asked of a server that is not supposed to be running.
        Assert.Empty(_factory.AdminCredentials.Calls);
    }

    [Fact]
    public async Task Rotate_WhileARotationIsUnfinished_IsRefused_AndOnlyOneJobExists()
    {
        var (instanceId, _) = await RunningInstanceAsync();
        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        var replacement = await _factory.AdminPasswordReplacementAsync(instanceId);

        await (await RotateAsync(instanceId)).AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_ROTATION_IN_PROGRESS");

        Assert.Equal(1, await _factory.WithDbAsync(db => db.Jobs.CountAsync(job => job.Type == JobType.RotateCredential)));
        // The refused request did not touch the replacement the accepted one stored.
        Assert.True(await _factory.AdminPasswordReplacementAsync(instanceId) == replacement);

        await _factory.ProcessJobAsync(jobId);
        Assert.True(await InSyncAsync(instanceId));
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == replacement);
    }

    [Fact]
    public async Task Rotate_RequestedManyTimesAtOnce_IsAcceptedOnce_AndEndsWithOnePasswordEverywhere()
    {
        var (instanceId, original) = await RunningInstanceAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => RotateAsync(instanceId))));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        foreach (var refused in responses.Where(response => response.StatusCode != HttpStatusCode.Accepted))
        {
            await refused.AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_ROTATION_IN_PROGRESS");
        }

        var jobIds = await _factory.WithDbAsync(db => db.Jobs.Where(job => job.Type == JobType.RotateCredential).Select(job => job.Id).ToListAsync());
        var jobId = Assert.Single(jobIds);

        // Two executions of that one job, as two workers would attempt it: one of them does it.
        await Task.WhenAll(_factory.ProcessJobAsync(jobId), _factory.ProcessJobAsync(jobId));

        await AssertJobAsync(jobId, "completed");
        Assert.Equal(1, _factory.AdminCredentials.Changes);
        Assert.True(await InSyncAsync(instanceId));
        Assert.False(await _factory.AdminPasswordAsync(instanceId) == original, "The password was not replaced.");
    }

    [Fact]
    public async Task Rotate_IsRefused_WhileADatabaseOfTheInstanceIsBeingWorkedOn()
    {
        var (instanceId, _) = await RunningInstanceAsync();
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "orders");

        var (_, createJobId) = await _client.CreateDatabaseAsync(instanceId, "being_created");
        await (await RotateAsync(instanceId)).AssertErrorAsync(HttpStatusCode.Conflict, "DATABASE_OPERATION_IN_PROGRESS");
        await _factory.ProcessJobAsync(createJobId);

        var (_, backupJobId) = await _client.CreateBackupAsync(databaseId);
        await (await RotateAsync(instanceId)).AssertErrorAsync(HttpStatusCode.Conflict, "BACKUP_OPERATION_IN_PROGRESS");
        await _factory.ProcessJobAsync(backupJobId);

        var backupId = await _factory.WithDbAsync(db => db.Backups.Select(backup => backup.Id).SingleAsync());
        var restoreJobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await (await RotateAsync(instanceId)).AssertErrorAsync(HttpStatusCode.Conflict, "RESTORE_OPERATION_IN_PROGRESS");
        await _factory.ProcessJobAsync(restoreJobId);

        Assert.Null(await _factory.AdminPasswordReplacementAsync(instanceId));
        // With all of that finished, it is accepted.
        await _factory.ProcessJobAsync(await ApiFactory.RequestRotationAsync(_client, instanceId));
        Assert.True(await InSyncAsync(instanceId));
    }

    [Fact]
    public async Task WhileARotationIsUnfinished_EverythingThatNeedsTheServer_IsRefused_AndAcceptedAgainAfterwards()
    {
        var (instanceId, _) = await RunningInstanceAsync();
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "orders");
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);

        const string InProgress = "CREDENTIAL_ROTATION_IN_PROGRESS";
        await (await _client.PostAsync(InstanceDatabasesUrl(instanceId), System.Net.Http.Json.JsonContent.Create(new { name = "another" })))
            .AssertErrorAsync(HttpStatusCode.Conflict, InProgress);
        await (await _client.DeleteAsync($"{DatabasesUrl}/{databaseId}")).AssertErrorAsync(HttpStatusCode.Conflict, InProgress);
        await (await _client.PostAsync(DatabaseBackupsUrl(databaseId), content: null)).AssertErrorAsync(HttpStatusCode.Conflict, InProgress);
        await (await _client.PostAsync($"{BackupsUrl}/{backupId}/restore", content: null)).AssertErrorAsync(HttpStatusCode.Conflict, InProgress);
        await (await _client.PostAsync($"{InstancesUrl}/{instanceId}/external-access", content: null)).AssertErrorAsync(HttpStatusCode.Conflict, InProgress);
        await (await _client.DeleteAsync($"{InstancesUrl}/{instanceId}")).AssertErrorAsync(HttpStatusCode.Conflict, InProgress);

        // Nothing of the refused requests was begun.
        Assert.Equal("ready", (await _client.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Databases.CountAsync()));
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
        Assert.Empty(_factory.Provisioner.DeprovisionedInstanceIds);

        await _factory.ProcessJobAsync(jobId);

        // And once the password is rotated, the same work runs, with the new password.
        await _factory.CreateReadyDatabaseAsync(_client, instanceId, "another");
        await _factory.CreateCompletedBackupAsync(_client, databaseId);
        await _factory.ProcessJobAsync(await ApiFactory.RequestRestoreAsync(_client, backupId));
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{InstancesUrl}/{instanceId}")).StatusCode);
    }

    [Fact]
    public async Task ScheduledBackup_DueDuringARotation_IsSkipped_NotFailed()
    {
        var (instanceId, _) = await RunningInstanceAsync();
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "orders");
        var created = await _client.PostAsync(
            $"{DatabasesUrl}/{databaseId}/backup-schedule",
            System.Net.Http.Json.JsonContent.Create(new { cronExpression = "* * * * *", timeZoneId = "UTC", enabled = true }));
        Assert.True(created.IsSuccessStatusCode);
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _factory.WithDbAsync(db => db.BackupSchedules.ExecuteUpdateAsync(update => update.SetProperty(schedule => schedule.NextRunAt, past)));
        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);

        Assert.Empty(await _factory.RunSchedulerAsync());

        Assert.Equal(0, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
        await _factory.ProcessJobAsync(jobId);
        Assert.True(await InSyncAsync(instanceId));
    }

    // --- When the database server does not do it ----------------------------------------------

    [Fact]
    public async Task ServerRefusesTheChange_JobFails_AndTheOldPasswordStaysEverywhere()
    {
        var (instanceId, original) = await RunningInstanceAsync();
        _factory.AdminCredentials.FailNextChanges(int.MaxValue);

        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(jobId);

        var job = await AssertJobAsync(jobId, "failed", attempt: 3, errorCode: "CREDENTIAL_ROTATION_DATABASE_FAILED");
        Assert.Equal("The database server did not change the administrator password.", job.GetProperty("error").GetProperty("message").GetString());
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == original);
        Assert.True(await InSyncAsync(instanceId));
        Assert.Equal(0, _factory.AdminCredentials.Changes);
        Assert.Equal(0, _factory.SecretFaults.Promotions);
        Assert.Equal("incomplete", (await CredentialAsync(instanceId)).GetProperty("rotation").GetString());

        // Everything else goes on working with the password that is still the right one.
        await _factory.CreateReadyDatabaseAsync(_client, instanceId, "still_working");
    }

    [Fact]
    public async Task ServerRefusesTheChangeOnce_TheSameJobRetries_AndChangesItOnce()
    {
        var (instanceId, _) = await RunningInstanceAsync();
        _factory.AdminCredentials.FailNextChanges(1);

        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        var replacement = await _factory.AdminPasswordReplacementAsync(instanceId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobAsync(jobId, "completed", attempt: 2);
        Assert.Equal(1, _factory.AdminCredentials.Changes);
        Assert.True(await InSyncAsync(instanceId));
        // The retry did not make up another password.
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == replacement);
    }

    [Fact]
    public async Task ServerSaysItChanged_ButDoesNotAcceptTheNewPassword_IsNotSuccess_AndTheStoredPasswordIsKept()
    {
        var (instanceId, original) = await RunningInstanceAsync();
        _factory.AdminCredentials.IgnoreNextChanges(int.MaxValue);

        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobAsync(jobId, "failed", attempt: 3, errorCode: "CREDENTIAL_ROTATION_VERIFICATION_FAILED");
        // The store was not given a password the server was never seen to accept.
        Assert.Equal(0, _factory.SecretFaults.Promotions);
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == original);
        Assert.True(await InSyncAsync(instanceId));
    }

    [Fact]
    public async Task ServerUnreachable_JobFailsWithTheConnectionsCode_AndNothingIsChanged()
    {
        var (instanceId, original) = await RunningInstanceAsync();
        await _factory.AdminCredentials.PasswordAsync(instanceId);
        _factory.AdminCredentials.Unreachable = new DatabaseOperationException(
            DatabaseErrorCodes.DatabaseEngineUnavailable,
            "The instance's database server is not running.",
            new IOException("raw-driver-detail"));

        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobAsync(jobId, "failed", attempt: 3, errorCode: "DATABASE_ENGINE_UNAVAILABLE");
        Assert.Equal(0, _factory.SecretFaults.Promotions);
        Assert.Equal(0, _factory.AdminCredentials.Changes);

        // The server comes back; the rotation is asked for again and finishes with the password made the first time.
        _factory.AdminCredentials.Unreachable = null;
        var replacement = await _factory.AdminPasswordReplacementAsync(instanceId);
        Assert.NotNull(replacement);
        await _factory.ProcessJobAsync(await ApiFactory.RequestRotationAsync(_client, instanceId));

        Assert.True(await _factory.AdminPasswordAsync(instanceId) == replacement);
        Assert.False(replacement == original);
        Assert.True(await InSyncAsync(instanceId));
    }

    [Fact]
    public async Task ServerAcceptsNeitherPassword_FailsAsRecoveryRequired_AndTouchesNothing()
    {
        var (instanceId, original) = await RunningInstanceAsync();
        // Changed by someone else, to something Aurora never had.
        _factory.AdminCredentials.SetPassword(instanceId, "changed-behind-auroras-back");

        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobAsync(jobId, "failed", attempt: 3, errorCode: "CREDENTIAL_ROTATION_RECOVERY_REQUIRED");
        Assert.Equal(0, _factory.AdminCredentials.Changes);
        Assert.Equal(0, _factory.SecretFaults.Promotions);
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == original);
        Assert.DoesNotContain(_factory.AdminCredentials.Calls, call => call.Contains("change", StringComparison.Ordinal));
    }

    // --- When the secret store does not do it -------------------------------------------------

    [Fact]
    public async Task StoreFailsAfterTheServerChanged_IsNotSuccess_KeepsTheReplacement_AndTheNextRotationFinishesWithIt()
    {
        var (instanceId, original) = await RunningInstanceAsync();
        _factory.SecretFaults.FailNextPromotions(int.MaxValue);

        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(jobId);

        // The dangerous state, and it is on record: the server has the new password, the store's
        // password is the old one, and the job did not claim otherwise.
        var job = await AssertJobAsync(jobId, "failed", attempt: 3, errorCode: "CREDENTIAL_ROTATION_SECRET_STORE_FAILED");
        Assert.DoesNotContain("raw-store-detail", job.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, _factory.AdminCredentials.Changes);
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == original);
        Assert.False(await InSyncAsync(instanceId));
        Assert.Equal("incomplete", (await CredentialAsync(instanceId)).GetProperty("rotation").GetString());

        // What the server was changed to is not lost: it is the stored replacement, encrypted.
        var replacement = await _factory.AdminPasswordReplacementAsync(instanceId);
        Assert.True(replacement == await _factory.AdminCredentials.PasswordAsync(instanceId));
        var stored = await _factory.WithDbAsync(db => db.InstanceSecrets.AsNoTracking().SingleAsync());
        Assert.False(stored.ProtectedPendingAdminPassword!.Contains(replacement!, StringComparison.Ordinal), "The replacement is stored in the clear.");

        // The store works again. Asking for a rotation does not start over: it finishes this one.
        _factory.SecretFaults.FailNextPromotions(0);
        var secondJobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        Assert.True(await _factory.AdminPasswordReplacementAsync(instanceId) == replacement);
        await _factory.ProcessJobAsync(secondJobId);

        await AssertJobAsync(secondJobId, "completed", attempt: 1);
        // The server was not changed a second time.
        Assert.Equal(1, _factory.AdminCredentials.Changes);
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == replacement);
        Assert.True(await InSyncAsync(instanceId));
        Assert.Null(await _factory.AdminPasswordReplacementAsync(instanceId));
        Assert.Equal("idle", (await CredentialAsync(instanceId)).GetProperty("rotation").GetString());
        await _factory.CreateReadyDatabaseAsync(_client, instanceId, "working_again");
    }

    [Fact]
    public async Task StoreFailsOnce_TheSameJobRetries_WithoutChangingTheServerAgain()
    {
        var (instanceId, _) = await RunningInstanceAsync();
        _factory.SecretFaults.FailNextPromotions(1);

        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(jobId);

        await AssertJobAsync(jobId, "completed", attempt: 2);
        Assert.Equal(1, _factory.AdminCredentials.Changes);
        Assert.Equal(1, _factory.SecretFaults.Promotions);
        Assert.True(await InSyncAsync(instanceId));
        Assert.Contains("postgres authenticate refused", _factory.AdminCredentials.Calls);
    }

    // --- When the process dies ----------------------------------------------------------------

    [Fact]
    public async Task ProcessDiesAfterTheServerChanged_RecoveryFinishesTheRotation_WithoutAnotherPassword()
    {
        var (instanceId, original) = await RunningInstanceAsync();
        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        var replacement = (await _factory.AdminPasswordReplacementAsync(instanceId))!;

        // An execution claims the job, changes the server's password, and dies before it can
        // store it: the job is left running under a lease nobody renews.
        _factory.AdminCredentials.SetPassword(instanceId, replacement);
        await _factory.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == original);

        Assert.Equal([jobId], await _factory.RecoverJobsAsync(includePending: false));
        await _factory.ProcessJobAsync(jobId);

        await AssertJobAsync(jobId, "completed", attempt: 1);
        Assert.Equal(0, _factory.AdminCredentials.Changes);
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == replacement);
        Assert.True(await InSyncAsync(instanceId));
    }

    [Fact]
    public async Task ProcessDiesAfterThePasswordWasStored_RecoveryOnlyChecks_AndCompletes()
    {
        var (instanceId, _) = await RunningInstanceAsync();
        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        var replacement = (await _factory.AdminPasswordReplacementAsync(instanceId))!;

        // Everything was done but the job's own record of it.
        _factory.AdminCredentials.SetPassword(instanceId, replacement);
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE instance_secrets SET protected_admin_password = protected_pending_admin_password, protected_pending_admin_password = NULL"));
        await _factory.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: LongAgo);

        Assert.Equal([jobId], await _factory.RecoverJobsAsync(includePending: false));
        await _factory.ProcessJobAsync(jobId);

        await AssertJobAsync(jobId, "completed", attempt: 1);
        Assert.Equal(0, _factory.AdminCredentials.Changes);
        Assert.Equal(0, _factory.SecretFaults.Promotions);
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == replacement);
        Assert.True(await InSyncAsync(instanceId));
    }

    [Fact]
    public async Task ApplicationRestartedWithARotationPending_TheRestartedApplicationCarriesItOut()
    {
        using var database = new TempDatabase();
        Guid instanceId;
        Guid jobId;
        string original;
        string replacement;

        using (var first = new ApiFactory { DatabasePath = database.Path })
        using (var client = first.CreateClient())
        {
            instanceId = await first.CreateRunningInstanceAsync(client);
            original = await first.AdminPasswordAsync(instanceId);
            jobId = await ApiFactory.RequestRotationAsync(client, instanceId);
            replacement = (await first.AdminPasswordReplacementAsync(instanceId))!;
        }

        using var second = new ApiFactory { DatabasePath = database.Path };
        using var restarted = second.CreateClient();
        // The server is the same server: it still accepts what it did before the restart.
        second.AdminCredentials.SetPassword(instanceId, original);

        Assert.Contains(jobId, await second.RecoverJobsAsync(includePending: true));
        await second.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await restarted.GetJobAsync(jobId)).Status());
        Assert.True(await second.AdminPasswordAsync(instanceId) == replacement);
        Assert.True(await second.AdminCredentials.PasswordAsync(instanceId) == replacement);
        Assert.Equal(1, second.AdminCredentials.Changes);
    }

    [Fact]
    public async Task InstanceDeletedBeforeTheJobRan_TheJobIsGoneWithIt_AndNothingIsLeft()
    {
        var (instanceId, _) = await RunningInstanceAsync();
        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        // Deleting is refused while the rotation is unfinished; the instance's row going away
        // regardless is what a concurrent delete that won the race amounts to.
        await _factory.WithDbAsync(db => db.Instances.Where(instance => instance.Id == instanceId).ExecuteDeleteAsync());

        await _factory.ProcessJobAsync(jobId);

        Assert.Equal(0, await _factory.WithDbAsync(db => db.Jobs.CountAsync()));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.InstanceSecrets.CountAsync()));
        Assert.Equal(0, _factory.AdminCredentials.Changes);
    }

    // --- Nothing is ever shown ----------------------------------------------------------------

    [Fact]
    public async Task NoPassword_Old_New_OrWaiting_IsInAnyResponse_AnyLogEntry_OrAnyRowInTheClear()
    {
        var (instanceId, original) = await RunningInstanceAsync();
        var bodies = new List<string>();

        async Task CollectAsync(HttpResponseMessage response)
        {
            bodies.Add(await response.Content.ReadAsStringAsync());
            bodies.Add(string.Join("\n", response.Headers.Select(header => $"{header.Key}: {string.Join(",", header.Value)}")));
        }

        async Task CollectEverythingAsync(Guid jobId)
        {
            foreach (var url in new[]
                     {
                         CredentialUrl(instanceId), $"{InstancesUrl}/{instanceId}", InstancesUrl, $"{InstancesUrl}/{instanceId}/connection",
                         $"{JobsUrl}/{jobId}", JobsUrl, $"{JobsUrl}?type=rotate_credential&instanceId={instanceId}", MonitoringSummaryUrl
                     })
            {
                await CollectAsync(await _client.GetAsync(url));
            }
        }

        // One rotation that fails in the store after the server changed, one refused, one that succeeds.
        _factory.SecretFaults.FailNextPromotions(int.MaxValue);
        var accepted = await RotateAsync(instanceId);
        await CollectAsync(accepted);
        var failedJobId = (await accepted.ReadJsonAsync()).GetProperty("job").GetProperty("id").GetGuid();
        var waiting = (await _factory.AdminPasswordReplacementAsync(instanceId))!;
        await CollectAsync(await RotateAsync(instanceId));
        await CollectEverythingAsync(failedJobId);
        await _factory.ProcessJobAsync(failedJobId);
        await CollectEverythingAsync(failedJobId);

        _factory.SecretFaults.FailNextPromotions(0);
        var finishedJobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(finishedJobId);
        await CollectEverythingAsync(finishedJobId);

        var secondJobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        var second = (await _factory.AdminPasswordReplacementAsync(instanceId))!;
        await _factory.ProcessJobAsync(secondJobId);
        await CollectEverythingAsync(secondJobId);

        string[] secrets = [original, waiting, second];
        Assert.Equal(3, secrets.Distinct().Count());
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == second);

        foreach (var secret in secrets)
        {
            Assert.False(bodies.Any(body => body.Contains(secret, StringComparison.Ordinal)), "A response carries a password.");
            Assert.False(_factory.Logs.Entries.Any(entry => entry.Contains(secret, StringComparison.Ordinal)), "A log entry carries a password.");
        }

        // The rotations were logged, by instance and job and nothing else.
        Assert.Contains(_factory.Logs.Entries, entry => entry.Contains($"Starting credential rotation for instance {instanceId}", StringComparison.Ordinal));
        Assert.Contains(_factory.Logs.Entries, entry => entry.Contains($"Credential rotation for instance {instanceId} completed", StringComparison.Ordinal));
        Assert.Contains(_factory.Logs.Entries, entry => entry.Contains($"Credential rotation failed for instance {instanceId}", StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains("raw-store-detail", StringComparison.Ordinal));

        // Nothing durable holds a password in the clear: not the jobs, not the secrets, not anything else.
        var rows = await _factory.WithDbAsync(async db =>
        {
            var everything = new List<string>();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            var tables = new List<string>();
            await using (var list = connection.CreateCommand())
            {
                list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                await using var reader = await list.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            foreach (var table in tables)
            {
                await using var all = connection.CreateCommand();
                all.CommandText = $"SELECT * FROM \"{table}\"";
                await using var reader = await all.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    for (var column = 0; column < reader.FieldCount; column++)
                    {
                        everything.Add(Convert.ToString(reader.GetValue(column), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                    }
                }
            }

            return everything;
        });
        Assert.NotEmpty(rows);
        foreach (var secret in secrets)
        {
            Assert.False(rows.Any(value => value.Contains(secret, StringComparison.Ordinal)), "A row holds a password in the clear.");
        }
    }

    [Fact]
    public async Task RotateRequest_TakesNoPassword_FromABody_OrAQuery()
    {
        var (instanceId, _) = await RunningInstanceAsync();
        const string Chosen = "a-password-the-caller-would-like";

        var response = await _client.PostAsync(
            $"{RotateUrl(instanceId)}?password={Chosen}&newPassword={Chosen}",
            System.Net.Http.Json.JsonContent.Create(new { password = Chosen, newPassword = Chosen }));
        var jobId = (await response.ReadJsonAsync(HttpStatusCode.Accepted)).GetProperty("job").GetProperty("id").GetGuid();
        await _factory.ProcessJobAsync(jobId);

        Assert.False(await _factory.AdminPasswordAsync(instanceId) == Chosen, "The caller chose the password.");
        Assert.True(await InSyncAsync(instanceId));
        Assert.DoesNotContain(Chosen, response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    // --- Who may ------------------------------------------------------------------------------

    [Fact]
    public async Task Viewer_SeesTheCredential_AndCannotRotate_OperatorAndAdministratorCan()
    {
        var (instanceId, original) = await RunningInstanceAsync();
        using var viewer = _factory.CreateClientAs(UserRole.Viewer);
        using var @operator = _factory.CreateClientAs(UserRole.Operator);
        using var anonymous = _factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(CredentialUrl(instanceId))).StatusCode);
        await (await viewer.PostAsync(RotateUrl(instanceId), content: null)).AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await anonymous.PostAsync(RotateUrl(instanceId), content: null)).AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        await (await anonymous.GetAsync(CredentialUrl(instanceId))).AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");

        // The refused requests left no trace.
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Jobs.CountAsync(job => job.Type == JobType.RotateCredential)));
        Assert.Null(await _factory.AdminPasswordReplacementAsync(instanceId));
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == original);

        await _factory.ProcessJobAsync(await ApiFactory.RequestRotationAsync(@operator, instanceId));
        await _factory.ProcessJobAsync(await ApiFactory.RequestRotationAsync(_client, instanceId));
        Assert.Equal(2, _factory.AdminCredentials.Changes);
        Assert.True(await InSyncAsync(instanceId));
    }

    [Fact]
    public async Task RotateJob_IsListedAndFilteredLikeAnyOtherJob()
    {
        var (instanceId, _) = await RunningInstanceAsync();
        var jobId = await ApiFactory.RequestRotationAsync(_client, instanceId);
        await _factory.ProcessJobAsync(jobId);

        var listed = await (await _client.GetAsync($"{JobsUrl}?type=rotate_credential")).ReadJsonAsync(HttpStatusCode.OK);
        var job = Assert.Single(listed.GetProperty("items").EnumerateArray());
        Assert.Equal(jobId, job.GetProperty("id").GetGuid());
        Assert.Equal(instanceId, job.GetProperty("instanceId").GetGuid());
        // A rotation is of the instance: it names no database and no backup.
        Assert.False(job.TryGetProperty("databaseId", out _));
        Assert.False(job.TryGetProperty("backupId", out _));
    }
}
