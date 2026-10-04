using System.Net;
using System.Net.Http.Json;
using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Domain.Users;
using Microsoft.EntityFrameworkCore;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Security;

/// <summary>What happens to a password: hashed, never stored, never returned, never logged.</summary>
public sealed class PasswordSecurityTests : IDisposable
{
    private const string Password = "tr0ub4dor-and-then-some";

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly HttpClient _anonymous;

    public PasswordSecurityTests()
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

    private Task<string> StoredHashAsync(Guid userId) =>
        _factory.WithDbAsync(db => db.Users.Where(user => user.Id == userId).Select(user => user.PasswordHash).SingleAsync());

    // --- The hasher ---------------------------------------------------------------------------

    [Fact]
    public void Hash_IsNotThePassword_AndIsDifferentEveryTime()
    {
        var hashing = new PasswordHashing();

        var first = hashing.Hash(Password);
        var second = hashing.Hash(Password);

        Assert.NotEqual(Password, first);
        Assert.DoesNotContain(Password, first, StringComparison.Ordinal);
        // A salt per hash: the same password never hashes to the same value.
        Assert.NotEqual(first, second);
        Assert.InRange(first.Length, 60, User.PasswordHashMaxLength);
    }

    [Fact]
    public void Hash_IsPbkdf2WithHmacSha512_AtTheConfiguredIterationCount()
    {
        // ASP.NET Core's format: marker 0x01, then the PRF, the iteration count and the salt size, big-endian.
        var bytes = Convert.FromBase64String(new PasswordHashing().Hash(Password));

        Assert.Equal(0x01, bytes[0]);
        Assert.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1)));
        Assert.Equal((uint)PasswordHashing.Iterations, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5)));
        Assert.True(PasswordHashing.Iterations >= 210_000);
        Assert.True(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9)) >= 16);
    }

    [Fact]
    public void Verify_AcceptsThePassword_AndNothingElse()
    {
        var hashing = new PasswordHashing();
        var hash = hashing.Hash(Password);

        Assert.True(hashing.Verify(hash, Password).Succeeded);
        Assert.False(hashing.Verify(hash, Password).NeedsRehash);
        Assert.False(hashing.Verify(hash, Password + " ").Succeeded);
        Assert.False(hashing.Verify(hash, Password.ToUpperInvariant()).Succeeded);
        Assert.False(hashing.Verify(hash, string.Empty).Succeeded);
        // Knowing the hash is not knowing the password.
        Assert.False(hashing.Verify(hash, hash).Succeeded);
    }

    [Theory]
    [InlineData("not a hash at all")]
    [InlineData("")]
    [InlineData("AQAAAAIAAYagAAAAEA==")]
    public void Verify_StoredValueThatIsNoHash_MatchesNoPassword(string stored)
    {
        Assert.False(new PasswordHashing().Verify(stored, Password).Succeeded);
        Assert.False(new PasswordHashing().Verify(stored, stored).Succeeded);
    }

    [Fact]
    public void Verify_HashMadeWithFewerIterations_StillMatches_AndAsksToBeRehashed()
    {
        var older = new Microsoft.AspNetCore.Identity.PasswordHasher<User>(Microsoft.Extensions.Options.Options.Create(
            new Microsoft.AspNetCore.Identity.PasswordHasherOptions { IterationCount = 10_000 })).HashPassword(null!, Password);

        var verification = new PasswordHashing().Verify(older, Password);

        Assert.True(verification.Succeeded);
        Assert.True(verification.NeedsRehash);
    }

    // --- In the database ----------------------------------------------------------------------

    [Fact]
    public async Task Password_IsNeverPersisted_OnlyAHashOfIt()
    {
        var userId = await _admin.CreateUserAsync("olivia", Password, "operator");

        var stored = await StoredHashAsync(userId);
        Assert.NotEqual(Password, stored);
        Assert.DoesNotContain(Password, stored, StringComparison.Ordinal);
        Assert.True(new PasswordHashing().Verify(stored, Password).Succeeded);

        // Nowhere in the row, in any column.
        var row = await _factory.WithDbAsync(async db =>
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT * FROM users";
            await db.Database.OpenConnectionAsync();
            await using var reader = await command.ExecuteReaderAsync();
            var values = new List<string>();
            while (await reader.ReadAsync())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    values.Add($"{reader.GetName(i)}={reader.GetValue(i)}");
                }
            }

            return values;
        });
        Assert.Equal(8, row.Count);
        Assert.All(row, value => Assert.DoesNotContain(Password, value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoUsersWithTheSamePassword_HaveDifferentHashes()
    {
        var first = await _admin.CreateUserAsync("first-user", Password, "viewer");
        var second = await _admin.CreateUserAsync("second-user", Password, "viewer");

        Assert.NotEqual(await StoredHashAsync(first), await StoredHashAsync(second));
    }

    [Fact]
    public async Task ChangingThePassword_StoresANewHash_TheOldPasswordStopsWorking_AndTheNewOneWorks()
    {
        const string replacement = "an-entirely-new-password";
        var userId = await _admin.CreateUserAsync("olivia", Password, "operator");
        var before = await StoredHashAsync(userId);

        await (await _admin.PutAsJsonAsync($"{UsersUrl}/{userId}", new { role = "operator", enabled = true, password = replacement }))
            .ReadJsonAsync(HttpStatusCode.OK);

        var after = await StoredHashAsync(userId);
        Assert.NotEqual(before, after);
        Assert.DoesNotContain(replacement, after, StringComparison.Ordinal);
        await (await _anonymous.LoginAsync("olivia", Password)).AssertErrorAsync(HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.LoginAsync("olivia", replacement)).StatusCode);
    }

    [Fact]
    public async Task UpdateWithoutAPassword_LeavesThePasswordAsItIs()
    {
        var userId = await _admin.CreateUserAsync("olivia", Password, "operator");
        var before = await StoredHashAsync(userId);

        await (await _admin.PutAsJsonAsync($"{UsersUrl}/{userId}", new { role = "viewer", enabled = true })).ReadJsonAsync(HttpStatusCode.OK);

        Assert.Equal(before, await StoredHashAsync(userId));
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.LoginAsync("olivia", Password)).StatusCode);
    }

    [Fact]
    public async Task SigningIn_WithAHashMadeWithOlderParameters_Works_AndUpgradesTheHash()
    {
        var userId = await _admin.CreateUserAsync("olivia", Password, "operator");
        var older = new Microsoft.AspNetCore.Identity.PasswordHasher<User>(Microsoft.Extensions.Options.Options.Create(
            new Microsoft.AspNetCore.Identity.PasswordHasherOptions { IterationCount = 10_000 })).HashPassword(null!, Password);
        await _factory.WithDbAsync(db => db.Users.Where(user => user.Id == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.PasswordHash, older)));

        Assert.Equal(HttpStatusCode.OK, (await _anonymous.LoginAsync("olivia", Password)).StatusCode);

        var upgraded = await StoredHashAsync(userId);
        Assert.NotEqual(older, upgraded);
        Assert.False(new PasswordHashing().Verify(upgraded, Password).NeedsRehash);
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.LoginAsync("olivia", Password)).StatusCode);
    }

    // --- In responses -------------------------------------------------------------------------

    [Fact]
    public async Task NoResponse_EverContainsThePassword_OrItsHash()
    {
        const string replacement = "an-entirely-new-password";
        var created = await _admin.PostAsJsonAsync(UsersUrl, new { username = "olivia", password = Password, role = "operator" });
        var userId = (await created.ReadJsonAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var firstHash = await StoredHashAsync(userId);

        var responses = new List<HttpResponseMessage>
        {
            created,
            await _admin.GetAsync($"{UsersUrl}/{userId}"),
            await _admin.GetAsync(UsersUrl),
            await _admin.PutAsJsonAsync($"{UsersUrl}/{userId}", new { role = "viewer", enabled = true, password = replacement }),
            await _anonymous.LoginAsync("olivia", replacement),
            await _anonymous.LoginAsync("olivia", Password),
            // Refused requests, which have every reason to say what was wrong, and still do not say this.
            await _admin.PostAsJsonAsync(UsersUrl, new { username = "olivia", password = Password, role = "operator" }),
            await _admin.PostAsJsonAsync(UsersUrl, new { username = "bad name", password = Password, role = "wizard" }),
            await _admin.PostAsJsonAsync(UsersUrl, new { username = "peter", password = "2short", role = "viewer" })
        };
        var secondHash = await StoredHashAsync(userId);

        foreach (var response in responses)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
            Assert.DoesNotContain(replacement, text, StringComparison.Ordinal);
            Assert.DoesNotContain("2short", text, StringComparison.Ordinal);
            Assert.DoesNotContain(firstHash, text, StringComparison.Ordinal);
            Assert.DoesNotContain(secondHash, text, StringComparison.Ordinal);
            Assert.DoesNotContain("passwordHash", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password_hash", text, StringComparison.OrdinalIgnoreCase);
        }

        var user = await (await _admin.GetAsync($"{UsersUrl}/{userId}")).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(
            ["createdAt", "enabled", "id", "role", "updatedAt", "username"],
            user.EnumerateObject().Select(property => property.Name).Order());
    }

    // --- In logs ------------------------------------------------------------------------------

    [Fact]
    public async Task NothingLogged_WhileUsersAreManaged_ContainsAPasswordOrAHash()
    {
        const string replacement = "an-entirely-new-password";
        var userId = await _admin.CreateUserAsync("olivia", Password, "operator");
        await _admin.PutAsJsonAsync($"{UsersUrl}/{userId}", new { role = "viewer", enabled = true, password = replacement });
        await _admin.PostAsJsonAsync(UsersUrl, new { username = "peter", password = "2short", role = "viewer" });
        await _anonymous.LoginAsync("olivia", replacement);
        await _anonymous.LoginAsync("olivia", Password);
        var hash = await StoredHashAsync(userId);
        await _admin.DeleteAsync($"{UsersUrl}/{userId}");

        var entries = _factory.Logs.Entries;
        Assert.Contains(entries, entry => entry.Contains($"User {userId} created", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Contains("password changed: True", StringComparison.Ordinal));
        Assert.All(
            new[] { Password, replacement, "2short", hash, ApiFactory.SigningKey },
            secret => Assert.DoesNotContain(entries, entry => entry.Contains(secret, StringComparison.Ordinal)));
    }
}
