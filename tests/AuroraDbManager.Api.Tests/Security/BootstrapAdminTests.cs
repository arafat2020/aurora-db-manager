using System.Net;
using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Security;

/// <summary>
/// The first administrator, created at startup from the bootstrap settings, and the settings
/// authentication cannot start without. A "restart" disposes one factory and starts another on
/// the same system database.
/// </summary>
public sealed class BootstrapAdminTests : IDisposable
{
    private const string Name = "first-admin";
    private const string Password = "the-initial-admin-password";

    private readonly TempDatabase _database = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }

        _database.Dispose();
    }

    private (ApiFactory Factory, HttpClient Anonymous) Application((string, string)? bootstrap, Action<AuthOptions>? configure = null)
    {
        var factory = new ApiFactory { DatabasePath = _database.Path, BootstrapAdmin = bootstrap, ConfigureAuth = configure };
        var client = factory.CreateAnonymousClient();
        _disposables.Add(factory);
        _disposables.Add(client);
        return (factory, client);
    }

    private static Task<List<User>> UsersAsync(ApiFactory factory) =>
        factory.WithDbAsync(db => db.Users.AsNoTracking().OrderBy(user => user.CreatedAt).ToListAsync());

    // --- Bootstrap ----------------------------------------------------------------------------

    [Fact]
    public async Task ConfiguredBootstrapAdmin_IsCreatedAtStartup_AsAnEnabledAdministrator_WhoCanSignIn()
    {
        var (factory, anonymous) = Application((Name, Password));

        var user = Assert.Single(await UsersAsync(factory));
        Assert.Equal(Name, user.Username);
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.True(user.Enabled);
        Assert.DoesNotContain(Password, user.PasswordHash, StringComparison.Ordinal);

        await anonymous.SignInAsync(Name, Password);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(UsersUrl)).StatusCode);
    }

    [Fact]
    public async Task NoBootstrapSettings_NoAccountIsCreated_AndTheLogSaysWhatToDo()
    {
        var (factory, anonymous) = Application(null);

        Assert.Empty(await UsersAsync(factory));
        Assert.Contains(factory.Logs.Entries, entry =>
            entry.Contains("Warning", StringComparison.Ordinal) && entry.Contains("No administrator exists", StringComparison.Ordinal));
        // The application runs, and nobody can sign in: there is no default account.
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(HealthUrl)).StatusCode);
        foreach (var (username, password) in new[] { ("admin", "admin"), ("admin", "password"), ("root", "root"), ("aurora", "aurora") })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.LoginAsync(username, password)).StatusCode);
        }
    }

    [Fact]
    public async Task Restart_WithTheSameSettings_CreatesNothingAgain()
    {
        var (first, _) = Application((Name, Password));
        var created = Assert.Single(await UsersAsync(first));
        first.Dispose();

        var (second, anonymous) = Application((Name, Password));

        var after = Assert.Single(await UsersAsync(second));
        Assert.Equal(created.Id, after.Id);
        Assert.Equal(created.PasswordHash, after.PasswordHash);
        Assert.Equal(created.UpdatedAt, after.UpdatedAt);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.LoginAsync(Name, Password)).StatusCode);
    }

    [Fact]
    public async Task Restart_WithOtherBootstrapSettings_NeitherOverwritesTheAdministrator_NorAddsAnother()
    {
        var (first, _) = Application((Name, Password));
        var created = Assert.Single(await UsersAsync(first));
        first.Dispose();

        // The same name with another password, then another name altogether.
        var (second, anonymous) = Application((Name, "a-password-someone-put-in-the-config"));
        Assert.Equal(created.PasswordHash, Assert.Single(await UsersAsync(second)).PasswordHash);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.LoginAsync(Name, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.LoginAsync(Name, "a-password-someone-put-in-the-config")).StatusCode);
        second.Dispose();

        var (third, _) = Application(("another-admin", "another-admin-password"));
        Assert.Equal(Name, Assert.Single(await UsersAsync(third)).Username);
    }

    [Fact]
    public async Task Restart_WithoutBootstrapSettings_OnceAnAdministratorExists_JustWorks()
    {
        var (first, _) = Application((Name, Password));
        first.Dispose();

        var (second, anonymous) = Application(null);

        Assert.Single(await UsersAsync(second));
        Assert.Equal(HttpStatusCode.OK, (await anonymous.LoginAsync(Name, Password)).StatusCode);
        Assert.DoesNotContain(second.Logs.Entries, entry => entry.Contains("No administrator exists", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdministratorWhoChangedTheirPassword_KeepsIt_WhenTheOldBootstrapSettingsAreStillThere()
    {
        var (first, _) = Application((Name, Password));
        using (var admin = first.CreateClientAs(UserRole.Admin))
        {
            var id = Assert.Single(await UsersAsync(first)).Id;
            await (await System.Net.Http.Json.HttpClientJsonExtensions.PutAsJsonAsync(
                admin, $"{UsersUrl}/{id}", new { role = "admin", enabled = true, password = "the-password-chosen-afterwards" })).ReadJsonAsync(HttpStatusCode.OK);
        }

        first.Dispose();
        var (_, anonymous) = Application((Name, Password));

        Assert.Equal(HttpStatusCode.OK, (await anonymous.LoginAsync(Name, "the-password-chosen-afterwards")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.LoginAsync(Name, Password)).StatusCode);
    }

    [Fact]
    public async Task BootstrapName_TakenByAUserWhoIsNotAnAdministrator_IsNeitherPromotedNorOverwritten()
    {
        var (first, _) = Application(null);
        var hash = first.Services.GetService(typeof(PasswordHashing)) is PasswordHashing hashing ? hashing.Hash("the-viewers-own-password") : throw new InvalidOperationException();
        await first.WithDbAsync(async db =>
        {
            db.Users.Add(User.Create(Name, hash, UserRole.Viewer, enabled: true, DateTime.UtcNow));
            return await db.SaveChangesAsync();
        });
        first.Dispose();

        var (second, anonymous) = Application((Name.ToUpperInvariant(), Password));

        var user = Assert.Single(await UsersAsync(second));
        Assert.Equal(UserRole.Viewer, user.Role);
        Assert.Equal(hash, user.PasswordHash);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.LoginAsync(Name, Password)).StatusCode);
        Assert.Contains(second.Logs.Entries, entry => entry.Contains("Error", StringComparison.Ordinal) && entry.Contains("was not created", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BootstrapPassword_IsNeverLogged_AndNeverReturned()
    {
        var (factory, anonymous) = Application((Name, Password));
        var login = await anonymous.LoginAsync(Name, Password);
        await anonymous.SignInAsync(Name, Password);
        var users = await (await anonymous.GetAsync(UsersUrl)).Content.ReadAsStringAsync();

        Assert.Contains(factory.Logs.Entries, entry => entry.Contains($"Bootstrap administrator {Name} created", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(Password, StringComparison.Ordinal));
        Assert.DoesNotContain(Password, await login.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, users, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemDatabaseUnreachableAtStartup_TheApplicationStillStarts_AndSaysSoInTheLog()
    {
        var (factory, _) = Application((Name, Password));
        using var broken = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<DbContextOptions<Infrastructure.Persistence.AppDbContext>>(services);
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
                .RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<Infrastructure.Persistence.AppDbContext>>(services);
            Microsoft.Extensions.DependencyInjection.EntityFrameworkServiceCollectionExtensions.AddDbContext<Infrastructure.Persistence.AppDbContext>(
                services,
                options => options.UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"aurora-missing-{Guid.NewGuid():N}", "system.db")};Mode=ReadWrite;Pooling=False"));
        }));
        using var client = broken.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(HealthUrl)).StatusCode);
        Assert.Contains(factory.Logs.Entries, entry => entry.Contains("could not be checked whether an administrator exists", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(Password, StringComparison.Ordinal));
    }

    // --- Settings the application will not start without ---------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-short-to-sign-with")]
    public void MissingOrShortSigningKey_FailsStartup_WithAMessageThatNamesTheSetting_AndNotTheKey(string signingKey)
    {
        using var factory = new ApiFactory { ConfigureAuth = options => options.Jwt.SigningKey = signingKey };

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("Authentication:Jwt:SigningKey", exception.Message, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(signingKey))
        {
            Assert.DoesNotContain(signingKey, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Application_HasNoSigningKeyOfItsOwn()
    {
        // Neither a default in the options nor a value in the settings files that are shipped.
        Assert.Equal(string.Empty, new JwtOptions().SigningKey);
        Assert.NotNull(new AuthOptions().Validate());

        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AuroraDbManager.Api"));
        foreach (var file in Directory.GetFiles(root, "appsettings*.json"))
        {
            using var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            if (!settings.RootElement.TryGetProperty("Authentication", out var authentication))
            {
                continue;
            }

            if (authentication.TryGetProperty("Jwt", out var jwt) && jwt.TryGetProperty("SigningKey", out var key))
            {
                Assert.True(string.IsNullOrEmpty(key.GetString()), $"{Path.GetFileName(file)} contains a signing key.");
            }

            if (authentication.TryGetProperty("BootstrapAdmin", out var bootstrap) && bootstrap.TryGetProperty("Password", out var password))
            {
                Assert.True(string.IsNullOrEmpty(password.GetString()), $"{Path.GetFileName(file)} contains a bootstrap password.");
            }
        }
    }

    [Theory]
    [InlineData("first-admin", null, "Authentication:BootstrapAdmin")]
    [InlineData(null, "the-initial-admin-password", "Authentication:BootstrapAdmin")]
    [InlineData("first-admin", "short", "Authentication:BootstrapAdmin:Password")]
    [InlineData("bad name", "the-initial-admin-password", "Authentication:BootstrapAdmin:Username")]
    [InlineData("ab", "the-initial-admin-password", "Authentication:BootstrapAdmin:Username")]
    public void UnusableBootstrapSettings_AreRefused_WithoutQuotingThePassword(string? username, string? password, string setting)
    {
        var options = ValidOptions();
        options.BootstrapAdmin.Username = username;
        options.BootstrapAdmin.Password = password;

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains(setting, error, StringComparison.Ordinal);
        if (password is not null)
        {
            Assert.DoesNotContain(password, error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1441)]
    public void TokenLifetimeOutOfRange_IsRefused(int minutes)
    {
        var options = ValidOptions();
        options.Jwt.AccessTokenLifetimeMinutes = minutes;

        Assert.Contains("AccessTokenLifetimeMinutes", options.Validate(), StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_WithASigningKey_AreUsable_AndTheTokenLifetimeIsShort()
    {
        var options = ValidOptions();

        Assert.Null(options.Validate());
        Assert.Equal(30, options.Jwt.AccessTokenLifetimeMinutes);
        Assert.Equal("aurora-db-manager", options.Jwt.Issuer);
        Assert.Equal("aurora-db-manager-api", options.Jwt.Audience);
        Assert.False(options.BootstrapAdmin.IsConfigured);
    }

    [Fact]
    public async Task ConfiguredTokenLifetime_IsTheLifetimeOfIssuedTokens()
    {
        var (_, anonymous) = Application((Name, Password), options => options.Jwt.AccessTokenLifetimeMinutes = 5);
        var before = DateTimeOffset.UtcNow;

        var body = await (await anonymous.LoginAsync(Name, Password)).ReadJsonAsync(HttpStatusCode.OK);

        Assert.InRange(body.GetProperty("expiresAt").GetDateTimeOffset(), before.AddMinutes(5).AddSeconds(-1), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static AuthOptions ValidOptions() => new() { Jwt = { SigningKey = ApiFactory.SigningKey } };
}
