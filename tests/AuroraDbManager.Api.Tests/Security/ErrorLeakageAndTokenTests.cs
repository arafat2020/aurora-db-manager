using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Tests.Backups;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Security;

/// <summary>
/// What a failure tells a client, and what is written to the log about it: a stable code and a
/// fixed message, never the internals. And how access tokens are validated: every check on, one
/// algorithm, one place a token is read from.
/// </summary>
public sealed class ErrorLeakageAndTokenTests : IDisposable
{
    private const string AdminName = "root-admin";
    private const string AdminPassword = "correct horse battery staple";
    private const string AccessKey = "AKIAHARDENINGTEST001";
    private const string SecretKey = "hardening-test-secret-9c4e7a2b";
    private const string Endpoint = "https://s3.internal.example";

    private readonly ApiFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _anonymous;

    public ErrorLeakageAndTokenTests()
    {
        _factory = new ApiFactory
        {
            UseDockerProvisioner = true,
            BootstrapAdmin = (AdminName, AdminPassword),
            ConfigureBackups = options =>
            {
                options.StorageType = BackupStorageType.S3;
                options.S3.Bucket = FakeS3ObjectStore.Bucket;
                options.S3.Region = "us-east-1";
                options.S3.Endpoint = Endpoint;
                options.S3.AccessKey = AccessKey;
                options.S3.SecretKey = SecretKey;
            }
        };
        _admin = _factory.CreateClientAs(UserRole.Admin);
        _anonymous = _factory.CreateAnonymousClient();
    }

    public void Dispose()
    {
        _anonymous.Dispose();
        _admin.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task RepresentativeFailures_AnswerWithStableCodes_AndNeitherResponsesNorLogsCarrySecretsOrInternals()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_admin);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_admin, instanceId, "app");
        var instancePassword = await _factory.AdminPasswordAsync(instanceId);
        var backupId = await _factory.CreateCompletedBackupAsync(_admin, databaseId);
        var responses = new List<(string What, HttpResponseMessage Response, string ExpectedCode)>();

        // Invalid credentials and invalid input.
        responses.Add(("login", await _anonymous.LoginAsync(AdminName, "wrong-password-attempt"), "INVALID_CREDENTIALS"));
        responses.Add(("no token", await _anonymous.GetAsync(InstancesUrl), "UNAUTHORIZED"));
        responses.Add(("input", await _admin.PostAsJsonAsync(InstancesUrl, new { name = "", engine = "oracle", cpu = -1 }), "VALIDATION_FAILED"));
        responses.Add(("user input", await _admin.PostAsJsonAsync(UsersUrl, new { username = "x", password = "too-short", role = "root" }), "VALIDATION_FAILED"));

        // The dump program fails, saying things that are for the log only.
        _factory.DumpTools.FailAllRuns($"pg_dump: error: connection to server at \"10.20.30.40\" failed: FATAL: password authentication failed (raw-tool-detail {instancePassword})");
        var (failedBackup, backupJob) = await _admin.CreateBackupAsync(databaseId);
        await _factory.ProcessJobAsync(backupJob);
        responses.Add(("backup job", await _admin.GetAsync($"{JobsUrl}/{backupJob}"), "BACKUP_"));
        responses.Add(("backup", await _admin.GetAsync($"{BackupsUrl}/{failedBackup}"), "BACKUP_"));

        // The restore program fails likewise.
        var restoreJob = await ApiFactory.RequestRestoreAsync(_admin, backupId);
        await _factory.ProcessJobAsync(restoreJob);
        responses.Add(("restore job", await _admin.GetAsync($"{JobsUrl}/{restoreJob}"), "RESTORE_"));
        _factory.DumpTools.ClearScript();

        // The object store is unreachable.
        _factory.ObjectStore.Unavailable = true;
        var (_, s3Job) = await _admin.CreateBackupAsync(databaseId);
        await _factory.ProcessJobAsync(s3Job);
        responses.Add(("s3 job", await _admin.GetAsync($"{JobsUrl}/{s3Job}"), "BACKUP_STORAGE_UNAVAILABLE"));
        responses.Add(("s3 health", await _admin.GetAsync(StorageHealthUrl), "unhealthy"));

        // Docker is unreachable.
        _factory.Docker.Unavailable = true;
        responses.Add(("docker delete", await _admin.DeleteAsync($"{InstancesUrl}/{instanceId}"), "DOCKER_UNAVAILABLE"));
        responses.Add(("docker health", await _admin.GetAsync(InstanceHealthUrl(instanceId)), "runtime_unavailable"));
        responses.Add(("readiness", await _anonymous.GetAsync(ReadinessUrl), "degraded"));
        responses.Add(("summary", await _admin.GetAsync(MonitoringSummaryUrl), "BACKUP_"));

        // The system database fails underneath a request.
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE backup_schedules"));
        responses.Add(("database", await _admin.GetAsync($"{DatabasesUrl}/{databaseId}/backup-schedule"), "INTERNAL_ERROR"));

        var passwordHash = await _factory.WithDbAsync(db => db.Users.Select(user => user.PasswordHash).SingleAsync());
        string[] secrets = [AdminPassword, "wrong-password-attempt", instancePassword, passwordHash, ApiFactory.SigningKey, AccessKey, SecretKey];
        string[] internals =
        [
            "raw-tool-detail", "raw-sdk-detail", "raw-daemon-detail", "Exception", "   at ", "docker.sock", "10.20.30.40",
            "s3.internal.example", FakeS3ObjectStore.Bucket, _factory.BackupRoot, _factory.StagingRoot, "SELECT ", "sqlite", "pg_dump:",
            "PGPASSFILE", "--dbname", "--host"
        ];

        foreach (var (what, response, expectedCode) in responses)
        {
            var text = await response.Content.ReadAsStringAsync() + string.Join(' ', response.Headers.SelectMany(header => header.Value));

            // The stable signal is there...
            Assert.True(text.Contains(expectedCode, StringComparison.Ordinal), $"{what}: expected {expectedCode} in {text}");
            // ...and nothing else that should not be.
            Assert.All(secrets, secret => Assert.False(text.Contains(secret, StringComparison.Ordinal), $"{what} leaks a secret."));
            Assert.All(internals, detail => Assert.False(text.Contains(detail, StringComparison.OrdinalIgnoreCase), $"{what} leaks '{detail}': {text}"));
        }

        // The log has the detail an operator needs, and still none of the secrets.
        var entries = _factory.Logs.Entries;
        Assert.Contains(entries, entry => entry.Contains("raw-tool-detail", StringComparison.Ordinal));
        Assert.All(secrets, secret => Assert.DoesNotContain(entries, entry => entry.Contains(secret, StringComparison.Ordinal)));
        foreach (var pattern in new[] { "Password=", "Authorization: Bearer", "Bearer ey", "SigningKey=", "SecretKey=", "DATABASE_URL", "AWS_SECRET_ACCESS_KEY" })
        {
            Assert.DoesNotContain(entries, entry => entry.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task RequestBodies_AreNeverLogged()
    {
        const string marker = "a-marker-only-the-request-body-contains";

        await _admin.PostAsJsonAsync(InstancesUrl, new { name = marker, engine = "postgres", version = "16", cpu = 1, memoryMb = 512, storageGb = 1 });
        await _admin.PostAsJsonAsync(UsersUrl, new { username = "olivia", password = $"{marker}-pw", role = "viewer" });
        await _anonymous.PostAsJsonAsync(LoginUrl, new { username = marker, password = $"{marker}-pw" });
        await _admin.PostAsync(InstancesUrl, new StringContent($"{{ not json {marker}", Encoding.UTF8, "application/json"));

        Assert.NotEmpty(_factory.Logs.Entries);
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(marker, StringComparison.Ordinal));
    }

    // --- How tokens are validated --------------------------------------------------------------

    [Fact]
    public async Task TokenValidation_HasEveryCheckOn_OneAlgorithm_AndNoOtherWayToBeSignedIn()
    {
        var options = _factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        var validation = options.TokenValidationParameters;

        Assert.True(validation.ValidateIssuer);
        Assert.True(validation.ValidateAudience);
        Assert.True(validation.ValidateLifetime);
        Assert.True(validation.ValidateIssuerSigningKey);
        Assert.True(validation.RequireSignedTokens);
        Assert.True(validation.RequireExpirationTime);
        Assert.Equal([SecurityAlgorithms.HmacSha256], validation.ValidAlgorithms);
        Assert.Equal("aurora-db-manager", validation.ValidIssuer);
        Assert.Equal("aurora-db-manager-api", validation.ValidAudience);
        Assert.NotNull(validation.LifetimeValidator);
        Assert.Null(validation.SignatureValidator);
        Assert.True(Assert.IsType<SymmetricSecurityKey>(validation.IssuerSigningKey).KeySize >= 256);

        // No detail in the challenge, and no custom events type that could take a token from
        // anywhere but the header (that it is taken from nowhere else is tested below, by trying).
        Assert.False(options.IncludeErrorDetails);
        Assert.Null(options.EventsType);
        Assert.Null(options.Authority);
        Assert.Null(options.MetadataAddress);

        // The scheme is the only one: there is no cookie or other way to be signed in.
        var schemes = await _factory.Services.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        Assert.Equal([JwtBearerDefaults.AuthenticationScheme], schemes.Select(scheme => scheme.Name));
    }

    private static string Token(SigningCredentials credentials) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "aurora-db-manager",
        Audience = "aurora-db-manager-api",
        Expires = DateTime.UtcNow.AddMinutes(30),
        Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["name"] = "forged", ["role"] = "admin" },
        SigningCredentials = credentials
    });

    private async Task<HttpStatusCode> StatusWithAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, InstancesUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return (await _anonymous.SendAsync(request)).StatusCode;
    }

    [Theory]
    [InlineData(SecurityAlgorithms.HmacSha384)]
    [InlineData(SecurityAlgorithms.HmacSha512)]
    public async Task TokenSignedWithTheRightKey_ButAnotherAlgorithm_IsRejected(string algorithm)
    {
        // The key padded to the size the algorithm asks for: the right secret, the wrong algorithm.
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiFactory.SigningKey));
        var accepted = Token(new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        var other = Token(new SigningCredentials(key, algorithm));

        Assert.Equal(HttpStatusCode.OK, await StatusWithAsync(accepted));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWithAsync(other));
    }

    [Fact]
    public async Task TokenSignedWithAnAsymmetricKey_IsRejected_IncludingOneThatPassesTheSecretOffAsAPublicKey()
    {
        using var rsa = RSA.Create(2048);
        var rs256 = Token(new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256));
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var es256 = Token(new SigningCredentials(new ECDsaSecurityKey(ecdsa), SecurityAlgorithms.EcdsaSha256));

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWithAsync(rs256));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWithAsync(es256));
    }

    [Fact]
    public async Task TokenAnywhereButTheAuthorizationHeader_IsNotAToken()
    {
        var token = ApiFactory.TokenFor(UserRole.Admin);

        foreach (var url in new[] { $"{InstancesUrl}?access_token={token}", $"{InstancesUrl}?token={token}", $"{InstancesUrl}?bearer={token}" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.GetAsync(url)).StatusCode);
        }

        foreach (var header in new[] { "X-Access-Token", "X-Auth-Token", "Cookie" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, InstancesUrl);
            request.Headers.TryAddWithoutValidation(header, header == "Cookie" ? $"access_token={token}; Authorization=Bearer {token}" : token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.SendAsync(request)).StatusCode);
        }

        using var form = new HttpRequestMessage(HttpMethod.Post, InstancesUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["access_token"] = token })
        };
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.SendAsync(form)).StatusCode);

        // And a query string with a token in it is not logged either.
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyOrShortSigningKey_CanNeverBeConfigured()
    {
        foreach (var key in new[] { "", " ", "short", new string('k', 31) })
        {
            var options = new AuthOptions { Jwt = { SigningKey = key } };
            Assert.Contains("SigningKey", options.Validate(), StringComparison.Ordinal);
        }

        Assert.Null(new AuthOptions { Jwt = { SigningKey = new string('k', 32) } }.Validate());
    }
}
