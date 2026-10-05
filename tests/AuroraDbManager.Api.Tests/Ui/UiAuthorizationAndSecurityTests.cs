using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Infrastructure.Security;
using AuroraDbManager.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Ui;

/// <summary>
/// Who is shown and allowed what in the UI, and how the UI host keeps its two kinds of client
/// apart: pages take the cookie, the API takes the bearer token, and neither takes the other's.
/// </summary>
public sealed partial class UiAuthorizationAndSecurityTests : IDisposable
{
    private const string UserPassword = "a-perfectly-fine-password";

    private readonly WebFactory _factory = new() { BootstrapAdmin = (Browser.Admin, Browser.Password) };
    private readonly HttpClient _api;
    private readonly List<HttpClient> _browsers = [];

    public UiAuthorizationAndSecurityTests()
    {
        _api = _factory.CreateClientAs(UserRole.Admin);
    }

    public void Dispose()
    {
        foreach (var browser in _browsers)
        {
            browser.Dispose();
        }

        _api.Dispose();
        _factory.Dispose();
    }

    private async Task<HttpClient> SignedInAsync(string role)
    {
        var browser = _factory.CreateBrowser();
        _browsers.Add(browser);
        if (role == "admin")
        {
            await browser.SignInAsync();
        }
        else
        {
            await _api.CreateUserAsync($"the-{role}", UserPassword, role);
            await browser.SignInAsync($"the-{role}", UserPassword);
        }

        return browser;
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : string.Empty;

    // --- Navigation and pages, by role ---------------------------------------------------------

    private static readonly string[] Sections = ["Overview", "Instances", "Jobs", "Monitoring"];

    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    public async Task ViewerAndOperator_SeeEverySection_ButNotAdministration(string role)
    {
        var browser = await SignedInAsync(role);

        var html = await browser.GetHtmlAsync("/");

        Assert.Equal(Sections, Browser.NavigationOf(html));
        Assert.DoesNotContain("Administration", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/users\"", html, StringComparison.Ordinal);
        Assert.Contains($"<span class=\"user-role\">{role}</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_SeesUserManagementInTheNavigation()
    {
        var browser = await SignedInAsync("admin");

        var html = await browser.GetHtmlAsync("/");

        Assert.Equal([.. Sections, "Users"], Browser.NavigationOf(html));
        Assert.Contains("Administration", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"user-name\">root-admin</span>", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    public async Task GoingToAnAdministratorsPageDirectly_IsForbidden_WhateverTheMenuShowed(string role)
    {
        var browser = await SignedInAsync(role);

        var response = await browser.GetAsync("/users");

        // 403, at the address that was asked for, as a page of the application.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("You don&#x27;t have permission", html, StringComparison.Ordinal);
        Assert.Contains("Request ID:", html, StringComparison.Ordinal);
        Assert.Equal(Sections, Browser.NavigationOf(html));
        // And nothing of what the page would have shown.
        Assert.DoesNotContain(Browser.Admin, html.Replace("the-", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("<table", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_ReachesTheUsersPage_WhichNeverShowsAPasswordHash()
    {
        var browser = await SignedInAsync("admin");
        await _api.CreateUserAsync("olivia", UserPassword, "operator", enabled: false);
        var hashes = await _factory.WithDbAsync(db => db.Users.Select(user => user.PasswordHash).ToListAsync());

        var html = await browser.GetHtmlAsync("/users");

        Assert.Contains("<td class=\"cell-primary\">olivia</td>", html, StringComparison.Ordinal);
        Assert.Contains("Disabled", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\"", html, StringComparison.Ordinal);
        Assert.All(hashes, hash => Assert.DoesNotContain(hash, html, StringComparison.Ordinal));
        Assert.DoesNotContain(UserPassword, html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/", "Overview")]
    [InlineData("/instances", "Instances")]
    [InlineData("/jobs", "Jobs")]
    [InlineData("/monitoring", "Monitoring")]
    public async Task EverySection_RendersForAViewer_WithItsTitle_ItsPlaceInTheNavigation_AndTheLayout(string path, string title)
    {
        var browser = await SignedInAsync("viewer");

        var html = await browser.GetHtmlAsync(path);

        Assert.Contains($"<h1>{title}</h1>", html, StringComparison.Ordinal);
        Assert.Contains($"<title>{title} &#xB7; Aurora</title>", html, StringComparison.Ordinal);
        Assert.Matches($"aria-current=\"page\">\\s*<svg.*?</svg>\\s*{title}\\s*</a>", html.Replace("\n", " ", StringComparison.Ordinal));
        Assert.Single(Regex.Matches(html, "aria-current=\"page\""));
        Assert.Contains("<a class=\"skip-link\" href=\"#main\">", html, StringComparison.Ordinal);
        Assert.Contains("<main class=\"content\" id=\"main\">", html, StringComparison.Ordinal);
        Assert.Contains("<nav class=\"sidebar\" id=\"sections\" aria-label=\"Sections\">", html, StringComparison.Ordinal);
        Assert.Contains("Sign out", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPage_NeedsASignedInUser_ExceptSigningInOutAndTheErrorPage_AndTheUsersPageNeedsAnAdministrator()
    {
        var pages = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<PageActionDescriptor>() is not null)
            .ToList();

        Assert.True(pages.Count >= 11);
        var anonymous = pages
            .Where(page => page.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(page => "/" + page.RoutePattern.RawText!.TrimStart('/'))
            .Distinct()
            .Order();
        Assert.Equal(["/error/{code:int?}", "/login", "/logout"], anonymous);

        var users = pages.Single(page => page.RoutePattern.RawText!.TrimStart('/') == "users");
        Assert.Contains(users.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == "admin");
    }

    // --- Two kinds of client, kept apart -------------------------------------------------------

    [Fact]
    public async Task SessionCookie_IsNotAccepted_ByTheApi()
    {
        var browser = await SignedInAsync("admin");

        // Signed in to the UI, and still nobody to the API: a cookie is sent by the browser on its
        // own, also when another site makes it, so the API must not act on one.
        var read = await browser.GetAsync(InstancesUrl);
        var write = await browser.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest());
        var users = await browser.PostAsJsonAsync(UsersUrl, new { username = "accomplice", password = UserPassword, role = "admin" });

        foreach (var response in new[] { read, write, users })
        {
            // Refused in the API's own terms: JSON, not a redirect to a sign-in page.
            await response.AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        }

        Assert.Equal(0, await _factory.WithDbAsync(db => db.Instances.CountAsync()));
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    [Fact]
    public async Task BearerToken_IsNotAccepted_ByThePages()
    {
        var browser = _factory.CreateBrowser();
        _browsers.Add(browser);
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WebFactory.TokenFor(UserRole.Admin));

        foreach (var path in new[] { "/", "/users", "/jobs" })
        {
            var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/login", response.Headers.Location!.PathAndQuery, StringComparison.Ordinal);
        }

        // The same token is what the API takes.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(InstancesUrl)).StatusCode);
    }

    [Fact]
    public async Task UiHost_ServesTheSameRestApi_WithItsOwnErrorsHeadersAndHealthChecks()
    {
        using var anonymous = _factory.CreateAnonymousClient();

        // The API as it is in the API host: JSON errors, the API's content security policy, public probes.
        var unauthorized = await anonymous.GetAsync(InstancesUrl);
        await unauthorized.AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        Assert.Equal(SecurityHardening.ApiContentSecurityPolicy, Header(unauthorized, "Content-Security-Policy"));
        await (await _api.GetAsync("/api/v1/no-such-thing")).AssertErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
        await (await _api.GetAsync($"{InstancesUrl}/{Guid.NewGuid()}")).AssertErrorAsync(HttpStatusCode.NotFound, "INSTANCE_NOT_FOUND");
        await (await _api.PostAsJsonAsync(InstancesUrl, new { })).AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal("healthy", (await (await anonymous.GetAsync(HealthUrl)).ReadJsonAsync(HttpStatusCode.OK)).Status());
        Assert.Equal("healthy", (await (await anonymous.GetAsync(ReadinessUrl)).ReadJsonAsync(HttpStatusCode.OK)).Status());

        // Signing in through the API gives a token, as ever, and the token works.
        await ApiClientExtensions.SignInAsync(anonymous, Browser.Admin, Browser.Password);
        var (instanceId, jobId) = await anonymous.CreateInstanceAsync();
        await _factory.ProcessJobAsync(jobId);
        Assert.Equal("running", (await anonymous.GetInstanceAsync(instanceId)).Status());

        // And every API endpoint the API host has is here, under the same policies.
        using var apiHost = new ApiFactory();
        Assert.Equal(ApiEndpoints(apiHost.Services), ApiEndpoints(_factory.Services));

        static List<string> ApiEndpoints(IServiceProvider services) => services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<PageActionDescriptor>() is null)
            .Select(endpoint =>
                $"{string.Join('/', endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["GET"])} {endpoint.RoutePattern.RawText} "
                + $"[{string.Join(',', endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy))}]"
                + (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null ? string.Empty : " anonymous"))
            .Order()
            .ToList();
    }

    // --- Headers and content security policy ---------------------------------------------------

    [Theory]
    [InlineData("/login")]
    [InlineData("/")]
    [InlineData("/users")]
    [InlineData("/no-such-page")]
    public async Task Pages_CarryTheSecurityHeaders_WithAPolicyThatAllowsOnlyThisHostsOwnStylesAndScripts(string path)
    {
        var browser = path == "/login" ? _factory.CreateBrowser() : await SignedInAsync("viewer");
        _browsers.Add(browser);

        var response = await browser.GetAsync(path);

        var policy = Header(response, "Content-Security-Policy");
        Assert.Equal(WebProgram.PageContentSecurityPolicy, policy);
        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("style-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("form-action 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("*", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("data:", policy, StringComparison.Ordinal);

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        // A page is for the user who asked for it; nothing keeps a copy, the back button after signing out included.
        Assert.Contains("no-store", Header(response, "Cache-Control"), StringComparison.Ordinal);
        Assert.Empty(Header(response, "Server"));
        Assert.Matches("^[0-9a-f]{32}$", Header(response, "X-Request-Id"));
    }

    [Theory]
    [InlineData("/login", null)]
    [InlineData("/", "admin")]
    [InlineData("/instances", "admin")]
    [InlineData("/jobs", "admin")]
    [InlineData("/users", "admin")]
    [InlineData("/instances/create", "admin")]
    [InlineData("/monitoring", "viewer")]
    [InlineData("/styleguide", "viewer")]
    [InlineData("/users", "viewer")]
    [InlineData("/no-such-page", "viewer")]
    public async Task Pages_ContainNothingTheirContentSecurityPolicyWouldBlock(string path, string? role)
    {
        HttpClient browser;
        if (role is null)
        {
            browser = _factory.CreateBrowser();
            _browsers.Add(browser);
        }
        else
        {
            browser = await SignedInAsync(role);
        }

        var html = await (await browser.GetAsync(path)).Content.ReadAsStringAsync();

        // No inline script, no inline style, no inline handler: the policy would refuse them, and
        // a page that needed one would be a page that asked for the policy to be weakened.
        Assert.Empty(InlineScript().Matches(html));
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(StyleAttribute().Matches(html));
        Assert.Empty(EventHandlerAttribute().Matches(html));
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);

        // Everything a page loads is this host's own.
        var loaded = LoadedResource().Matches(html).Select(match => match.Groups[1].Value).ToList();
        Assert.NotEmpty(loaded);
        Assert.All(loaded, url => Assert.StartsWith("/", url, StringComparison.Ordinal));
        Assert.All(loaded, url => Assert.False(url.StartsWith("//", StringComparison.Ordinal), url));
        Assert.All(FormAction().Matches(html).Select(match => match.Groups[1].Value), action => Assert.StartsWith("/", action, StringComparison.Ordinal));
    }

    [GeneratedRegex("<script(?![^>]*\\bsrc=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    [GeneratedRegex("<[^>]+\\sstyle\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttribute();

    [GeneratedRegex("<[^>]+\\son[a-z]+\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerAttribute();

    [GeneratedRegex("<(?:script|link|img)[^>]+(?:src|href)=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LoadedResource();

    [GeneratedRegex("<form[^>]+\\saction=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex FormAction();

    [Fact]
    public async Task StylesAndScripts_AreServedFromThisHost_ToAnyone_Cacheably_AndUseNoBrowserStorage()
    {
        using var browser = _factory.CreateBrowser();
        var login = await browser.GetHtmlAsync("/login");
        var assets = LoadedResource().Matches(login).Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToList();
        Assert.Contains(assets, asset => asset.StartsWith("/css/aurora.css?v=", StringComparison.Ordinal));
        Assert.Contains(assets, asset => asset.StartsWith("/js/aurora.js?v=", StringComparison.Ordinal));

        foreach (var asset in assets)
        {
            var response = await browser.GetAsync(asset);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("public", Header(response, "Cache-Control"), StringComparison.Ordinal);
            Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        }

        // The script keeps nothing in the browser, and certainly no token; it talks to nobody.
        var script = await (await browser.GetAsync("/js/aurora.js")).Content.ReadAsStringAsync();
        Assert.Equal("text/javascript", (await browser.GetAsync("/js/aurora.js")).Content.Headers.ContentType?.MediaType);
        foreach (var forbidden in new[] { "localStorage", "sessionStorage", "indexedDB", "document.cookie", "fetch(", "XMLHttpRequest", "eval(", "innerHTML", "Bearer", "token" })
        {
            Assert.DoesNotContain(forbidden, script, StringComparison.OrdinalIgnoreCase);
        }

        var styles = await (await browser.GetAsync("/css/aurora.css")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("@import", styles, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", styles, StringComparison.OrdinalIgnoreCase);
    }

    // --- Request ids and error pages -----------------------------------------------------------

    [Fact]
    public async Task ErrorPages_ShowTheRequestId_ThatTheResponseHeaderAndTheLogHave()
    {
        var browser = await SignedInAsync("viewer");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/users");
        request.Headers.Add("X-Request-Id", "ui-check-403");

        var response = await browser.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("ui-check-403", Header(response, "X-Request-Id"));
        Assert.Contains("Request ID: <span class=\"mono\">ui-check-403</span>", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains(_factory.Logs.Entries, entry => entry.EndsWith("scope: Request ui-check-403", StringComparison.Ordinal));
        Assert.Contains(_factory.Logs.Entries, entry => entry.Contains("HTTP GET /users responded 403", StringComparison.Ordinal) && !entry.Contains("anonymous", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotFound_IsAPageOfTheApplication_WithTheStatus_TheLayout_AndAWayBack()
    {
        var browser = await SignedInAsync("viewer");

        var response = await browser.GetAsync("/instances/does-not-exist/at-all");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Page not found", html, StringComparison.Ordinal);
        Assert.Contains("Back to the overview", html, StringComparison.Ordinal);
        Assert.Contains("Request ID:", html, StringComparison.Ordinal);
        Assert.Equal(Sections, Browser.NavigationOf(html));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task UnhandledFailure_IsAGenericErrorPage_InEveryEnvironment_WithNothingOfTheException(string environment)
    {
        using var factory = new WebFactory { BootstrapAdmin = (Browser.Admin, Browser.Password), EnvironmentName = environment };
        using var browser = factory.CreateBrowser(baseAddress: "https://aurora.example.test");
        await browser.SignInAsync();
        await factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE jobs"));

        var response = await browser.GetAsync("/jobs");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Something went wrong", html, StringComparison.Ordinal);
        Assert.Contains($"Request ID: <span class=\"mono\">{Header(response, "X-Request-Id")}</span>", html, StringComparison.Ordinal);
        Assert.Contains("Sign out", html, StringComparison.Ordinal);
        foreach (var leaked in new[] { "Exception", "SQLite", "no such table", "   at ", "jobs.", "DbContext", "stack" })
        {
            Assert.DoesNotContain(leaked, html, StringComparison.OrdinalIgnoreCase);
        }

        // The log has what the page does not.
        Assert.Contains(factory.Logs.Entries, entry => entry.Contains("no such table", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SystemDatabaseUnavailable_ASignedInUserGetsTheErrorPage_NotALoopOrABlankResponse()
    {
        var browser = await SignedInAsync("admin");
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE users"));

        var response = await browser.GetAsync("/jobs");
        var users = await browser.GetAsync("/users");

        // Jobs can still be read; what cannot be is answered with the error page.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, users.StatusCode);
        Assert.Contains("Something went wrong", await users.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/error")]
    [InlineData("/error/200")]
    [InlineData("/error/999")]
    public async Task ErrorPage_AskedForDirectly_IsJustNotFound(string path)
    {
        using var browser = _factory.CreateBrowser();

        var response = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Page not found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(403, "You don&#x27;t have permission")]
    [InlineData(409, "That could not be done right now")]
    [InlineData(413, "That request is too large")]
    [InlineData(429, "Too many attempts")]
    [InlineData(500, "Something went wrong")]
    [InlineData(503, "Something went wrong")]
    public async Task ErrorPage_HasWordsForEachKindOfFailure_AndNeverInternals(int status, string title)
    {
        using var browser = _factory.CreateBrowser();

        var response = await browser.GetAsync($"/error/{status}");

        Assert.Equal(status, (int)response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"<h1>{title}</h1>", html, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", html, StringComparison.Ordinal);
        Assert.Contains("Request ID:", html, StringComparison.Ordinal);
        // Not signed in: no navigation, and a way to sign in.
        Assert.Empty(Browser.NavigationOf(html));
        Assert.Contains("href=\"/login\"", html, StringComparison.Ordinal);
    }
}
