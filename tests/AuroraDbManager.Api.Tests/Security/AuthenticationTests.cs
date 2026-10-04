using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Security;

/// <summary>
/// Signing in and being recognised, through the application's real authentication: users are in
/// the database with hashed passwords, tokens come from the login endpoint, and every token,
/// good or bad, is judged by ASP.NET Core's JWT bearer handler as configured in Program.
/// </summary>
public sealed class AuthenticationTests : IDisposable
{
    private const string AdminName = "root-admin";
    private const string AdminPassword = "correct horse battery staple";
    private static readonly DateTimeOffset Start = new(2026, 3, 10, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly ApiFactory _factory;
    private readonly HttpClient _anonymous;
    private readonly HttpClient _admin;

    public AuthenticationTests()
    {
        _factory = new ApiFactory { Clock = _clock, BootstrapAdmin = (AdminName, AdminPassword) };
        _anonymous = _factory.CreateAnonymousClient();
        _admin = _factory.CreateClientAs(UserRole.Admin);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _anonymous.Dispose();
        _factory.Dispose();
    }

    private HttpRequestMessage WithToken(string? token, string url = InstancesUrl, string scheme = "Bearer")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"{scheme} {token}");
        }

        return request;
    }

    private static string Segment(object value) => Base64UrlEncoder.Encode(JsonSerializer.Serialize(value));

    private static string Sign(string key, Action<SecurityTokenDescriptor>? change = null)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "aurora-db-manager",
            Audience = "aurora-db-manager-api",
            NotBefore = Start.UtcDateTime.AddMinutes(-1),
            Expires = Start.UtcDateTime.AddMinutes(30),
            Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["name"] = "forged", ["role"] = "admin" },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)
        };
        change?.Invoke(descriptor);
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    // --- Login --------------------------------------------------------------------------------

    [Fact]
    public async Task Login_ValidCredentials_ReturnsABearerToken_ItsExpiry_AndNothingElse()
    {
        var response = await _anonymous.LoginAsync(AdminName, AdminPassword);

        var body = await response.ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(["accessToken", "expiresAt", "tokenType"], body.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        Assert.Equal(Start.AddMinutes(30), body.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("accessToken").GetString()));
        // A credential is not for caches.
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Login_Token_CarriesTheUsersIdNameAndRole_AndNoOtherInformation()
    {
        var userId = await _factory.WithDbAsync(db => db.Users.Select(user => user.Id).SingleAsync());

        var token = await _anonymous.SignInAsync(AdminName, AdminPassword);

        var jwt = new JsonWebToken(token);
        Assert.Equal("HS256", jwt.Alg);
        Assert.Equal(userId.ToString(), jwt.GetClaim("sub").Value);
        Assert.Equal(AdminName, jwt.GetClaim("name").Value);
        Assert.Equal("admin", jwt.GetClaim("role").Value);
        Assert.Equal("aurora-db-manager", jwt.Issuer);
        Assert.Equal(["aurora-db-manager-api"], jwt.Audiences);
        Assert.Equal(Start.AddMinutes(30).UtcDateTime, jwt.ValidTo);
        Assert.Equal(
            ["aud", "exp", "iat", "iss", "name", "nbf", "role", "sub"],
            jwt.Claims.Select(claim => claim.Type).Distinct().Order());

        // Nothing in it is, or is derived from, a secret.
        var payload = Base64UrlEncoder.Decode(jwt.EncodedPayload);
        Assert.DoesNotContain(AdminPassword, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.SigningKey, payload, StringComparison.Ordinal);
        Assert.DoesNotContain("hash", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ROOT-ADMIN")]
    [InlineData("Root-Admin")]
    [InlineData("  root-admin  ")]
    public async Task Login_UsernameIsNotCaseSensitive_AndSurroundingSpaceIsIgnored(string username)
    {
        var response = await _anonymous.LoginAsync(username, AdminPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_WrongPassword_UnknownUser_AndDisabledUser_AreAnsweredIdentically()
    {
        await _admin.CreateUserAsync("sleeper", "sleeper-password-1", "operator", enabled: false);

        var wrongPassword = await _anonymous.LoginAsync(AdminName, "not the password at all");
        var unknownUser = await _anonymous.LoginAsync("nobody-by-that-name", AdminPassword);
        var disabledUser = await _anonymous.LoginAsync("sleeper", "sleeper-password-1");

        foreach (var response in new[] { wrongPassword, unknownUser, disabledUser })
        {
            var error = await response.AssertErrorAsync(HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
            Assert.Equal("Invalid username or password.", error.GetProperty("message").GetString());
        }

        // Byte for byte: nothing tells a client which of the three it was.
        var bodies = await Task.WhenAll(new[] { wrongPassword, unknownUser, disabledUser }.Select(response => response.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        Assert.All(bodies, body => Assert.DoesNotContain("accessToken", body, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("root-admin", "CORRECT HORSE BATTERY STAPLE")]
    [InlineData("root-admin", "correct horse battery staple ")]
    [InlineData("root-admin", "correct horse battery stapl")]
    public async Task Login_PasswordMustMatchExactly(string username, string password)
    {
        var response = await _anonymous.LoginAsync(username, password);

        await response.AssertErrorAsync(HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Theory]
    [InlineData("{}", "username")]
    [InlineData("{\"username\":\"root-admin\"}", "password")]
    [InlineData("{\"password\":\"correct horse battery staple\"}", "username")]
    [InlineData("{\"username\":\"\",\"password\":\"\"}", "username")]
    public async Task Login_IncompleteRequest_IsAValidationError_ThatEchoesNothing(string json, string field)
    {
        var response = await _anonymous.PostAsync(LoginUrl, new StringContent(json, Encoding.UTF8, "application/json"));

        var error = await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(error.GetProperty("details").TryGetProperty(field, out _), error.GetRawText());
        Assert.DoesNotContain(AdminPassword, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_DisabledUser_CannotSignIn_AndCanAgainOnceEnabled()
    {
        var userId = await _admin.CreateUserAsync("olivia", "olivia-password-1", "operator");
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.LoginAsync("olivia", "olivia-password-1")).StatusCode);

        await (await _admin.PutAsJsonAsync($"{UsersUrl}/{userId}", new { role = "operator", enabled = false })).ReadJsonAsync(HttpStatusCode.OK);
        await (await _anonymous.LoginAsync("olivia", "olivia-password-1")).AssertErrorAsync(HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");

        await (await _admin.PutAsJsonAsync($"{UsersUrl}/{userId}", new { role = "operator", enabled = true })).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.LoginAsync("olivia", "olivia-password-1")).StatusCode);
    }

    [Fact]
    public async Task ThereIsNoWayToRegister()
    {
        foreach (var url in new[] { "/api/v1/auth/register", "/api/v1/auth/signup", "/api/v1/register" })
        {
            // Without a token not even whether a route exists is told; with one, it does not.
            var body = new { username = "intruder", password = "intruder-password-1" };
            Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.PostAsJsonAsync(url, body)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await _admin.PostAsJsonAsync(url, body)).StatusCode);
        }

        // And the one way to create a user is not open to someone who is not signed in.
        var create = await _anonymous.PostAsJsonAsync(UsersUrl, new { username = "intruder", password = "intruder-password-1", role = "admin" });
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    // --- The whole way: sign in, get a token, use it -------------------------------------------

    [Fact]
    public async Task SignIn_ThenCallTheApiWithTheToken_IsAuthorizedByTheRoleOfTheUserWhoSignedIn()
    {
        await _admin.CreateUserAsync("vera", "vera-password-123", "viewer");
        using var admin = _factory.CreateAnonymousClient();
        using var viewer = _factory.CreateAnonymousClient();

        // Before signing in: nothing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync(InstancesUrl)).StatusCode);

        await admin.SignInAsync(AdminName, AdminPassword);
        await viewer.SignInAsync("vera", "vera-password-123");

        // The administrator creates an instance; the viewer can see it and cannot create one.
        var (instanceId, _) = await admin.CreateInstanceAsync();
        Assert.Equal(instanceId, (await viewer.GetInstanceAsync(instanceId)).GetProperty("id").GetGuid());
        var refused = await viewer.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest(name: "not-allowed"));
        await refused.AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal(1, (await (await viewer.GetAsync(InstancesUrl)).ReadJsonAsync(HttpStatusCode.OK)).GetProperty("totalCount").GetInt32());

        // Only the administrator sees the users.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(UsersUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(UsersUrl)).StatusCode);
    }

    // --- Tokens that must not be accepted -----------------------------------------------------

    [Fact]
    public async Task NoToken_IsUnauthorized_InTheStandardErrorFormat_WithNothingAboutWhy()
    {
        var response = await _anonymous.GetAsync(InstancesUrl);

        var error = await response.AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        Assert.Equal(["code", "message"], error.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).ToString());
    }

    [Fact]
    public async Task Token_IsAcceptedUntilItExpires_AndNotAfterwards()
    {
        var token = await _anonymous.SignInAsync(AdminName, AdminPassword);
        _anonymous.DefaultRequestHeaders.Authorization = null;

        _clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.SendAsync(WithToken(token))).StatusCode);

        _clock.Advance(TimeSpan.FromMinutes(2));
        var expired = await _anonymous.SendAsync(WithToken(token));
        await expired.AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        // Not even that it expired is said.
        Assert.Equal("Bearer", Assert.Single(expired.Headers.WwwAuthenticate).ToString());
        Assert.DoesNotContain("expired", await expired.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // Signing in again gives a token that works.
        await _anonymous.SignInAsync(AdminName, AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.GetAsync(InstancesUrl)).StatusCode);
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    [InlineData("")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.e30.")]
    [InlineData("Bearer Bearer")]
    public async Task MalformedToken_IsUnauthorized(string token)
    {
        var response = await _anonymous.SendAsync(WithToken(token));

        await response.AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Fact]
    public async Task TokenSignedWithAnotherKey_IsUnauthorized()
    {
        var forged = Sign("another-key-that-is-long-enough-to-sign-with-0123456789");

        var response = await _anonymous.SendAsync(WithToken(forged));

        await response.AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Fact]
    public async Task TokenWithATamperedPayload_IsUnauthorized()
    {
        await _admin.CreateUserAsync("vera", "vera-password-123", "viewer");
        var token = await _anonymous.SignInAsync("vera", "vera-password-123");
        _anonymous.DefaultRequestHeaders.Authorization = null;
        var parts = token.Split('.');

        // The same token, saying "admin" where it said "viewer", under the signature of the original.
        var payload = Base64UrlEncoder.Decode(parts[1]).Replace("\"viewer\"", "\"admin\"", StringComparison.Ordinal);
        var tampered = $"{parts[0]}.{Base64UrlEncoder.Encode(payload)}.{parts[2]}";

        Assert.Equal(HttpStatusCode.OK, (await _anonymous.SendAsync(WithToken(token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.SendAsync(WithToken(tampered))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.SendAsync(WithToken(tampered, UsersUrl))).StatusCode);
    }

    [Fact]
    public async Task UnsignedToken_ClaimingToNeedNoSignature_IsUnauthorized()
    {
        var header = Segment(new { alg = "none", typ = "JWT" });
        var payload = Segment(new
        {
            sub = Guid.NewGuid().ToString(),
            name = "forged",
            role = "admin",
            iss = "aurora-db-manager",
            aud = "aurora-db-manager-api",
            exp = Start.AddHours(1).ToUnixTimeSeconds()
        });

        foreach (var unsigned in new[] { $"{header}.{payload}.", $"{header}.{payload}" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.SendAsync(WithToken(unsigned))).StatusCode);
        }
    }

    [Fact]
    public async Task TokenFromAnotherIssuer_ForAnotherAudience_OrWithoutAnExpiry_IsUnauthorized_EvenWithTheRightKey()
    {
        // The control: the same token, unchanged, is accepted.
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.SendAsync(WithToken(Sign(ApiFactory.SigningKey)))).StatusCode);

        var rejected = new[]
        {
            Sign(ApiFactory.SigningKey, token => token.Issuer = "someone-else"),
            Sign(ApiFactory.SigningKey, token => token.Audience = "another-api"),
            Sign(ApiFactory.SigningKey, token => token.Expires = null),
            Sign(ApiFactory.SigningKey, token => token.Expires = Start.UtcDateTime.AddMinutes(-5)),
            Sign(ApiFactory.SigningKey, token => token.NotBefore = Start.UtcDateTime.AddHours(1))
        };

        foreach (var token in rejected)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.SendAsync(WithToken(token))).StatusCode);
        }
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("Token")]
    public async Task ValidToken_UnderAnotherScheme_IsUnauthorized(string scheme)
    {
        var token = ApiFactory.TokenFor(UserRole.Admin);

        var response = await _anonymous.SendAsync(WithToken(token, scheme: scheme));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("ADMIN")]
    [InlineData("")]
    public async Task ValidToken_WithARoleThatIsNotOne_IsForbiddenEverywhere(string role)
    {
        var token = Sign(ApiFactory.SigningKey, descriptor => descriptor.Claims["role"] = role);

        foreach (var url in new[] { InstancesUrl, JobsUrl, MonitoringSummaryUrl, UsersUrl })
        {
            var response = await _anonymous.SendAsync(WithToken(token, url));
            await response.AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        }
    }

    [Fact]
    public async Task TokenInTheQueryStringOrACookie_IsNotAToken()
    {
        var token = ApiFactory.TokenFor(UserRole.Admin);

        var inQuery = await _anonymous.GetAsync($"{InstancesUrl}?access_token={token}");
        using var withCookie = new HttpRequestMessage(HttpMethod.Get, InstancesUrl);
        withCookie.Headers.Add("Cookie", $"access_token={token}");

        Assert.Equal(HttpStatusCode.Unauthorized, inQuery.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.SendAsync(withCookie)).StatusCode);
    }

    // --- Tokens are not revoked ---------------------------------------------------------------

    [Fact]
    public async Task TokenAlreadyIssued_StaysValidWithItsRole_UntilItExpires_AfterTheUserIsDisabled()
    {
        // Documented behaviour: there is no revocation, which is why the lifetime is short.
        var userId = await _admin.CreateUserAsync("olivia", "olivia-password-1", "operator");
        using var olivia = _factory.CreateAnonymousClient();
        await olivia.SignInAsync("olivia", "olivia-password-1");

        await (await _admin.PutAsJsonAsync($"{UsersUrl}/{userId}", new { role = "viewer", enabled = false })).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.OK, (await olivia.GetAsync(InstancesUrl)).StatusCode);
        await (await olivia.LoginAsync("olivia", "olivia-password-1")).AssertErrorAsync(HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");

        _clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.Unauthorized, (await olivia.GetAsync(InstancesUrl)).StatusCode);
    }

    // --- Correlation and logging --------------------------------------------------------------

    [Fact]
    public async Task AuthenticationFailures_KeepTheirRequestId()
    {
        using var unauthorized = WithToken("not-a-jwt");
        unauthorized.Headers.Add("X-Request-Id", "auth-check-401");
        using var forbidden = WithToken(ApiFactory.TokenFor(UserRole.Viewer), UsersUrl);
        forbidden.Headers.Add("X-Request-Id", "auth-check-403");
        using var login = new HttpRequestMessage(HttpMethod.Post, LoginUrl) { Content = JsonContent.Create(new { username = AdminName, password = "wrong-password-1" }) };
        login.Headers.Add("X-Request-Id", "auth-check-login");

        var responses = new[] { await _anonymous.SendAsync(unauthorized), await _anonymous.SendAsync(forbidden), await _anonymous.SendAsync(login) };

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized], responses.Select(response => response.StatusCode));
        Assert.Equal(
            ["auth-check-401", "auth-check-403", "auth-check-login"],
            responses.Select(response => Assert.Single(response.Headers.GetValues("X-Request-Id"))));

        // And each is in the log, under its request id, with who it was: nobody, or a user by id.
        var entries = _factory.Logs.Entries;
        Assert.Contains(entries, entry => entry.Contains("HTTP GET /api/v1/instances responded 401", StringComparison.Ordinal) && entry.Contains("for user anonymous", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Contains("HTTP GET /api/v1/users responded 403", StringComparison.Ordinal) && !entry.Contains("anonymous", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.EndsWith("scope: Request auth-check-login", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Contains("Login failed for user", StringComparison.Ordinal) && entry.Contains("wrong_password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingLogged_EverContainsAPassword_AToken_AHash_OrTheSigningKey()
    {
        const string attempted = "a-wrong-password-attempt";
        const string typedIntoTheWrongField = "password-typed-as-username";

        var token = await _anonymous.SignInAsync(AdminName, AdminPassword);
        _anonymous.DefaultRequestHeaders.Authorization = null;
        await _anonymous.LoginAsync(AdminName, attempted);
        await _anonymous.LoginAsync(typedIntoTheWrongField, attempted);
        await _admin.CreateUserAsync("olivia", "olivia-password-1", "operator");
        await _admin.PostAsJsonAsync(UsersUrl, new { username = "x", password = "short", role = "nonsense" });
        await _anonymous.SendAsync(WithToken("not-a-jwt"));
        await _anonymous.SendAsync(WithToken(Sign("another-key-that-is-long-enough-to-sign-with-0123456789")));
        await _anonymous.SendAsync(WithToken(token, UsersUrl));
        var hashes = await _factory.WithDbAsync(db => db.Users.Select(user => user.PasswordHash).ToListAsync());

        var entries = _factory.Logs.Entries;
        Assert.NotEmpty(entries);
        string[] secrets = [AdminPassword, attempted, typedIntoTheWrongField, "olivia-password-1", token, token.Split('.')[2], ApiFactory.SigningKey, .. hashes];
        Assert.All(secrets, secret => Assert.DoesNotContain(entries, entry => entry.Contains(secret, StringComparison.Ordinal)));
        Assert.DoesNotContain(entries, entry => entry.Contains("Authorization", StringComparison.Ordinal) && entry.Contains("Bearer ey", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecurityFailureResponses_ContainNoSecrets_AndNoInternals()
    {
        var responses = new List<HttpResponseMessage>
        {
            await _anonymous.GetAsync(InstancesUrl),
            await _anonymous.SendAsync(WithToken("not-a-jwt")),
            await _anonymous.SendAsync(WithToken(Sign("another-key-that-is-long-enough-to-sign-with-0123456789"))),
            await _anonymous.SendAsync(WithToken(ApiFactory.TokenFor(UserRole.Viewer), UsersUrl)),
            await _anonymous.LoginAsync(AdminName, "a-wrong-password-attempt"),
            await _anonymous.LoginAsync("nobody-by-that-name", "a-wrong-password-attempt")
        };
        var hash = await _factory.WithDbAsync(db => db.Users.Select(user => user.PasswordHash).SingleAsync());

        foreach (var response in responses)
        {
            var text = await response.Content.ReadAsStringAsync() + string.Join(' ', response.Headers.SelectMany(header => header.Value));
            Assert.DoesNotContain(AdminPassword, text, StringComparison.Ordinal);
            Assert.DoesNotContain("a-wrong-password-attempt", text, StringComparison.Ordinal);
            Assert.DoesNotContain(hash, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ApiFactory.SigningKey, text, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
            Assert.DoesNotContain("IDX", text, StringComparison.Ordinal);
            Assert.DoesNotContain("signature", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
        }
    }
}
