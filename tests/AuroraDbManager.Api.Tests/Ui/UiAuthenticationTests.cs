using System.Net;
using System.Net.Http.Json;
using AuroraDbManager.Api.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Ui;

/// <summary>
/// Signing in to and out of the UI, through the real pages with the real cookie authentication:
/// users are in the database, forms carry antiforgery tokens, and the cookie is the one the
/// application sets.
/// </summary>
public sealed class UiAuthenticationTests : IDisposable
{
    private readonly WebFactory _factory = new() { BootstrapAdmin = (Browser.Admin, Browser.Password) };
    private readonly HttpClient _browser;
    private readonly HttpClient _api;

    public UiAuthenticationTests()
    {
        _browser = _factory.CreateBrowser();
        _api = _factory.CreateClientAs(UserRole.Admin);
    }

    public void Dispose()
    {
        _api.Dispose();
        _browser.Dispose();
        _factory.Dispose();
    }

    private Task<Guid> AdminIdAsync() => _factory.WithDbAsync(db => db.Users.Where(user => user.Username == Browser.Admin).Select(user => user.Id).SingleAsync());

    // --- Not signed in ------------------------------------------------------------------------

    [Theory]
    [InlineData("/", "%2F")]
    [InlineData("/instances", "%2Finstances")]
    [InlineData("/jobs", "%2Fjobs")]
    [InlineData("/users", "%2Fusers")]
    [InlineData("/no-such-page", "%2Fno-such-page")]
    public async Task AnonymousRequestForAPage_IsRedirectedToTheLoginPage_WhichRemembersWhereItWasGoing(string path, string encoded)
    {
        var response = await _browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/login?returnUrl={encoded}", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task LoginPage_Renders_AForm_WithLabels_AnAntiforgeryToken_AndNothingOfTheApplication()
    {
        var html = await _browser.GetHtmlAsync("/login");

        Assert.Contains("<form method=\"post\"", html, StringComparison.Ordinal);
        Assert.Contains("<label class=\"field-label\" for=\"Input_Username\">Username</label>", html, StringComparison.Ordinal);
        Assert.Contains("<label class=\"field-label\" for=\"Input_Password\">Password</label>", html, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", html, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"current-password\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"__RequestVerificationToken\"", html, StringComparison.Ordinal);
        Assert.Contains(">Sign in</button>", html, StringComparison.Ordinal);
        // No navigation, no data: nothing for someone who has not signed in.
        Assert.Empty(Browser.NavigationOf(html));
        Assert.DoesNotContain("Sign out", html, StringComparison.Ordinal);
    }

    // --- Signing in ---------------------------------------------------------------------------

    [Fact]
    public async Task ValidLogin_SetsTheSessionCookie_AndLeadsToTheDashboard()
    {
        var response = await _browser.SubmitLoginAsync(Browser.Admin, Browser.Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        Assert.Contains("aurora.session", response.SetCookies().Keys);

        var dashboard = await _browser.GetHtmlAsync("/");
        Assert.Contains("<h1>Overview</h1>", dashboard, StringComparison.Ordinal);
        Assert.Contains("Signed in as <strong>root-admin</strong>", dashboard, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ROOT-ADMIN")]
    [InlineData("  root-admin ")]
    public async Task Login_UsernameIsNotCaseSensitive_LikeTheApis(string username)
    {
        var response = await _browser.SubmitLoginAsync(username, Browser.Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task SessionCookie_IsHttpOnly_SameSiteLax_ForTheWholeSite_AndCarriesNothingReadable()
    {
        var cookie = (await _browser.SubmitLoginAsync(Browser.Admin, Browser.Password)).SetCookies()["aurora.session"];

        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        // A session cookie: gone when the browser is closed. How long it is accepted at most is
        // inside it, where the browser cannot change it; see the test of the session's lifetime.
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-age=", cookie, StringComparison.OrdinalIgnoreCase);

        // Encrypted: neither who it is for nor anything else can be read from it, and it is no JWT.
        Assert.DoesNotContain(Browser.Admin, cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Browser.Password, cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("admin", cookie[..cookie.IndexOf(';')], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eyJ", cookie, StringComparison.Ordinal);
        Assert.InRange(cookie.Length, 100, 2000);
    }

    [Fact]
    public async Task InProduction_TheCookiesAreSecure_AndBoundToTheHost()
    {
        using var factory = new WebFactory { BootstrapAdmin = (Browser.Admin, Browser.Password), EnvironmentName = "Production" };
        using var browser = factory.CreateBrowser(baseAddress: "https://aurora.example.test");

        var login = await browser.GetAsync("/login");
        var cookies = (await browser.SubmitLoginAsync(Browser.Admin, Browser.Password)).SetCookies();

        var session = cookies["__Host-aurora.session"];
        Assert.Contains("secure", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", session, StringComparison.OrdinalIgnoreCase);
        var antiforgery = login.SetCookies()["__Host-aurora.antiforgery"];
        Assert.Contains("secure", antiforgery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", antiforgery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", antiforgery, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("<h1>Overview</h1>", await browser.GetHtmlAsync("/"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/jobs", "/jobs")]
    [InlineData("/instances?page=2", "/instances?page=2")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/login", "/")]
    public async Task AfterSigningIn_TheUserGoesWhereTheyWereGoing_ButNeverToAnotherSite(string returnUrl, string expected)
    {
        var response = await _browser.SubmitLoginAsync(Browser.Admin, Browser.Password, $"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task SignedInUser_VisitingTheLoginPage_IsSentToTheDashboard_NotAskedAgain()
    {
        await _browser.SignInAsync();

        var response = await _browser.GetAsync("/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
    }

    // --- Not signing in -----------------------------------------------------------------------

    [Fact]
    public async Task WrongPassword_UnknownUser_AndDisabledUser_GetTheSameGenericAnswer_AndNoCookie()
    {
        await _api.CreateUserAsync("sleeper", "sleeper-password-1", "operator", enabled: false);

        var responses = new[]
        {
            await _browser.SubmitLoginAsync(Browser.Admin, "not the password at all"),
            await _browser.SubmitLoginAsync("nobody-by-that-name", Browser.Password),
            await _browser.SubmitLoginAsync("sleeper", "sleeper-password-1")
        };

        var pages = new List<string>();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.DoesNotContain("aurora.session", response.SetCookies().Keys);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("Invalid username or password.", html, StringComparison.Ordinal);
            Assert.Contains("role=\"alert\"", html, StringComparison.Ordinal);
            // Nothing that was typed comes back: not the password, not even the username.
            Assert.DoesNotContain("not the password at all", html, StringComparison.Ordinal);
            Assert.DoesNotContain("sleeper", html, StringComparison.Ordinal);
            Assert.DoesNotContain("nobody-by-that-name", html, StringComparison.Ordinal);
            // The token of the form differs from page to page; everything else must not.
            pages.Add(System.Text.RegularExpressions.Regex.Replace(html, "value=\"[^\"]{40,}\"", "value=\"\""));
        }

        Assert.Single(pages.Distinct());
        Assert.Equal(HttpStatusCode.Redirect, (await _browser.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task EmptyForm_IsAnsweredWithFieldErrors_FromTheServer()
    {
        var response = await _browser.SubmitLoginAsync(string.Empty, string.Empty);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Enter your username.", html, StringComparison.Ordinal);
        Assert.Contains("Enter your password.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("aurora.session", response.SetCookies().Keys);
    }

    [Fact]
    public async Task LoginWithoutAnAntiforgeryToken_IsRefused_EvenWithTheRightPassword()
    {
        var response = await _browser.PostFormAsync(
            "/login", [new("Input.Username", Browser.Admin), new("Input.Password", Browser.Password)], withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("aurora.session", response.SetCookies().Keys);
        Assert.Contains("That request could not be processed", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginWithATokenFromAnotherBrowser_IsRefused()
    {
        using var other = _factory.CreateBrowser();
        var foreignToken = await other.AntiforgeryTokenAsync("/login");
        await _browser.GetAsync("/login");

        var response = await _browser.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = Browser.Admin,
            ["Input.Password"] = Browser.Password,
            ["__RequestVerificationToken"] = foreignToken
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Rate limiting ------------------------------------------------------------------------

    [Fact]
    public async Task RepeatedLoginAttempts_AreLimited_WithAPageThatSaysWhenToTryAgain_AndLookingAtTheFormIsNotAnAttempt()
    {
        using var factory = new WebFactory
        {
            BootstrapAdmin = (Browser.Admin, Browser.Password),
            ConfigureSecurity = options => options.LoginRateLimit.PermitLimit = 3
        };
        using var browser = factory.CreateBrowser();

        // Each attempt fetches the form first; those GETs must not use the limit up.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SubmitLoginAsync(Browser.Admin, $"wrong-password-{i}")).StatusCode);
        }

        var refused = await browser.SubmitLoginAsync(Browser.Admin, Browser.Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.InRange(int.Parse(Assert.Single(refused.Headers.GetValues("Retry-After"))), 1, 60);
        var html = await refused.Content.ReadAsStringAsync();
        Assert.Equal("text/html", refused.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Too many attempts", html, StringComparison.Ordinal);
        Assert.Contains("Try again in", html, StringComparison.Ordinal);
        Assert.Contains("Request ID:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("aurora.session", refused.SetCookies().Keys);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/login")).StatusCode);

        // One limit for both ways of signing in: the API's login is refused for this client too.
        using var api = factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await api.LoginAsync(Browser.Admin, Browser.Password)).StatusCode);
    }

    // --- Signing out --------------------------------------------------------------------------

    [Fact]
    public async Task Logout_IsAPost_ThatClearsTheCookie_AndLeadsToTheLoginPage()
    {
        await _browser.SignInAsync();

        var response = await _browser.PostFormAsync("/logout", [], tokenFrom: "/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.OriginalString);
        var cleared = response.SetCookies()["aurora.session"];
        Assert.StartsWith("aurora.session=;", cleared, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", cleared, StringComparison.Ordinal);
        // Signed out for good: the pages ask for a sign-in again.
        Assert.Equal(HttpStatusCode.Redirect, (await _browser.GetAsync("/")).StatusCode);
        Assert.StartsWith("/login", (await _browser.GetAsync("/jobs")).Headers.Location!.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_ByGet_ChangesNothing()
    {
        await _browser.SignInAsync();

        var response = await _browser.GetAsync("/logout");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("aurora.session", response.SetCookies().Keys);
        Assert.Equal(HttpStatusCode.OK, (await _browser.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutAnAntiforgeryToken_IsRefused_AndTheUserStaysSignedIn()
    {
        await _browser.SignInAsync();

        var response = await _browser.PostFormAsync("/logout", [], withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _browser.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task LayoutsLogoutForm_IsAPostWithAToken()
    {
        await _browser.SignInAsync();

        var html = await _browser.GetHtmlAsync("/");

        var form = html[html.IndexOf("<form method=\"post\" action=\"/logout\"", StringComparison.Ordinal)..];
        form = form[..form.IndexOf("</form>", StringComparison.Ordinal)];
        Assert.Contains("name=\"__RequestVerificationToken\"", form, StringComparison.Ordinal);
        Assert.Contains("Sign out", form, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/logout\"", html, StringComparison.Ordinal);
    }

    // --- The session follows the user ----------------------------------------------------------

    [Fact]
    public async Task DisabledUser_IsSignedOutAtTheirNextRequest()
    {
        var userId = await _api.CreateUserAsync("olivia", "olivia-password-1", "operator");
        await _browser.SignInAsync("olivia", "olivia-password-1");
        Assert.Equal(HttpStatusCode.OK, (await _browser.GetAsync("/")).StatusCode);

        await (await _api.PutAsJsonAsync($"{UsersUrl}/{userId}", new { role = "operator", enabled = false })).ReadJsonAsync(HttpStatusCode.OK);

        var response = await _browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletedUser_IsSignedOutAtTheirNextRequest()
    {
        var userId = await _api.CreateUserAsync("olivia", "olivia-password-1", "viewer");
        await _browser.SignInAsync("olivia", "olivia-password-1");

        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"{UsersUrl}/{userId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await _browser.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task ChangedRole_AppliesFromTheNextRequest_InBothDirections()
    {
        var userId = await _api.CreateUserAsync("olivia", "olivia-password-1", "viewer");
        await _browser.SignInAsync("olivia", "olivia-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await _browser.GetAsync("/users")).StatusCode);

        await (await _api.PutAsJsonAsync($"{UsersUrl}/{userId}", new { role = "admin", enabled = true })).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await _browser.GetAsync("/users")).StatusCode);
        Assert.Contains("Users", Browser.NavigationOf(await _browser.GetHtmlAsync("/")));

        await (await _api.PutAsJsonAsync($"{UsersUrl}/{userId}", new { role = "viewer", enabled = true })).ReadJsonAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Forbidden, (await _browser.GetAsync("/users")).StatusCode);
        Assert.DoesNotContain("Users", Browser.NavigationOf(await _browser.GetHtmlAsync("/")));
    }

    [Fact]
    public async Task Session_EndsAfterItsFixedLifetime_HoweverMuchItWasUsed()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 10, 10, 0, 0, TimeSpan.Zero));
        using var factory = new WebFactory { BootstrapAdmin = (Browser.Admin, Browser.Password), Clock = clock };
        using var browser = factory.CreateBrowser();
        await browser.SignInAsync();

        // Used every hour: with a sliding lifetime it would never end.
        for (var hour = 0; hour < 7; hour++)
        {
            clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);
        }

        clock.Advance(TimeSpan.FromHours(1.1));
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/")).StatusCode);
    }

    // --- Nothing leaks ------------------------------------------------------------------------

    [Fact]
    public async Task Passwords_AreNeverLogged_NeverEchoed_AndNoTokenIsEverGivenToTheBrowser()
    {
        const string attempted = "a-wrong-password-attempt";
        var failed = await _browser.SubmitLoginAsync(Browser.Admin, attempted);
        var succeeded = await _browser.SubmitLoginAsync(Browser.Admin, Browser.Password);
        var dashboard = await _browser.GetAsync("/");
        var adminId = await AdminIdAsync();

        Assert.Contains(_factory.Logs.Entries, entry => entry.Contains($"User {adminId} signed in", StringComparison.Ordinal));
        Assert.Contains(_factory.Logs.Entries, entry => entry.Contains("Login failed for user", StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(Browser.Password, StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(attempted, StringComparison.Ordinal));

        foreach (var response in new[] { failed, succeeded, dashboard })
        {
            var everything = await response.Content.ReadAsStringAsync() + string.Join('\n', response.Headers.SelectMany(header => header.Value));
            Assert.DoesNotContain(Browser.Password, everything, StringComparison.Ordinal);
            Assert.DoesNotContain(attempted, everything, StringComparison.Ordinal);
            // No JWT anywhere a browser could find one: not in a page, a header or a cookie.
            Assert.DoesNotContain("eyJhbGci", everything, StringComparison.Ordinal);
            Assert.DoesNotContain("accessToken", everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bearer", everything, StringComparison.Ordinal);
        }

        // The session cookie's value is not logged either.
        var session = succeeded.SetCookies()["aurora.session"];
        var value = session[(session.IndexOf('=') + 1)..session.IndexOf(';')];
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(value, StringComparison.Ordinal));
    }
}
