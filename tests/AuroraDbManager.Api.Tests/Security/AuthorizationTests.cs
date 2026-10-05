using System.Net;
using System.Net.Http.Json;
using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Security;

/// <summary>
/// Who may do what: every endpoint, for an administrator, an operator, a viewer and nobody. The
/// tokens are real JWTs judged by the application's real authentication and policies.
/// </summary>
public sealed class AuthorizationTests : IDisposable
{
    private static readonly Guid Id = Guid.Parse("0198c0de-0000-7000-8000-000000000001");

    private readonly ApiFactory _factory = new();
    private readonly Dictionary<string, HttpClient> _clients;

    public AuthorizationTests()
    {
        _clients = new Dictionary<string, HttpClient>
        {
            ["admin"] = _factory.CreateClientAs(UserRole.Admin),
            ["operator"] = _factory.CreateClientAs(UserRole.Operator),
            ["viewer"] = _factory.CreateClientAs(UserRole.Viewer),
            ["anonymous"] = _factory.CreateAnonymousClient()
        };
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _factory.Dispose();
    }

    private HttpClient Admin => _clients["admin"];

    private HttpClient Operator => _clients["operator"];

    private HttpClient Viewer => _clients["viewer"];

    /// <summary>Every protected endpoint with the least role that may call it. Ids are of nothing that exists.</summary>
    public static TheoryData<string, string, string> Endpoints => new()
    {
        // Instances: an administrator's to create and delete, anyone's to look at.
        { "POST", "/api/v1/instances", "admin" },
        { "DELETE", $"/api/v1/instances/{Id}", "admin" },
        { "GET", "/api/v1/instances", "viewer" },
        { "GET", $"/api/v1/instances/{Id}", "viewer" },
        { "GET", $"/api/v1/instances/{Id}/health", "viewer" },

        // Reaching an instance from outside the Docker network: an administrator's to turn on and
        // off, like the instance itself; where it is reached is anyone's to look at.
        { "POST", $"/api/v1/instances/{Id}/external-access", "admin" },
        { "DELETE", $"/api/v1/instances/{Id}/external-access", "admin" },
        { "GET", $"/api/v1/instances/{Id}/connection", "viewer" },
        { "GET", $"/api/v1/databases/{Id}/connection", "viewer" },

        // Databases.
        { "POST", $"/api/v1/instances/{Id}/databases", "operator" },
        { "DELETE", $"/api/v1/databases/{Id}", "operator" },
        { "GET", $"/api/v1/instances/{Id}/databases", "viewer" },
        { "GET", $"/api/v1/databases/{Id}", "viewer" },

        // Backups and restores.
        { "POST", $"/api/v1/databases/{Id}/backups", "operator" },
        { "POST", $"/api/v1/backups/{Id}/restore", "operator" },
        { "GET", $"/api/v1/databases/{Id}/backups", "viewer" },
        { "GET", $"/api/v1/backups/{Id}", "viewer" },

        // Backup schedules.
        { "POST", $"/api/v1/databases/{Id}/backup-schedule", "operator" },
        { "PUT", $"/api/v1/databases/{Id}/backup-schedule", "operator" },
        { "DELETE", $"/api/v1/databases/{Id}/backup-schedule", "operator" },
        { "GET", $"/api/v1/databases/{Id}/backup-schedule", "viewer" },

        // Jobs and monitoring.
        { "GET", "/api/v1/jobs", "viewer" },
        { "GET", $"/api/v1/jobs/{Id}", "viewer" },
        { "GET", "/api/v1/monitoring/summary", "viewer" },
        { "GET", "/health/storage", "viewer" },

        // Users: administrators only, reading included.
        { "GET", "/api/v1/users", "admin" },
        { "POST", "/api/v1/users", "admin" },
        { "GET", $"/api/v1/users/{Id}", "admin" },
        { "PUT", $"/api/v1/users/{Id}", "admin" },
        { "DELETE", $"/api/v1/users/{Id}", "admin" }
    };

    private static HttpRequestMessage Request(string method, string url)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT")
        {
            // Never a valid body: a request that is let through ends as 400 or 404 and changes nothing.
            request.Content = JsonContent.Create(new { });
        }

        return request;
    }

    private static int Rank(string role) => role switch { "admin" => 3, "operator" => 2, "viewer" => 1, _ => 0 };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Endpoint_IsOpenToItsRoleAndAbove_ForbiddenBelow_AndUnauthorizedWithoutAToken(string method, string url, string minimumRole)
    {
        foreach (var (role, client) in _clients)
        {
            var response = await client.SendAsync(Request(method, url));

            if (role == "anonymous")
            {
                await response.AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
            }
            else if (Rank(role) < Rank(minimumRole))
            {
                await response.AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
            }
            else
            {
                // Let through: whatever the endpoint then says about a request for nothing, it is not about who asked.
                Assert.True(
                    response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
                    $"{role} was refused {method} {url}: {(int)response.StatusCode}");
                Assert.True((int)response.StatusCode < 500, $"{method} {url} failed for {role}: {(int)response.StatusCode}");
            }
        }

        // Nobody who was refused, and no empty request, changed anything.
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Instances.CountAsync()));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Users.CountAsync()));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Jobs.CountAsync()));
    }

    [Theory]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/health/ready")]
    public async Task HealthProbes_NeedNoToken_AndAnswerTheSameWithOne(string method, string url)
    {
        foreach (var client in _clients.Values)
        {
            var body = await (await client.SendAsync(Request(method, url))).ReadJsonAsync(HttpStatusCode.OK);
            Assert.Equal("healthy", body.Status());
        }
    }

    [Fact]
    public async Task HealthProbes_NeedNoToken_EvenWithAnInvalidOne()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReadinessUrl);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer not-a-jwt");

        Assert.Equal(HttpStatusCode.OK, (await _clients["anonymous"].SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Login_NeedsNoToken()
    {
        var response = await _clients["anonymous"].LoginAsync("nobody", "nothing-to-see-here");

        // Reached, and answered on its own terms.
        await response.AssertErrorAsync(HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    // --- The table above is complete, and stays complete ---------------------------------------

    [Fact]
    public void EveryEndpointOfTheApplication_IsInTheTableAbove_OrIsOneOfTheThreePublicOnes()
    {
        var tested = Endpoints.Select(row => $"{row[0]} {Template((string)row[1])}").ToHashSet();
        string[] publicEndpoints = ["GET /health", "GET /health/ready", "POST /api/v1/auth/login"];

        var actual = RouteEndpoints()
            .SelectMany(endpoint => Methods(endpoint).Select(method => $"{method} {Template("/" + endpoint.RoutePattern.RawText!.TrimStart('/'))}"))
            .ToList();

        Assert.NotEmpty(actual);
        // An endpoint added later without a line here, and so without a decision about who may call it, fails this test.
        Assert.Equal(tested.Concat(publicEndpoints).Order(), actual.Order());

        static string Template(string url) =>
            System.Text.RegularExpressions.Regex.Replace(
                url.Replace(Id.ToString(), "{id}", StringComparison.Ordinal), @"\{[^}]+\}", "{id}");
    }

    [Fact]
    public void OnlyTheThreePublicEndpoints_AllowAnonymousAccess_AndEveryOtherNamesAPolicy()
    {
        var anonymous = new List<string>();
        foreach (var endpoint in RouteEndpoints())
        {
            var route = "/" + endpoint.RoutePattern.RawText!.TrimStart('/');
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                anonymous.Add(route);
                continue;
            }

            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy).ToList();
            Assert.True(policies.Count > 0, $"{route} names no authorization policy.");
            Assert.All(policies, policy => Assert.Contains(policy, new[] { AuroraPolicies.Viewer, AuroraPolicies.Operator, AuroraPolicies.Admin }));

            // Nothing that changes anything is open to a viewer.
            if (Methods(endpoint).Any(method => method != "GET"))
            {
                Assert.True(
                    policies.Contains(AuroraPolicies.Operator) || policies.Contains(AuroraPolicies.Admin),
                    $"{string.Join('/', Methods(endpoint))} {route} can be called by a viewer.");
            }
        }

        Assert.Equal(["/api/v1/auth/login", "/health", "/health/ready"], anonymous.Order());
    }

    [Fact]
    public async Task EndpointThatNamesNoPolicy_WouldStillNeedASignedInUser()
    {
        var fallback = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthorizationOptions>>().Value.FallbackPolicy;

        Assert.NotNull(fallback);
        Assert.Same(_factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthorizationOptions>>().Value.GetPolicy(AuroraPolicies.Viewer), fallback);
        // A path that is no endpoint at all is covered by it too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _clients["anonymous"].GetAsync("/api/v1/anything")).StatusCode);
    }

    // Without the OpenAPI document, which only the development environment serves, and which the test host is.
    private IEnumerable<RouteEndpoint> RouteEndpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => !endpoint.RoutePattern.RawText!.TrimStart('/').StartsWith("openapi/", StringComparison.Ordinal));

    // --- OpenAPI ------------------------------------------------------------------------------

    [Fact]
    public async Task OpenApiDocument_DescribesBearerAuthentication_OnEveryOperationButThePublicOne()
    {
        var document = await (await _clients["anonymous"].GetAsync("/openapi/v1.json")).ReadJsonAsync(HttpStatusCode.OK);

        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
        Assert.Equal("JWT", scheme.GetProperty("bearerFormat").GetString());

        var operations = document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(operation => (Name: $"{operation.Name.ToUpperInvariant()} {path.Name}", operation.Value)))
            .ToList();
        Assert.True(operations.Count >= 25);
        foreach (var (name, operation) in operations)
        {
            if (name == "POST /api/v1/auth/login")
            {
                Assert.False(operation.TryGetProperty("security", out _), "Login is public.");
                continue;
            }

            Assert.True(operation.TryGetProperty("security", out var security), $"{name} does not say it needs a token.");
            Assert.True(security[0].TryGetProperty("Bearer", out _), name);
            Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _), name);
            Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _), name);
        }

        // The document describes users without the one thing about them that must never leave the server.
        Assert.DoesNotContain("passwordHash", document.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> Methods(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];

    // --- With real resources ------------------------------------------------------------------

    [Fact]
    public async Task Operator_RunsTheDayToDayOperations_ButNeitherInstancesNorUsers()
    {
        var (instanceId, provisionJob) = await Admin.CreateInstanceAsync();
        await _factory.ProcessJobAsync(provisionJob);

        // Databases, backups, restores and schedules: all the operator's.
        var databaseId = await _factory.CreateReadyDatabaseAsync(Operator, instanceId, "app");
        var backupId = await _factory.CreateCompletedBackupAsync(Operator, databaseId);
        var restoreJob = await ApiFactory.RequestRestoreAsync(Operator, backupId);
        await _factory.ProcessJobAsync(restoreJob);
        Assert.Equal("completed", (await Operator.GetJobAsync(restoreJob)).Status());

        var scheduleUrl = $"{DatabasesUrl}/{databaseId}/backup-schedule";
        await (await Operator.PostAsJsonAsync(scheduleUrl, new { cronExpression = "0 2 * * *", timeZoneId = "UTC" })).ReadJsonAsync(HttpStatusCode.Created);
        await (await Operator.PutAsJsonAsync(scheduleUrl, new { cronExpression = "0 3 * * *", timeZoneId = "UTC" })).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await Operator.DeleteAsync(scheduleUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Operator.GetAsync(MonitoringSummaryUrl)).StatusCode);

        var deleteJob = await Operator.DeleteDatabaseAsync(databaseId);
        await _factory.ProcessJobAsync(deleteJob);

        // Instances and users are not.
        await (await Operator.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest(name: "another"))).AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await Operator.DeleteAsync($"{InstancesUrl}/{instanceId}")).AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await Operator.GetAsync(UsersUrl)).AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal("running", (await Operator.GetInstanceAsync(instanceId)).Status());

        // The administrator can do what the operator could not.
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"{InstancesUrl}/{instanceId}")).StatusCode);
    }

    [Fact]
    public async Task Viewer_SeesEverything_AndChangesNothing()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(Admin);
        var databaseId = await _factory.CreateReadyDatabaseAsync(Admin, instanceId, "app");
        var backupId = await _factory.CreateCompletedBackupAsync(Admin, databaseId);
        var scheduleUrl = $"{DatabasesUrl}/{databaseId}/backup-schedule";
        await (await Admin.PostAsJsonAsync(scheduleUrl, new { cronExpression = "0 2 * * *", timeZoneId = "UTC" })).ReadJsonAsync(HttpStatusCode.Created);
        var before = await SnapshotAsync();

        // Everything can be read.
        foreach (var url in new[]
                 {
                     InstancesUrl, $"{InstancesUrl}/{instanceId}", InstanceHealthUrl(instanceId), InstanceDatabasesUrl(instanceId),
                     $"{DatabasesUrl}/{databaseId}", DatabaseBackupsUrl(databaseId), $"{BackupsUrl}/{backupId}", scheduleUrl,
                     JobsUrl, MonitoringSummaryUrl, StorageHealthUrl, HealthUrl, ReadinessUrl
                 })
        {
            Assert.Equal(HttpStatusCode.OK, (await Viewer.GetAsync(url)).StatusCode);
        }

        // Nothing can be changed, however valid the request.
        var attempts = new[]
        {
            await Viewer.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest(name: "another")),
            await Viewer.DeleteAsync($"{InstancesUrl}/{instanceId}"),
            await Viewer.PostAsJsonAsync(InstanceDatabasesUrl(instanceId), new { name = "another" }),
            await Viewer.DeleteAsync($"{DatabasesUrl}/{databaseId}"),
            await Viewer.PostAsync(DatabaseBackupsUrl(databaseId), null),
            await Viewer.PostAsync($"{BackupsUrl}/{backupId}/restore", null),
            await Viewer.PutAsJsonAsync(scheduleUrl, new { cronExpression = "* * * * *", timeZoneId = "UTC" }),
            await Viewer.DeleteAsync(scheduleUrl),
            await Viewer.PostAsJsonAsync(UsersUrl, new { username = "accomplice", password = "accomplice-password", role = "admin" })
        };
        foreach (var attempt in attempts)
        {
            await attempt.AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        }

        Assert.Equal(before, await SnapshotAsync());

        async Task<string> SnapshotAsync() => string.Join('|', await _factory.WithDbAsync(async db => new[]
        {
            await db.Instances.CountAsync(), await db.Databases.CountAsync(), await db.Backups.CountAsync(),
            await db.Jobs.CountAsync(), await db.BackupSchedules.CountAsync(), await db.Users.CountAsync()
        })) + (await Admin.GetAsync(scheduleUrl)).Content.ReadAsStringAsync().Result;
    }

    // --- Background work needs no user --------------------------------------------------------

    [Fact]
    public async Task SchedulerAndJobs_WorkWithNoUserAnywhere_AndStoreNothingAboutOne()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 3, 10, 10, 0, 0, TimeSpan.Zero));
        using var factory = new ApiFactory { Clock = clock };
        using var admin = factory.CreateClientAs(UserRole.Admin);
        var instanceId = await factory.CreateRunningInstanceAsync(admin);
        var databaseId = await factory.CreateReadyDatabaseAsync(admin, instanceId, "app");
        await (await admin.PostAsJsonAsync($"{DatabasesUrl}/{databaseId}/backup-schedule", new { cronExpression = "0 2 * * *", timeZoneId = "UTC" }))
            .ReadJsonAsync(HttpStatusCode.Created);

        // From here on no request is made: the scheduler creates the job and the processor runs it.
        clock.SetUtcNow(new DateTimeOffset(2026, 3, 11, 2, 0, 0, TimeSpan.Zero));
        var jobId = Assert.Single(await factory.RunSchedulerAsync());
        await factory.ProcessJobAsync(jobId);

        var job = await factory.GetJobEntityAsync(jobId);
        Assert.Equal(Domain.Jobs.JobStatus.Completed, job.Status);

        // Outside a request nobody is signed in, and that is not an error.
        await using var scope = factory.Services.CreateAsyncScope();
        var currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        Assert.False(currentUser.IsAuthenticated);
        Assert.Null(currentUser.UserId);
        Assert.Null(currentUser.Role);

        // Nothing about users or tokens was added to the jobs.
        var columns = typeof(Domain.Jobs.Job).GetProperties().Select(property => property.Name);
        Assert.DoesNotContain(columns, name => name.Contains("User", StringComparison.OrdinalIgnoreCase) || name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task JobsRecoveredAfterARestart_AndTheWorker_NeedNoUser()
    {
        using var database = new TempDatabase();
        Guid jobId;
        using (var before = new ApiFactory { DatabasePath = database.Path })
        using (var admin = before.CreateClientAs(UserRole.Admin))
        {
            (_, jobId) = await admin.CreateInstanceAsync();
        }

        // A new process: the worker recovers the pending job and runs it, with no request and no token involved.
        using var after = new ApiFactory { DatabasePath = database.Path, RunWorker = true };
        using var viewer = after.CreateClientAs(UserRole.Viewer);

        Assert.Equal("completed", (await viewer.WaitForFinishedJobAsync(jobId)).Status());
    }

    [Fact]
    public async Task CurrentUser_InsideARequest_IsTheUserOfTheToken()
    {
        var userId = Guid.NewGuid();
        var accessor = _factory.Services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
        var identity = new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(AuroraPolicies.SubjectClaim, userId.ToString()),
                new System.Security.Claims.Claim(AuroraPolicies.NameClaim, "olivia"),
                new System.Security.Claims.Claim(AuroraPolicies.RoleClaim, "operator")
            ],
            authenticationType: "Bearer");
        accessor.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) };
        try
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

            Assert.True(currentUser.IsAuthenticated);
            Assert.Equal(userId, currentUser.UserId);
            Assert.Equal("olivia", currentUser.Username);
            Assert.Equal(UserRole.Operator, currentUser.Role);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }
}
