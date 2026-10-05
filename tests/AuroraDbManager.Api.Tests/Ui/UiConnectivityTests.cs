using System.Net;
using System.Text.RegularExpressions;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuroraDbManager.Api.Tests.Ui;

/// <summary>
/// How an instance and its databases are reached, as the pages show it, and the page that turns
/// external access on and off: through the real Web host, real sign-ins and real antiforgery
/// tokens, with the connectivity service doing what the page asks.
/// </summary>
public sealed partial class UiConnectivityTests : IDisposable
{
    private const string UserPassword = "a-perfectly-fine-password";

    private readonly WebFactory _factory = new() { BootstrapAdmin = (Browser.Admin, Browser.Password) };
    private readonly HttpClient _api;
    private readonly List<HttpClient> _browsers = [];

    public UiConnectivityTests()
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

    private static string Flat(string html) => Regex.Replace(html, "\\s+", " ");

    private static async Task<string> HtmlOf(HttpResponseMessage response) => Flat(await response.Content.ReadAsStringAsync());

    private static string Section(string html, string id) =>
        Regex.Match(html, $"<section class=\"section\" aria-labelledby=\"{id}\">.*?</section>").Value;

    private Task<(bool Enabled, int? Port)> RecordAsync(Guid instanceId) => _factory.WithDbAsync(async db =>
    {
        var instance = await db.Instances.AsNoTracking().SingleAsync(i => i.Id == instanceId);
        return (instance.ExternalAccessEnabled, instance.ExternalPort);
    });

    private static Task<HttpResponseMessage> PostAsync(HttpClient browser, Guid instanceId, string handler, string? tokenFrom = null) =>
        browser.PostFormAsync($"/instances/{instanceId}/external-access?handler={handler}", [], tokenFrom: tokenFrom ?? $"/instances/{instanceId}/external-access");

    // --- Disabled, the way every instance starts ------------------------------------------------

    [Fact]
    public async Task InstancePage_ShowsExternalAccessDisabled_AndWhereTheServerIsOnTheDockerNetwork()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var admin = await SignedInAsync("admin");
        var viewer = await SignedInAsync("viewer");

        var connection = Section(Flat(await admin.GetHtmlAsync($"/instances/{instanceId}")), "connection-title");
        var forViewer = Section(Flat(await viewer.GetHtmlAsync($"/instances/{instanceId}")), "connection-title");

        Assert.Contains("<h2 id=\"connection-title\">Connection</h2>", connection, StringComparison.Ordinal);
        Assert.Matches("<dt>External access</dt> <dd> <span class=\"badge badge-neutral\"><svg.*?</svg> Disabled</span>", connection);
        Assert.Contains("The database port is not published. The server is reachable from the Docker network only.", connection, StringComparison.Ordinal);
        // Nothing of an external endpoint while there is none.
        Assert.DoesNotContain("15432", connection, StringComparison.Ordinal);
        Assert.DoesNotContain("Bound to", connection, StringComparison.Ordinal);
        Assert.DoesNotContain("data-copy", connection, StringComparison.Ordinal);
        Assert.Contains("<dt>Network</dt> <dd class=\"mono\">aurora-db</dd>", connection, StringComparison.Ordinal);
        Assert.Contains($"<dt>Host</dt> <dd class=\"mono\">aurora-instance-{instanceId}</dd>", connection, StringComparison.Ordinal);
        Assert.Contains("<dt>Port</dt> <dd class=\"mono\">5432</dd>", connection, StringComparison.Ordinal);
        Assert.Contains("<dt>Username</dt> <dd class=\"mono\">postgres</dd>", connection, StringComparison.Ordinal);
        // The way to change it is offered to an administrator, and to nobody else.
        Assert.Contains($"<a class=\"button\" href=\"/instances/{instanceId}/external-access\">Enable external access</a>", connection, StringComparison.Ordinal);
        Assert.DoesNotContain("external-access\"", forViewer, StringComparison.Ordinal);
        Assert.Contains("Disabled</span>", forViewer, StringComparison.Ordinal);
    }

    // --- Enabling ------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_EnablesExternalAccess_OnlyByPostingTheConfirmation_AndIsToldOnceItIsDone()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api, name: "orders");
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "app");
        var admin = await SignedInAsync("admin");
        var path = $"/instances/{instanceId}/external-access";

        // Asking is a GET, and changes nothing, with whatever it carries.
        var question = Flat(await admin.GetHtmlAsync(path));
        await admin.GetAsync($"{path}?handler=Enable");
        await admin.GetAsync($"{path}?handler=Enable&enable=true");
        Assert.Equal((false, null), await RecordAsync(instanceId));
        Assert.Empty(_factory.Provisioner.AppliedExternalAccess);

        Assert.Contains("<h1>Enable external access?</h1>", question, StringComparison.Ordinal);
        Assert.Contains("This exposes the database port of <strong>orders</strong> outside the Docker network", question, StringComparison.Ordinal);
        Assert.Contains("The port is bound to <strong class=\"mono\">127.0.0.1</strong>: this server only. Other machines cannot connect.", question, StringComparison.Ordinal);
        Assert.Contains("It does not open or close this server's firewall, a cloud security group", question, StringComparison.Ordinal);
        Assert.Contains("<strong>The database server is restarted.</strong> Its data is kept, and it is unavailable to its clients until it is up again", question, StringComparison.Ordinal);
        Assert.Contains($"<a class=\"button\" href=\"/instances/{instanceId}\">Cancel</a>", question, StringComparison.Ordinal);
        Assert.Contains($"<form method=\"post\" class=\"form\" action=\"/instances/{instanceId}/external-access?handler=Enable\">", question, StringComparison.Ordinal);
        Assert.Contains("data-busy-label=\"Restarting the database server…\">Enable external access</button>", question, StringComparison.Ordinal);
        Assert.DoesNotContain("checked", question, StringComparison.Ordinal);
        // It promises a port, not the internet.
        Assert.DoesNotContain("internet", question, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publicly", question, StringComparison.OrdinalIgnoreCase);

        var response = await PostAsync(admin, instanceId, "Enable");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/instances/{instanceId}", response.Headers.Location!.OriginalString);
        Assert.Equal((true, 15432), await RecordAsync(instanceId));
        Assert.Equal([(instanceId, (int?)15432)], _factory.Provisioner.AppliedExternalAccess);

        var html = Flat(await admin.GetHtmlAsync($"/instances/{instanceId}"));
        var connection = Section(html, "connection-title");
        Assert.Matches("<div class=\"alert alert-success\" role=\"status\">.*?External access enabled on host port 15432\\. The database server was restarted\\.", html);
        Assert.Matches("<dt>External access</dt> <dd> <span class=\"badge badge-warning\"><svg.*?</svg> Enabled</span>", connection);
        Assert.Contains("Whether it can be reached from other machines depends on the address it is bound to and on this server's firewall and network.", connection, StringComparison.Ordinal);
        Assert.Contains("<dt>Host</dt> <dd> <span class=\"mono\">127.0.0.1</span> </dd>", connection, StringComparison.Ordinal);
        Assert.Contains("<dt>Port</dt> <dd class=\"mono\">15432</dd>", connection, StringComparison.Ordinal);
        Assert.Contains("<dt>Bound to</dt> <dd> <span class=\"mono\">127.0.0.1</span> <span class=\"muted\"> This server only: other machines cannot connect. </span> </dd>", connection, StringComparison.Ordinal);
        Assert.Contains("<dt>Protocol</dt> <dd>PostgreSQL</dd>", connection, StringComparison.Ordinal);
        Assert.Contains("<pre class=\"snippet\" id=\"connection-details\">Engine: PostgreSQL Host: 127.0.0.1 Port: 15432 Username: postgres</pre>", connection, StringComparison.Ordinal);
        // Copying is an enhancement: the button is there, hidden until script finds that it works.
        Assert.Contains("<button type=\"button\" class=\"button\" data-copy=\"#connection-details\" hidden>Copy connection details</button>", connection, StringComparison.Ordinal);
        Assert.Contains($"<a class=\"button\" href=\"{path}\">Disable external access</a>", connection, StringComparison.Ordinal);
        Assert.DoesNotContain("internet", connection, StringComparison.OrdinalIgnoreCase);

        // The database is where its instance is.
        var database = Section(Flat(await admin.GetHtmlAsync($"/instances/{instanceId}/databases/{databaseId}")), "connection-title");
        Assert.Contains("<dt>Host</dt> <dd> <span class=\"mono\">127.0.0.1</span> </dd> <dt>Port</dt> <dd class=\"mono\">15432</dd>", database, StringComparison.Ordinal);
        Assert.Contains("<dt>Database</dt> <dd class=\"mono\">app</dd> <dt>Username</dt> <dd class=\"mono\">postgres</dd>", database, StringComparison.Ordinal);
        Assert.Contains("<pre class=\"snippet\" id=\"external-template\" aria-labelledby=\"external-template-label\">postgresql://postgres:&lt;password&gt;@127.0.0.1:15432/app</pre>", database, StringComparison.Ordinal);
        Assert.Contains($"<pre class=\"snippet\" id=\"internal-template\" aria-labelledby=\"internal-template-label\">postgresql://postgres:&lt;password&gt;@aurora-instance-{instanceId}:5432/app</pre>", database, StringComparison.Ordinal);
        Assert.Contains("data-copy=\"#external-template\" hidden>Copy connection template</button>", database, StringComparison.Ordinal);
        Assert.Contains("Aurora keeps that password in its secret store and does not show it.", database, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabasePage_OfAPrivateInstance_ShowsOnlyTheWayInFromTheDockerNetwork()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api, engine: "mysql");
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "shop");
        var viewer = await SignedInAsync("viewer");

        var connection = Section(Flat(await viewer.GetHtmlAsync($"/instances/{instanceId}/databases/{databaseId}")), "connection-title");

        Assert.Contains("Disabled</span>", connection, StringComparison.Ordinal);
        Assert.DoesNotContain("external-template", connection, StringComparison.Ordinal);
        Assert.Contains($">mysql://root:&lt;password&gt;@aurora-instance-{instanceId}:3306/shop</pre>", connection, StringComparison.Ordinal);
        Assert.Contains("<dt>Port</dt> <dd class=\"mono\">3306</dd> <dt>Database</dt> <dd class=\"mono\">shop</dd> <dt>Username</dt> <dd class=\"mono\">root</dd>", connection, StringComparison.Ordinal);
        Assert.Contains($"External access is the instance's: <a href=\"/instances/{instanceId}\">production-db</a>", connection, StringComparison.Ordinal);
    }

    // --- Disabling -----------------------------------------------------------------------------

    [Fact]
    public async Task Admin_DisablesExternalAccess_OnlyByPostingTheConfirmation()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api, name: "orders");
        var admin = await SignedInAsync("admin");
        await PostAsync(admin, instanceId, "Enable");
        var path = $"/instances/{instanceId}/external-access";

        var question = Flat(await admin.GetHtmlAsync(path));
        await admin.GetAsync($"{path}?handler=Disable");
        Assert.Equal((true, 15432), await RecordAsync(instanceId));

        Assert.Contains("<h1>Disable external access?</h1>", question, StringComparison.Ordinal);
        Assert.Contains("It is currently on host port <strong class=\"mono\">15432</strong>.", question, StringComparison.Ordinal);
        Assert.Contains("<strong>The database server is restarted.</strong>", question, StringComparison.Ordinal);
        Assert.Contains($"action=\"/instances/{instanceId}/external-access?handler=Disable\"", question, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=Enable", question, StringComparison.Ordinal);

        var response = await PostAsync(admin, instanceId, "Disable");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal((false, null), await RecordAsync(instanceId));
        Assert.Equal([(instanceId, (int?)15432), (instanceId, null)], _factory.Provisioner.AppliedExternalAccess);
        var html = Flat(await admin.GetHtmlAsync(response.Headers.Location!.OriginalString));
        Assert.Matches("<div class=\"alert alert-success\" role=\"status\">.*?External access disabled\\. The database server was restarted\\.", html);
        Assert.Contains("Disabled</span>", Section(html, "connection-title"), StringComparison.Ordinal);
        Assert.DoesNotContain("15432", Section(html, "connection-title"), StringComparison.Ordinal);
    }

    // --- Refusals and failures -----------------------------------------------------------------

    [Fact]
    public async Task WhatTheServiceRefuses_IsShownOnThePage_WithTheStableCode_AndNothingChanges()
    {
        var busy = await _factory.CreateRunningInstanceAsync(_api, name: "busy");
        await _api.CreateDatabaseAsync(busy, "being_created");
        var (provisioning, _) = await _api.CreateInstanceAsync(name: "still-provisioning");
        var enabled = await _factory.CreateRunningInstanceAsync(_api, name: "enabled");
        var admin = await SignedInAsync("admin");
        await PostAsync(admin, enabled, "Enable");

        foreach (var (instanceId, handler, code, message) in new[]
                 {
                     (busy, "Enable", "DATABASE_OPERATION_IN_PROGRESS", "External access cannot be changed while one of the instance&#x27;s databases is being created or deleted."),
                     (provisioning, "Enable", "INSTANCE_NOT_READY", "External access can only be changed while the instance is running."),
                     (enabled, "Enable", "EXTERNAL_ACCESS_ALREADY_ENABLED", "External access is already enabled for this instance."),
                     (busy, "Disable", "EXTERNAL_ACCESS_ALREADY_DISABLED", "External access is already disabled for this instance.")
                 })
        {
            var response = await PostAsync(admin, instanceId, handler, tokenFrom: "/instances");

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains($"<div class=\"alert-title\">{code}</div> <div>{message}</div>", await HtmlOf(response), StringComparison.Ordinal);
        }

        Assert.Equal((false, null), await RecordAsync(busy));
        Assert.Equal((false, null), await RecordAsync(provisioning));
        Assert.Equal((true, 15432), await RecordAsync(enabled));
        // The page of an instance that is not running says so before anything is sent, and the instance page does not offer the change.
        Assert.Contains("External access can only be changed while the instance is running.", await admin.GetHtmlAsync($"/instances/{provisioning}/external-access"), StringComparison.Ordinal);
        Assert.DoesNotContain("external-access\"", await admin.GetHtmlAsync($"/instances/{provisioning}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenDockerCannotApplyTheChange_ThePageSaysSo_ClaimsNothing_AndShowsNothingInternal()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var password = await _factory.AdminPasswordAsync(instanceId);
        _factory.Provisioner.ApplyExternalAccessFailure = new InstanceProvisioningException(
            "DOCKER_PORT_CONFIGURATION_FAILED",
            "The database server could not be restarted with the new port configuration. It is running as it was before.",
            new InvalidOperationException($"raw-daemon-detail unix:///var/run/docker.sock POSTGRES_PASSWORD={password}"));
        var admin = await SignedInAsync("admin");

        var response = await PostAsync(admin, instanceId, "Enable");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var html = await HtmlOf(response);
        Assert.Contains(
            "<div class=\"alert-title\">DOCKER_PORT_CONFIGURATION_FAILED</div> <div>The database server could not be restarted with the new port configuration. It is running as it was before.</div>",
            html,
            StringComparison.Ordinal);
        Assert.Contains("<h1>Enable external access?</h1>", html, StringComparison.Ordinal);
        foreach (var leaked in new[] { password, "raw-daemon-detail", "docker.sock", "POSTGRES_PASSWORD", "Exception" })
        {
            Assert.DoesNotContain(leaked, html, StringComparison.Ordinal);
        }

        // No success was announced, and the record says what it said.
        Assert.Equal((false, null), await RecordAsync(instanceId));
        var after = Flat(await admin.GetHtmlAsync($"/instances/{instanceId}"));
        Assert.DoesNotContain("External access enabled", after, StringComparison.Ordinal);
        Assert.Contains("Disabled</span>", Section(after, "connection-title"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PortThatTurnsOutToBeTaken_IsNotTheUsersProblem_TheNextOneIsUsed()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        _factory.Provisioner.HostPortsInUse.Add(15432);
        var admin = await SignedInAsync("admin");

        var response = await PostAsync(admin, instanceId, "Enable");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal((true, 15433), await RecordAsync(instanceId));
        Assert.Contains("External access enabled on host port 15433.", await admin.GetHtmlAsync($"/instances/{instanceId}"), StringComparison.Ordinal);
    }

    // --- Who may, and how ----------------------------------------------------------------------

    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    public async Task ViewerAndOperator_SeeTheConnection_ButCannotChangeExternalAccess_WhateverTheyAskFor(string role)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var admin = await SignedInAsync("admin");
        var browser = await SignedInAsync(role);

        var page = await browser.GetAsync($"/instances/{instanceId}/external-access");
        var enable = await PostAsync(browser, instanceId, "Enable", tokenFrom: "/instances");
        await PostAsync(admin, instanceId, "Enable");
        var disable = await PostAsync(browser, instanceId, "Disable", tokenFrom: "/instances");
        var details = Section(Flat(await browser.GetHtmlAsync($"/instances/{instanceId}")), "connection-title");

        foreach (var response in new[] { page, enable, disable })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("You don&#x27;t have permission", html, StringComparison.Ordinal);
            Assert.Contains("Request ID:", html, StringComparison.Ordinal);
        }

        // Only the administrator's own request did anything.
        Assert.Equal((true, 15432), await RecordAsync(instanceId));
        Assert.Single(_factory.Provisioner.AppliedExternalAccess);
        // They see where it is; they are not offered the change.
        Assert.Contains("<dt>Port</dt> <dd class=\"mono\">15432</dd>", details, StringComparison.Ordinal);
        Assert.DoesNotContain("external-access\"", details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingExternalAccess_NeedsTheAntiforgeryToken_ASignIn_AndANamedAction()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var admin = await SignedInAsync("admin");
        using var anonymous = _factory.CreateBrowser();
        var path = $"/instances/{instanceId}/external-access";

        var withoutToken = await admin.PostFormAsync($"{path}?handler=Enable", [], withToken: false);
        var forged = await admin.PostAsync($"{path}?handler=Enable", new FormUrlEncodedContent([new("__RequestVerificationToken", "CfDJ8forged")]));
        var unnamed = await admin.PostFormAsync(path, []);
        var unknown = await admin.PostFormAsync($"{path}?handler=Expose", [], tokenFrom: path);
        var notSignedIn = await anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Contains("Request ID:", await withoutToken.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, notSignedIn.StatusCode);
        Assert.StartsWith("/login?returnUrl=", notSignedIn.Headers.Location!.PathAndQuery, StringComparison.Ordinal);
        Assert.Equal((false, null), await RecordAsync(instanceId));
        Assert.Empty(_factory.Provisioner.AppliedExternalAccess);
    }

    [Fact]
    public void ExternalAccessPage_HasThePolicyOfTheApiEndpoints_AndInstancesThatAreNotThereAreNotFound()
    {
        var page = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<PageActionDescriptor>() is not null)
            .Single(endpoint => endpoint.RoutePattern.RawText!.TrimStart('/') == "instances/{id:guid}/external-access");

        Assert.Contains(page.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == "admin");
    }

    [Fact]
    public async Task InstanceThatIsNotThere_IsNotFound_OnThePageAndOnItsForm()
    {
        var admin = await SignedInAsync("admin");
        var nobody = Guid.CreateVersion7();

        var page = await admin.GetAsync($"/instances/{nobody}/external-access");
        var post = await PostAsync(admin, nobody, "Enable", tokenFrom: "/instances");

        foreach (var response in new[] { page, post })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("<h1>Instance not found</h1>", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    // --- What a page never contains -------------------------------------------------------------

    [Fact]
    public async Task NoPassword_InAnyPage_OrInTheLog_AndNothingTheContentSecurityPolicyWouldBlock()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "app");
        var password = await _factory.AdminPasswordAsync(instanceId);
        var admin = await SignedInAsync("admin");
        var pages = new List<(string Path, HttpResponseMessage Response)>();

        async Task VisitAsync()
        {
            foreach (var path in new[] { $"/instances/{instanceId}", $"/instances/{instanceId}/external-access", $"/instances/{instanceId}/databases/{databaseId}" })
            {
                pages.Add((path, await admin.GetAsync(path)));
            }
        }

        await VisitAsync();
        pages.Add(("enable", await PostAsync(admin, instanceId, "Enable")));
        await VisitAsync();
        pages.Add(("enable again", await PostAsync(admin, instanceId, "Enable")));

        foreach (var (path, response) in pages.Where(page => page.Response.StatusCode != HttpStatusCode.Redirect))
        {
            var html = await response.Content.ReadAsStringAsync();

            Assert.DoesNotContain(password, html, StringComparison.Ordinal);
            // The word appears as the placeholder and in the sentence about it, never as a value.
            Assert.DoesNotMatch("(?i)password\\s*[=:]\\s*[^&<\\s]", html.Replace("postgres:&lt;password&gt;", string.Empty, StringComparison.Ordinal));
            Assert.All(HiddenInput().Matches(html), field => Assert.Contains("__RequestVerificationToken", field.Value, StringComparison.Ordinal));

            Assert.Equal(WebProgram.PageContentSecurityPolicy, string.Join("", response.Headers.GetValues("Content-Security-Policy")));
            Assert.Contains("no-store", string.Join("", response.Headers.GetValues("Cache-Control")), StringComparison.Ordinal);
            Assert.Empty(InlineScript().Matches(html));
            Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(StyleAttribute().Matches(html));
            Assert.Empty(EventHandlerAttribute().Matches(html));
            Assert.All(Form().Matches(html), form => Assert.Contains("method=\"post\"", form.Value, StringComparison.Ordinal));
            Assert.True(path.Length > 0);
        }

        // A change of external access leaves the address of where it led, and nothing of a connection string, in no address.
        Assert.All(pages.Where(page => page.Response.StatusCode == HttpStatusCode.Redirect), page =>
            Assert.Equal($"/instances/{instanceId}", page.Response.Headers.Location!.OriginalString));
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains("<password>", StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains("postgresql://", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostileInstanceName_IsTextOnTheConfirmationPage()
    {
        const string hostile = "<script>alert(1)</script>\"'";
        var (instanceId, jobId) = await _api.CreateInstanceAsync(name: hostile);
        await _factory.ProcessJobAsync(jobId);
        var admin = await SignedInAsync("admin");

        var html = await admin.GetHtmlAsync($"/instances/{instanceId}/external-access");

        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains(System.Text.Encodings.Web.HtmlEncoder.Default.Encode(hostile), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyScript_CopiesOnlyTextThatIsOnThePage_AndTalksToNobody()
    {
        using var browser = _factory.CreateBrowser();

        var script = await (await browser.GetAsync("/js/aurora.js")).Content.ReadAsStringAsync();

        Assert.Contains("[data-copy]", script, StringComparison.Ordinal);
        Assert.Contains("navigator.clipboard.writeText(source.textContent.trim())", script, StringComparison.Ordinal);
        Assert.Contains("window.isSecureContext", script, StringComparison.Ordinal);
        // Writes to the clipboard, never reads from it.
        Assert.DoesNotContain("readText", script, StringComparison.Ordinal);
        Assert.DoesNotContain("clipboard.read", script, StringComparison.Ordinal);
    }

    [GeneratedRegex("<script(?![^>]*\\bsrc=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    [GeneratedRegex("<[^>]+\\sstyle\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttribute();

    [GeneratedRegex("<[^>]+\\son[a-z]+\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerAttribute();

    [GeneratedRegex("<form[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Form();

    [GeneratedRegex("<input[^>]+type=\"hidden\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInput();
}
