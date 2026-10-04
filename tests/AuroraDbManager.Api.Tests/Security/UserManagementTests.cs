using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuroraDbManager.Api.Domain.Users;
using Microsoft.EntityFrameworkCore;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Security;

/// <summary>
/// <c>/api/v1/users</c>: what an administrator can do with users, the rules for usernames and
/// passwords, and the one thing that is refused, leaving the installation without an administrator.
/// </summary>
public sealed class UserManagementTests : IDisposable
{
    private const string AdminName = "root-admin";
    private const string AdminPassword = "correct horse battery staple";
    private const string Password = "a-perfectly-fine-password";

    private readonly ApiFactory _factory = new() { BootstrapAdmin = (AdminName, AdminPassword) };
    private readonly HttpClient _admin;
    private readonly HttpClient _anonymous;

    public UserManagementTests()
    {
        _admin = _factory.CreateClientAs(UserRole.Admin);
        _anonymous = _factory.CreateAnonymousClient();
    }

    public void Dispose()
    {
        _anonymous.Dispose();
        _admin.Dispose();
        _factory.Dispose();
    }

    private Task<Guid> BootstrapAdminIdAsync() =>
        _factory.WithDbAsync(db => db.Users.Where(user => user.Username == AdminName).Select(user => user.Id).SingleAsync());

    private Task<HttpResponseMessage> UpdateAsync(Guid id, string role, bool enabled, string? password = null) =>
        _admin.PutAsJsonAsync($"{UsersUrl}/{id}", new { role, enabled, password });

    private async Task<JsonElement> GetUserAsync(Guid id) =>
        await (await _admin.GetAsync($"{UsersUrl}/{id}")).ReadJsonAsync(HttpStatusCode.OK);

    // --- Create, read, list -------------------------------------------------------------------

    [Fact]
    public async Task Create_ReturnsTheUser_WhoCanThenSignIn_WithTheRoleTheyWereGiven()
    {
        var before = DateTimeOffset.UtcNow;

        var response = await _admin.PostAsJsonAsync(UsersUrl, new { username = "Olivia.Ops", password = Password, role = "operator" });

        var user = await response.ReadJsonAsync(HttpStatusCode.Created);
        var id = user.GetProperty("id").GetGuid();
        Assert.Equal("Olivia.Ops", user.GetProperty("username").GetString());
        Assert.Equal("operator", user.GetProperty("role").GetString());
        Assert.True(user.GetProperty("enabled").GetBoolean());
        Assert.InRange(user.GetProperty("createdAt").GetDateTimeOffset(), before, DateTimeOffset.UtcNow);
        Assert.Equal($"{UsersUrl}/{id}", response.Headers.Location!.AbsolutePath);
        Assert.Equal(user.GetRawText(), (await GetUserAsync(id)).GetRawText());

        using var olivia = _factory.CreateAnonymousClient();
        var token = await olivia.SignInAsync("olivia.ops", Password);
        var jwt = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token);
        Assert.Equal(id.ToString(), jwt.GetClaim("sub").Value);
        Assert.Equal("operator", jwt.GetClaim("role").Value);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("operator")]
    [InlineData("viewer")]
    public async Task Create_AssignsAnyOfTheThreeRoles(string role)
    {
        var id = await _admin.CreateUserAsync($"user-{role}", Password, role);

        Assert.Equal(role, (await GetUserAsync(id)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Create_Disabled_TheUserExistsButCannotSignIn()
    {
        var id = await _admin.CreateUserAsync("sleeper", Password, "viewer", enabled: false);

        Assert.False((await GetUserAsync(id)).GetProperty("enabled").GetBoolean());
        await (await _anonymous.LoginAsync("sleeper", Password)).AssertErrorAsync(HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    [Theory]
    [InlineData("olivia")]
    [InlineData("OLIVIA")]
    [InlineData("Olivia")]
    public async Task Create_UsernameThatExistsInAnyCase_IsRefused(string username)
    {
        await _admin.CreateUserAsync("olivia", Password, "operator");

        var response = await _admin.PostAsJsonAsync(UsersUrl, new { username, password = Password, role = "viewer" });

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "USERNAME_ALREADY_EXISTS");
        Assert.Equal(2, await _factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    [Theory]
    [InlineData(null, "username")]
    [InlineData("", "username")]
    [InlineData("   ", "username")]
    [InlineData("ab", "username")]
    [InlineData("has space", "username")]
    [InlineData(" padded ", "username")]
    [InlineData("tab\there", "username")]
    [InlineData("semi;colon", "username")]
    [InlineData("ünïcödé", "username")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "username")]
    public async Task Create_InvalidUsername_IsRejected(string? username, string field)
    {
        var response = await _admin.PostAsJsonAsync(UsersUrl, new { username, password = Password, role = "viewer" });

        var error = await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(error.GetProperty("details").TryGetProperty(field, out _), error.GetRawText());
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("a.b-c_d")]
    [InlineData("User123")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Create_ValidUsername_IsAccepted(string username)
    {
        var id = await _admin.CreateUserAsync(username, Password, "viewer");

        Assert.Equal(username, (await GetUserAsync(id)).GetProperty("username").GetString());
        Assert.True(User.IsValidUsername(username));
    }

    [Theory]
    [InlineData(null, "viewer", "password")]
    [InlineData("", "viewer", "password")]
    [InlineData("elevenchars", "viewer", "password")]
    [InlineData("a-perfectly-fine-password", null, "role")]
    [InlineData("a-perfectly-fine-password", "superuser", "role")]
    [InlineData("a-perfectly-fine-password", "Admin", "role")]
    public async Task Create_InvalidPasswordOrRole_IsRejected(string? password, string? role, string field)
    {
        var response = await _admin.PostAsJsonAsync(UsersUrl, new { username = "peter", password, role });

        var error = await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(error.GetProperty("details").TryGetProperty(field, out _), error.GetRawText());
    }

    [Fact]
    public async Task Create_PasswordLongerThanTheMaximum_IsRejected()
    {
        var response = await _admin.PostAsJsonAsync(UsersUrl, new { username = "peter", password = new string('p', 129), role = "viewer" });

        await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task List_ReturnsUsersNewestFirst_Paged()
    {
        var first = await _admin.CreateUserAsync("first-user", Password, "viewer");
        var second = await _admin.CreateUserAsync("second-user", Password, "operator");
        var bootstrap = await BootstrapAdminIdAsync();

        var all = await (await _admin.GetAsync(UsersUrl)).ReadJsonAsync(HttpStatusCode.OK);
        var page = await (await _admin.GetAsync($"{UsersUrl}?pageSize=1&page=2")).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal([second, first, bootstrap], all.GetProperty("items").EnumerateArray().Select(user => user.GetProperty("id").GetGuid()));
        Assert.Equal(3, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(20, all.GetProperty("pageSize").GetInt32());
        Assert.Equal(first, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        await (await _admin.GetAsync($"{UsersUrl}?pageSize=101")).AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Get_Update_Delete_UnknownUser_ReturnNotFound()
    {
        var id = Guid.NewGuid();

        await (await _admin.GetAsync($"{UsersUrl}/{id}")).AssertErrorAsync(HttpStatusCode.NotFound, "USER_NOT_FOUND");
        await (await UpdateAsync(id, "viewer", true)).AssertErrorAsync(HttpStatusCode.NotFound, "USER_NOT_FOUND");
        await (await _admin.DeleteAsync($"{UsersUrl}/{id}")).AssertErrorAsync(HttpStatusCode.NotFound, "USER_NOT_FOUND");
    }

    // --- Update -------------------------------------------------------------------------------

    [Fact]
    public async Task Update_ChangesTheRole_WhichTheUsersNextTokenCarries()
    {
        var id = await _admin.CreateUserAsync("olivia", Password, "viewer");
        using var olivia = _factory.CreateAnonymousClient();
        await olivia.SignInAsync("olivia", Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await olivia.PostAsync(DatabaseBackupsUrl(Guid.NewGuid()), null)).StatusCode);

        var updated = await (await UpdateAsync(id, "operator", true)).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal("operator", updated.GetProperty("role").GetString());
        Assert.Equal("olivia", updated.GetProperty("username").GetString());
        Assert.True(updated.GetProperty("updatedAt").GetDateTimeOffset() >= updated.GetProperty("createdAt").GetDateTimeOffset());

        // Allowed now: the request gets as far as looking for the database.
        await olivia.SignInAsync("olivia", Password);
        Assert.Equal(HttpStatusCode.NotFound, (await olivia.PostAsync(DatabaseBackupsUrl(Guid.NewGuid()), null)).StatusCode);
    }

    [Fact]
    public async Task Update_DisablesAndEnablesAUser()
    {
        var id = await _admin.CreateUserAsync("olivia", Password, "operator");

        Assert.False((await (await UpdateAsync(id, "operator", false)).ReadJsonAsync(HttpStatusCode.OK)).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.LoginAsync("olivia", Password)).StatusCode);

        Assert.True((await (await UpdateAsync(id, "operator", true)).ReadJsonAsync(HttpStatusCode.OK)).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.LoginAsync("olivia", Password)).StatusCode);
    }

    [Fact]
    public async Task Update_ResetsAPassword()
    {
        var id = await _admin.CreateUserAsync("olivia", Password, "operator");

        await (await UpdateAsync(id, "operator", true, "the-password-after-a-reset")).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.LoginAsync("olivia", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.LoginAsync("olivia", "the-password-after-a-reset")).StatusCode);
    }

    [Theory]
    [InlineData("{\"enabled\":true}", "role")]
    [InlineData("{\"role\":\"viewer\"}", "enabled")]
    [InlineData("{\"role\":\"owner\",\"enabled\":true}", "role")]
    [InlineData("{\"role\":\"viewer\",\"enabled\":true,\"password\":\"short\"}", "password")]
    public async Task Update_InvalidRequest_IsRejected_AndChangesNothing(string json, string field)
    {
        var id = await _admin.CreateUserAsync("olivia", Password, "operator");
        var before = (await GetUserAsync(id)).GetRawText();

        var response = await _admin.PutAsync($"{UsersUrl}/{id}", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        var error = await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(error.GetProperty("details").TryGetProperty(field, out _), error.GetRawText());
        Assert.Equal(before, (await GetUserAsync(id)).GetRawText());
    }

    [Fact]
    public async Task Update_CannotChangeTheUsername()
    {
        var id = await _admin.CreateUserAsync("olivia", Password, "operator");

        await (await _admin.PutAsJsonAsync($"{UsersUrl}/{id}", new { username = "renamed", role = "operator", enabled = true }))
            .ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal("olivia", (await GetUserAsync(id)).GetProperty("username").GetString());
    }

    // --- Delete -------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheUser_WhoCanNoLongerSignIn_AndWhoseNameIsFreeAgain()
    {
        var id = await _admin.CreateUserAsync("olivia", Password, "operator");

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"{UsersUrl}/{id}")).StatusCode);

        await (await _admin.GetAsync($"{UsersUrl}/{id}")).AssertErrorAsync(HttpStatusCode.NotFound, "USER_NOT_FOUND");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.LoginAsync("olivia", Password)).StatusCode);
        await _admin.CreateUserAsync("olivia", Password, "viewer");
    }

    // --- The last administrator ---------------------------------------------------------------

    [Fact]
    public async Task OnlyEnabledAdministrator_CannotBeDeleted_Disabled_OrGivenAnotherRole()
    {
        var id = await BootstrapAdminIdAsync();
        var before = (await GetUserAsync(id)).GetRawText();

        var attempts = new[]
        {
            await _admin.DeleteAsync($"{UsersUrl}/{id}"),
            await UpdateAsync(id, "admin", false),
            await UpdateAsync(id, "operator", true),
            await UpdateAsync(id, "viewer", false)
        };

        foreach (var attempt in attempts)
        {
            await attempt.AssertErrorAsync(HttpStatusCode.Conflict, "LAST_ADMINISTRATOR");
        }

        Assert.Equal(before, (await GetUserAsync(id)).GetRawText());
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.LoginAsync(AdminName, AdminPassword)).StatusCode);
    }

    [Fact]
    public async Task OnlyEnabledAdministrator_CanStillHaveTheirPasswordChanged()
    {
        var id = await BootstrapAdminIdAsync();

        await (await UpdateAsync(id, "admin", true, "a-new-administrator-password")).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.OK, (await _anonymous.LoginAsync(AdminName, "a-new-administrator-password")).StatusCode);
    }

    [Fact]
    public async Task OtherUsers_DoNotCountAsAdministrators_NeitherOperatorsNorDisabledAdministrators()
    {
        var id = await BootstrapAdminIdAsync();
        await _admin.CreateUserAsync("olivia", Password, "operator");
        await _admin.CreateUserAsync("retired-admin", Password, "admin", enabled: false);

        await (await _admin.DeleteAsync($"{UsersUrl}/{id}")).AssertErrorAsync(HttpStatusCode.Conflict, "LAST_ADMINISTRATOR");
        await (await UpdateAsync(id, "admin", false)).AssertErrorAsync(HttpStatusCode.Conflict, "LAST_ADMINISTRATOR");
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("disable")]
    [InlineData("demote")]
    public async Task WithASecondEnabledAdministrator_TheFirstCanGo_AndThenTheSecondIsTheLast(string how)
    {
        var first = await BootstrapAdminIdAsync();
        var second = await _admin.CreateUserAsync("second-admin", Password, "admin");

        var response = how switch
        {
            "delete" => await _admin.DeleteAsync($"{UsersUrl}/{first}"),
            "disable" => await UpdateAsync(first, "admin", false),
            _ => await UpdateAsync(first, "operator", true)
        };
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        await (await _admin.DeleteAsync($"{UsersUrl}/{second}")).AssertErrorAsync(HttpStatusCode.Conflict, "LAST_ADMINISTRATOR");
        await (await UpdateAsync(second, "admin", false)).AssertErrorAsync(HttpStatusCode.Conflict, "LAST_ADMINISTRATOR");
        await (await UpdateAsync(second, "viewer", true)).AssertErrorAsync(HttpStatusCode.Conflict, "LAST_ADMINISTRATOR");
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.LoginAsync("second-admin", Password)).StatusCode);
    }

    [Fact]
    public async Task TwoRequestsRemovingTheLastTwoAdministratorsAtOnce_LeaveAtLeastOne()
    {
        var first = await BootstrapAdminIdAsync();
        var second = await _admin.CreateUserAsync("second-admin", Password, "admin");

        var responses = await Task.WhenAll(
            _admin.DeleteAsync($"{UsersUrl}/{first}"),
            _admin.DeleteAsync($"{UsersUrl}/{second}"),
            UpdateAsync(first, "viewer", true),
            UpdateAsync(second, "admin", false));

        Assert.Contains(responses, response => response.IsSuccessStatusCode);
        var enabledAdministrators = await _factory.WithDbAsync(db => db.Users.CountAsync(user => user.Role == UserRole.Admin && user.Enabled));
        Assert.True(enabledAdministrators >= 1, "No enabled administrator is left.");
    }

    [Fact]
    public async Task NonAdministrators_CanBeDeletedDisabledAndDemotedFreely()
    {
        var id = await _admin.CreateUserAsync("olivia", Password, "operator");

        await (await UpdateAsync(id, "viewer", false)).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"{UsersUrl}/{id}")).StatusCode);
    }

    // --- Who may ------------------------------------------------------------------------------

    [Theory]
    [InlineData(UserRole.Operator)]
    [InlineData(UserRole.Viewer)]
    public async Task NonAdministrators_CannotManageUsers_NotEvenThemselves(UserRole role)
    {
        var name = $"self-{role}".ToLowerInvariant();
        var id = await _admin.CreateUserAsync(name, Password, AuroraDbManager.Api.Application.Auth.AuroraPolicies.RoleName(role));
        using var client = _factory.CreateAnonymousClient();
        await client.SignInAsync(name, Password);

        var attempts = new[]
        {
            await client.GetAsync(UsersUrl),
            await client.GetAsync($"{UsersUrl}/{id}"),
            await client.PostAsJsonAsync(UsersUrl, new { username = "accomplice", password = Password, role = "admin" }),
            // Promoting oneself.
            await client.PutAsJsonAsync($"{UsersUrl}/{id}", new { role = "admin", enabled = true }),
            await client.PutAsJsonAsync($"{UsersUrl}/{id}", new { role = "viewer", enabled = true, password = "a-password-of-my-own" }),
            await client.DeleteAsync($"{UsersUrl}/{await BootstrapAdminIdAsync()}")
        };

        foreach (var attempt in attempts)
        {
            await attempt.AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        }

        Assert.Equal(2, await _factory.WithDbAsync(db => db.Users.CountAsync()));
        Assert.Equal(AuroraDbManager.Api.Application.Auth.AuroraPolicies.RoleName(role), (await GetUserAsync(id)).GetProperty("role").GetString());
    }
}
