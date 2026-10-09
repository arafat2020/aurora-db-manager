using System.Net;
using System.Text.Json;
using AuroraDbManager.Api.Application.Credentials;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Credentials;

/// <summary>
/// The one-time result of a rotation: the single response in which an instance's password leaves
/// the application. The whole application with the real secret store and job pipeline, against
/// <see cref="FakeAdminCredentials"/>, on a clock the tests move.
/// </summary>
/// <remarks>
/// A password is compared here and never printed: no assertion's message contains one.
/// </remarks>
public sealed class CredentialResultTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly ApiFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _operator;
    private readonly HttpClient _viewer;

    public CredentialResultTests()
    {
        _factory = new ApiFactory { Clock = _clock };
        _admin = _factory.CreateClientAs(UserRole.Admin);
        _operator = _factory.CreateClientAs(UserRole.Operator);
        _viewer = _factory.CreateClientAs(UserRole.Viewer);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _operator.Dispose();
        _viewer.Dispose();
        _factory.Dispose();
    }

    private static Task<HttpResponseMessage> RetrieveAsync(HttpClient client, Guid instanceId, Guid jobId) =>
        client.PostAsync(ApiFactory.RotationResultUrl(instanceId, jobId), content: null);

    private async Task<(Guid InstanceId, Guid JobId)> RotatedAsync(string engine = "postgres", HttpClient? by = null)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_admin, name: $"db-{Guid.NewGuid():N}"[..12], engine: engine);
        var jobId = await ApiFactory.RequestRotationAsync(by ?? _operator, instanceId);
        await _factory.ProcessJobAsync(jobId);
        Assert.Equal("completed", (await _admin.GetJobAsync(jobId)).Status());
        return (instanceId, jobId);
    }

    private async Task<string?> ResultStateAsync(Guid instanceId)
    {
        var credential = await (await _viewer.GetAsync($"{InstancesUrl}/{instanceId}/credentials")).ReadJsonAsync(HttpStatusCode.OK);
        var result = credential.GetProperty("result");
        return result.ValueKind == JsonValueKind.Null ? null : result.GetProperty("state").GetString();
    }

    /// <summary>Whether the password a response gave is the one the store holds and the server accepts.</summary>
    private async Task<bool> IsThePasswordAsync(Guid instanceId, string? given) =>
        given is not null
        && given == await _factory.AdminPasswordAsync(instanceId)
        && given == await _factory.AdminCredentials.PasswordAsync(instanceId);

    // --- Once -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("postgres", "postgres")]
    [InlineData("mysql", "root")]
    public async Task Result_OfACompletedRotation_IsTheUsernameAndTheNewPassword_Once(string engine, string username)
    {
        var (instanceId, jobId) = await RotatedAsync(engine);
        Assert.Equal("available", await ResultStateAsync(instanceId));

        var response = await RetrieveAsync(_operator, instanceId, jobId);

        var body = await response.ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(["engine", "instanceId", "jobId", "password", "username"], body.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(username, body.GetProperty("username").GetString());
        Assert.Equal(engine, body.GetProperty("engine").GetString());
        Assert.Equal(jobId, body.GetProperty("jobId").GetGuid());
        Assert.True(await IsThePasswordAsync(instanceId, body.GetProperty("password").GetString()), "The result is not the instance's password.");
        Assert.True(response.Headers.CacheControl!.NoStore, "The response may be stored.");
        Assert.Equal("retrieved", await ResultStateAsync(instanceId));

        // Never again, for anyone.
        foreach (var client in new[] { _operator, _admin })
        {
            var again = await RetrieveAsync(client, instanceId, jobId);
            var error = await again.AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_RESULT_ALREADY_RETRIEVED");
            Assert.Equal("Credential already retrieved. It cannot be displayed again.", error.GetProperty("message").GetString());
        }

        Assert.Equal("retrieved", await ResultStateAsync(instanceId));
    }

    [Fact]
    public async Task Result_OfARotationAnOperatorAskedFor_CanBeRetrievedByAnAdministrator_AndThenNotByTheOperator()
    {
        var (instanceId, jobId) = await RotatedAsync(by: _operator);

        var asAdmin = await (await RetrieveAsync(_admin, instanceId, jobId)).ReadJsonAsync(HttpStatusCode.OK);

        Assert.True(await IsThePasswordAsync(instanceId, asAdmin.GetProperty("password").GetString()), "The result is not the instance's password.");
        await (await RetrieveAsync(_operator, instanceId, jobId)).AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_RESULT_ALREADY_RETRIEVED");
    }

    [Fact]
    public async Task Result_RequestedByManyAtOnce_OperatorsAndAdministrators_IsGivenToExactlyOne()
    {
        var (instanceId, jobId) = await RotatedAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(
            () => RetrieveAsync(index % 2 == 0 ? _operator : _admin, instanceId, jobId))));

        var given = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.True(await IsThePasswordAsync(instanceId, (await given.ReadJsonAsync()).GetProperty("password").GetString()), "The result is not the instance's password.");
        var password = await _factory.AdminPasswordAsync(instanceId);
        foreach (var refused in responses.Where(response => response != given))
        {
            var text = await refused.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("CREDENTIAL_RESULT_ALREADY_RETRIEVED", text, StringComparison.Ordinal);
            Assert.False(text.Contains(password, StringComparison.Ordinal), "A refused request was given the password.");
        }
    }

    // --- Who, and for what ----------------------------------------------------------------------

    [Fact]
    public async Task Viewer_AndNobody_AreRefused_AndTheResultIsStillThereForSomeoneWhoMay()
    {
        var (instanceId, jobId) = await RotatedAsync();
        using var anonymous = _factory.CreateAnonymousClient();
        var password = await _factory.AdminPasswordAsync(instanceId);

        var asViewer = await RetrieveAsync(_viewer, instanceId, jobId);
        var asNobody = await RetrieveAsync(anonymous, instanceId, jobId);

        Assert.False((await asViewer.Content.ReadAsStringAsync()).Contains(password, StringComparison.Ordinal), "A viewer was given the password.");
        await asViewer.AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await asNobody.AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        // A GET is no way in either.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _admin.GetAsync(ApiFactory.RotationResultUrl(instanceId, jobId))).StatusCode);

        Assert.Equal("available", await ResultStateAsync(instanceId));
        await (await RetrieveAsync(_admin, instanceId, jobId)).ReadJsonAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Result_IsOnlyForARotationJobOfThatInstance()
    {
        var (instanceId, jobId) = await RotatedAsync();
        var (otherId, otherJobId) = await RotatedAsync();
        var provisionJobId = await _factory.WithDbAsync(db => db.Jobs
            .Where(job => job.InstanceId == instanceId && job.Type == Domain.Jobs.JobType.ProvisionInstance).Select(job => job.Id).SingleAsync());

        // Another instance's rotation, under this instance's address, and the other way round.
        await (await RetrieveAsync(_admin, instanceId, otherJobId)).AssertErrorAsync(HttpStatusCode.NotFound, "JOB_NOT_FOUND");
        await (await RetrieveAsync(_admin, otherId, jobId)).AssertErrorAsync(HttpStatusCode.NotFound, "JOB_NOT_FOUND");
        // A job of this instance that is no rotation; a job and an instance that do not exist.
        await (await RetrieveAsync(_admin, instanceId, provisionJobId)).AssertErrorAsync(HttpStatusCode.NotFound, "JOB_NOT_FOUND");
        await (await RetrieveAsync(_admin, instanceId, Guid.NewGuid())).AssertErrorAsync(HttpStatusCode.NotFound, "JOB_NOT_FOUND");
        await (await RetrieveAsync(_admin, Guid.NewGuid(), jobId)).AssertErrorAsync(HttpStatusCode.NotFound, "JOB_NOT_FOUND");

        // None of that used anything up, and each instance's result is its own password.
        var mine = await (await RetrieveAsync(_admin, instanceId, jobId)).ReadJsonAsync(HttpStatusCode.OK);
        var theirs = await (await RetrieveAsync(_admin, otherId, otherJobId)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.True(await IsThePasswordAsync(instanceId, mine.GetProperty("password").GetString()), "The result is not the instance's password.");
        Assert.True(await IsThePasswordAsync(otherId, theirs.GetProperty("password").GetString()), "The result is not the instance's password.");
    }

    // --- Only a rotation that got all the way ---------------------------------------------------

    [Fact]
    public async Task Rotation_ThatIsStillPending_HasNoResultYet()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_admin);
        var jobId = await ApiFactory.RequestRotationAsync(_operator, instanceId);

        await (await RetrieveAsync(_admin, instanceId, jobId)).AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_RESULT_NOT_AVAILABLE");
        Assert.Null(await ResultStateAsync(instanceId));

        await _factory.ProcessJobAsync(jobId);
        await (await RetrieveAsync(_admin, instanceId, jobId)).ReadJsonAsync(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("server refuses the change")]
    [InlineData("server does not accept the new password")]
    [InlineData("server accepts neither password")]
    public async Task Rotation_ThatFailed_HasNoResult(string failure)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_admin);
        var original = await _factory.AdminPasswordAsync(instanceId);
        switch (failure)
        {
            case "server refuses the change": _factory.AdminCredentials.FailNextChanges(int.MaxValue); break;
            case "server does not accept the new password": _factory.AdminCredentials.IgnoreNextChanges(int.MaxValue); break;
            default: _factory.AdminCredentials.SetPassword(instanceId, "changed-behind-auroras-back"); break;
        }

        var jobId = await ApiFactory.RequestRotationAsync(_operator, instanceId);
        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("failed", (await _admin.GetJobAsync(jobId)).Status());
        var refused = await RetrieveAsync(_admin, instanceId, jobId);
        var text = await refused.Content.ReadAsStringAsync();
        await refused.AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_RESULT_NOT_AVAILABLE");
        // Neither the password that still works nor the one that was to replace it.
        Assert.False(text.Contains(original, StringComparison.Ordinal), "A failed rotation gave out the stored password.");
        Assert.False(text.Contains((await _factory.AdminPasswordReplacementAsync(instanceId))!, StringComparison.Ordinal), "A failed rotation gave out the replacement.");
        Assert.Null(await ResultStateAsync(instanceId));
    }

    [Fact]
    public async Task StoreFailure_ThenRecovery_TheResultIsThereOnlyOnceARotationCompleted_AndBelongsToTheOneThatDid()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_admin);
        _factory.SecretFaults.FailNextPromotions(int.MaxValue);
        var failedJobId = await ApiFactory.RequestRotationAsync(_operator, instanceId);
        await _factory.ProcessJobAsync(failedJobId);
        var replacement = await _factory.AdminPasswordReplacementAsync(instanceId);

        // The server has the new password and Aurora's store does not: nothing is handed out.
        await (await RetrieveAsync(_admin, instanceId, failedJobId)).AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_RESULT_NOT_AVAILABLE");
        Assert.Null(await ResultStateAsync(instanceId));

        _factory.SecretFaults.FailNextPromotions(0);
        var finishedJobId = await ApiFactory.RequestRotationAsync(_admin, instanceId);
        await (await RetrieveAsync(_admin, instanceId, finishedJobId)).AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_RESULT_NOT_AVAILABLE");
        await _factory.ProcessJobAsync(finishedJobId);

        await (await RetrieveAsync(_admin, instanceId, failedJobId)).AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_RESULT_NOT_AVAILABLE");
        var body = await (await RetrieveAsync(_operator, instanceId, finishedJobId)).ReadJsonAsync(HttpStatusCode.OK);
        var password = body.GetProperty("password").GetString();
        Assert.True(await IsThePasswordAsync(instanceId, password), "The result is not the instance's password.");
        Assert.True(password == replacement, "The rotation did not finish with the replacement it had.");
    }

    [Fact]
    public async Task PuttingTheResultOnOffer_FailsOnce_TheJobRetries_AndTheResultIsThere()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_admin);
        _factory.SecretFaults.FailNextOffers(1);

        var jobId = await ApiFactory.RequestRotationAsync(_operator, instanceId);
        await _factory.ProcessJobAsync(jobId);

        var job = await _admin.GetJobAsync(jobId);
        Assert.Equal("completed", job.Status());
        Assert.Equal(2, job.GetProperty("attempt").GetInt32());
        Assert.Equal(1, _factory.AdminCredentials.Changes);
        var body = await (await RetrieveAsync(_operator, instanceId, jobId)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.True(await IsThePasswordAsync(instanceId, body.GetProperty("password").GetString()), "The result is not the instance's password.");
    }

    [Fact]
    public async Task NewerRotation_OnceAccepted_TakesAnUnretrievedOlderResultAway()
    {
        var (instanceId, firstJobId) = await RotatedAsync();
        Assert.Equal("available", await ResultStateAsync(instanceId));

        var secondJobId = await ApiFactory.RequestRotationAsync(_admin, instanceId);

        // The first password is about to stop working; it is not handed out any more.
        await (await RetrieveAsync(_admin, instanceId, firstJobId)).AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_RESULT_NOT_AVAILABLE");
        await _factory.ProcessJobAsync(secondJobId);
        await (await RetrieveAsync(_admin, instanceId, firstJobId)).AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_RESULT_NOT_AVAILABLE");

        var body = await (await RetrieveAsync(_admin, instanceId, secondJobId)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.True(await IsThePasswordAsync(instanceId, body.GetProperty("password").GetString()), "The result is not the instance's password.");
    }

    // --- A failure in the middle of handing it out ---------------------------------------------

    [Fact]
    public async Task FailureAfterTheResultWasClaimed_BeforeTheAnswerWasReady_DoesNotUseTheOneTimeUp()
    {
        var (instanceId, jobId) = await RotatedAsync();
        var password = await _factory.AdminPasswordAsync(instanceId);
        _factory.SecretFaults.FailNextClaimsAfterClaiming(2);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failed = await RetrieveAsync(_operator, instanceId, jobId);
            var text = await failed.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.False(text.Contains(password, StringComparison.Ordinal), "A failed request was given the password.");
            Assert.DoesNotContain("raw-store-detail", text, StringComparison.Ordinal);
            // The claim went with the failure: nothing on record says it was handed out.
            Assert.Equal("available", await ResultStateAsync(instanceId));
            Assert.Null(await _factory.WithDbAsync(db => db.InstanceSecrets.AsNoTracking().Select(secret => secret.DeliveryConsumedAt).SingleAsync()));
        }

        var body = await (await RetrieveAsync(_operator, instanceId, jobId)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.True(await IsThePasswordAsync(instanceId, body.GetProperty("password").GetString()), "The result is not the instance's password.");
        await (await RetrieveAsync(_operator, instanceId, jobId)).AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_RESULT_ALREADY_RETRIEVED");
    }

    // --- Restarts -------------------------------------------------------------------------------

    [Fact]
    public async Task ApplicationRestartedAfterTheRotationCompleted_TheResultIsStillThere_Once()
    {
        using var database = new TempDatabase();
        Guid instanceId;
        Guid jobId;
        string rotated;

        using (var first = new ApiFactory { DatabasePath = database.Path, Clock = _clock })
        using (var client = first.CreateClient())
        {
            instanceId = await first.CreateRunningInstanceAsync(client);
            jobId = await ApiFactory.RequestRotationAsync(client, instanceId);
            await first.ProcessJobAsync(jobId);
            rotated = await first.AdminPasswordAsync(instanceId);
        }

        using var second = new ApiFactory { DatabasePath = database.Path, Clock = _clock };
        using var restarted = second.CreateClientAs(UserRole.Operator);

        var body = await (await RetrieveAsync(restarted, instanceId, jobId)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.True(body.GetProperty("password").GetString() == rotated, "The restarted application gave out another password.");
        await (await RetrieveAsync(restarted, instanceId, jobId)).AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_RESULT_ALREADY_RETRIEVED");
    }

    [Fact]
    public async Task ProcessDiedAfterThePasswordWasStored_BeforeItWasPutOnOffer_RecoveryPutsItOnOffer()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_admin);
        var jobId = await ApiFactory.RequestRotationAsync(_operator, instanceId);
        var replacement = (await _factory.AdminPasswordReplacementAsync(instanceId))!;

        // Changed in the server, stored as the password, and then nothing: no offer, no completed job.
        _factory.AdminCredentials.SetPassword(instanceId, replacement);
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
            "UPDATE instance_secrets SET protected_admin_password = protected_pending_admin_password, protected_pending_admin_password = NULL"));
        await _factory.SimulateAbandonedExecutionAsync(jobId, leaseExpiresAt: _clock.GetUtcNow().UtcDateTime.AddHours(-1));
        await (await RetrieveAsync(_admin, instanceId, jobId)).AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_RESULT_NOT_AVAILABLE");

        Assert.Equal([jobId], await _factory.RecoverJobsAsync(includePending: false));
        await _factory.ProcessJobAsync(jobId);

        var body = await (await RetrieveAsync(_admin, instanceId, jobId)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.True(body.GetProperty("password").GetString() == replacement, "Recovery made up another password.");
        Assert.Equal(0, _factory.AdminCredentials.Changes);
    }

    // --- Expiry ---------------------------------------------------------------------------------

    [Fact]
    public async Task Result_NotRetrievedInTime_Expires_ThePasswordGoesOnWorking_AndAnotherRotationGivesANewResult()
    {
        var (instanceId, jobId) = await RotatedAsync();
        var rotated = await _factory.AdminPasswordAsync(instanceId);

        _clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal("available", await ResultStateAsync(instanceId));
        _clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));

        var expired = await RetrieveAsync(_admin, instanceId, jobId);
        var text = await expired.Content.ReadAsStringAsync();
        await expired.AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_RESULT_EXPIRED");
        Assert.False(text.Contains(rotated, StringComparison.Ordinal), "An expired result gave out the password.");
        Assert.Equal("expired", await ResultStateAsync(instanceId));

        // Expiry is of the offer, not of the password: Aurora and the server still agree, and work goes on.
        Assert.True(await IsThePasswordAsync(instanceId, rotated), "Expiry changed a password.");
        await _factory.CreateReadyDatabaseAsync(_admin, instanceId, "after_expiry");
        Assert.Equal("completed", (await _admin.GetJobAsync(jobId)).Status());

        var secondJobId = await ApiFactory.RequestRotationAsync(_operator, instanceId);
        await _factory.ProcessJobAsync(secondJobId);
        var body = await (await RetrieveAsync(_operator, instanceId, secondJobId)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.True(await IsThePasswordAsync(instanceId, body.GetProperty("password").GetString()), "The result is not the instance's password.");
        Assert.False(body.GetProperty("password").GetString() == rotated, "The second rotation gave out the first password.");
    }

    [Fact]
    public async Task Result_Lifetime_IsConfigurable()
    {
        using var factory = new ApiFactory { Clock = _clock, CredentialResultTtlMinutes = 2 };
        using var client = factory.CreateClient();
        var instanceId = await factory.CreateRunningInstanceAsync(client);
        var jobId = await ApiFactory.RequestRotationAsync(client, instanceId);
        await factory.ProcessJobAsync(jobId);

        var credential = await (await client.GetAsync($"{InstancesUrl}/{instanceId}/credentials")).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(2), credential.GetProperty("result").GetProperty("expiresAt").GetDateTimeOffset());

        _clock.Advance(TimeSpan.FromMinutes(3));
        await (await RetrieveAsync(client, instanceId, jobId)).AssertErrorAsync(HttpStatusCode.Conflict, "CREDENTIAL_RESULT_EXPIRED");
    }

    [Fact]
    public void DefaultLifetime_IsFifteenMinutes() => Assert.Equal(15, new CredentialOptions().ResultTtlMinutes);

    // --- It crosses the boundary in that one response and nowhere else -------------------------

    [Fact]
    public async Task ThePassword_IsInTheOneResponse_AndInNoOtherResponse_NoLogEntry_AndNoRow()
    {
        var (instanceId, jobId) = await RotatedAsync(engine: "mysql");
        var others = new List<string>();

        async Task CollectAsync()
        {
            foreach (var url in new[]
                     {
                         $"{InstancesUrl}/{instanceId}/credentials", $"{InstancesUrl}/{instanceId}", InstancesUrl, $"{InstancesUrl}/{instanceId}/connection",
                         $"{JobsUrl}/{jobId}", JobsUrl, $"{JobsUrl}?type=rotate_credential", MonitoringSummaryUrl
                     })
            {
                var response = await _admin.GetAsync(url);
                others.Add(await response.Content.ReadAsStringAsync());
                others.Add(response.Headers.ToString());
            }
        }

        await CollectAsync();
        var given = await RetrieveAsync(_operator, instanceId, jobId);
        var password = (await given.ReadJsonAsync(HttpStatusCode.OK)).GetProperty("password").GetString()!;
        others.Add(given.Headers.ToString());
        others.Add(given.RequestMessage!.RequestUri!.ToString());
        var refused = await RetrieveAsync(_admin, instanceId, jobId);
        others.Add(await refused.Content.ReadAsStringAsync());
        await CollectAsync();

        Assert.True(await IsThePasswordAsync(instanceId, password), "The result is not the instance's password.");
        Assert.False(others.Any(text => text.Contains(password, StringComparison.Ordinal)), "Something other than the one response carries the password.");
        Assert.False(_factory.Logs.Entries.Any(entry => entry.Contains(password, StringComparison.Ordinal)), "A log entry carries the password.");
        // That it was retrieved is on record, by whom, and without what.
        Assert.Contains(_factory.Logs.Entries, entry =>
            entry.Contains($"The new administrator password of instance {instanceId} from rotation job {jobId} was retrieved by user", StringComparison.Ordinal));

        var rows = await _factory.WithDbAsync(async db =>
        {
            var values = new List<string>();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            foreach (var table in new[] { "jobs", "instance_secrets", "instances" })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM {table}";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    for (var column = 0; column < reader.FieldCount; column++)
                    {
                        values.Add(Convert.ToString(reader.GetValue(column), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                    }
                }
            }

            return values;
        });
        Assert.NotEmpty(rows);
        Assert.False(rows.Any(value => value.Contains(password, StringComparison.Ordinal)), "A row holds the password in the clear.");
    }

    [Fact]
    public void TheResult_DoesNotPrintItsPassword()
    {
        var result = new CredentialRotationResultResponse(Guid.NewGuid(), Guid.NewGuid(), InstanceEngine.Postgres, "postgres", "NotToBePrintedAnywhere0123456789");

        Assert.DoesNotContain("NotToBePrintedAnywhere", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("NotToBePrintedAnywhere", $"{new RetrieveCredentialResult(RetrieveCredentialStatus.Retrieved, result)}", StringComparison.Ordinal);
    }
}
