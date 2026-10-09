using System.Net;
using System.Text.RegularExpressions;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuroraDbManager.Api.Tests.Ui;

/// <summary>
/// The credential Aurora manages for an instance, as the pages show it, and the page that asks
/// for its password to be rotated: through the real Web host, real sign-ins and real antiforgery
/// tokens, with the rotation service and its job doing what the page asks. What every test here
/// also is, is a test that no page has a password.
/// </summary>
public sealed partial class UiCredentialTests : IDisposable
{
    private const string UserPassword = "a-perfectly-fine-password";

    private readonly WebFactory _factory = new() { BootstrapAdmin = (Browser.Admin, Browser.Password) };
    private readonly HttpClient _api;
    private readonly List<HttpClient> _browsers = [];

    public UiCredentialTests()
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
            await Browser.SignInAsync(browser);
        }
        else
        {
            await _api.CreateUserAsync($"the-{role}", UserPassword, role);
            await Browser.SignInAsync(browser, $"the-{role}", UserPassword);
        }

        return browser;
    }

    private static string Flat(string html) => Regex.Replace(html, "\\s+", " ");

    private static async Task<string> HtmlOf(HttpResponseMessage response) => Flat(await response.Content.ReadAsStringAsync());

    private static string Section(string html, string id) =>
        Regex.Match(html, $"<section class=\"section\" aria-labelledby=\"{id}\">.*?</section>").Value;

    private static string RotatePath(Guid instanceId) => $"/instances/{instanceId}/rotate-password";

    private Task<int> RotationJobsAsync() =>
        _factory.WithDbAsync(db => db.Jobs.CountAsync(job => job.Type == JobType.RotateCredential));

    private Task<Guid> RotationJobAsync() =>
        _factory.WithDbAsync(db => db.Jobs.Where(job => job.Type == JobType.RotateCredential).OrderByDescending(job => job.CreatedAt).Select(job => job.Id).FirstAsync());

    private async Task<bool> InSyncAsync(Guid instanceId) =>
        await _factory.AdminPasswordAsync(instanceId) == await _factory.AdminCredentials.PasswordAsync(instanceId);

    // --- What the instance's page says ----------------------------------------------------------

    [Fact]
    public async Task InstancePage_ShowsTheManagedCredential_ItsUsername_AndThatThePasswordIsNotDisplayed()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var viewer = await SignedInAsync("viewer");

        var credential = Section(Flat(await viewer.GetHtmlAsync($"/instances/{instanceId}")), "credential-title");

        Assert.Contains("<h2 id=\"credential-title\">Credential</h2>", credential, StringComparison.Ordinal);
        Assert.Matches("<dt>Credential</dt> <dd> <span class=\"badge badge-success\"><svg.*?</svg> Managed by Aurora</span>", credential);
        Assert.Contains("Aurora-managed database credential", credential, StringComparison.Ordinal);
        Assert.Contains("<dt>Username</dt> <dd class=\"mono\">postgres</dd>", credential, StringComparison.Ordinal);
        Assert.Contains("<dt>Password</dt> <dd>Not displayed", credential, StringComparison.Ordinal);
        Assert.Contains("stores it encrypted. It cannot be chosen, and it is shown only once, right after a rotation.", credential, StringComparison.Ordinal);
        Assert.Contains("<dt>Last rotated</dt> <dd> <span class=\"muted\">Never.", credential, StringComparison.Ordinal);
        Assert.DoesNotContain("Last rotation</dt>", credential, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstancePage_OffersRotation_ToOperatorsAndAdministrators_AndNotToViewers()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var link = $"<a class=\"button\" href=\"{RotatePath(instanceId)}\">Rotate password</a>";

        foreach (var role in new[] { "admin", "operator" })
        {
            Assert.Contains(link, Section(Flat(await (await SignedInAsync(role)).GetHtmlAsync($"/instances/{instanceId}")), "credential-title"), StringComparison.Ordinal);
        }

        var forViewer = Flat(await (await SignedInAsync("viewer")).GetHtmlAsync($"/instances/{instanceId}"));
        Assert.DoesNotContain("rotate-password", forViewer, StringComparison.Ordinal);
        Assert.DoesNotContain("Rotate password", forViewer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InstanceStatus.Stopped)]
    [InlineData(InstanceStatus.Failed)]
    public async Task InstancePage_OffersNoRotation_ForAnInstanceThatIsNotRunning(InstanceStatus status)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        await _factory.SetInstanceStatusAsync(instanceId, status);
        var admin = await SignedInAsync("admin");

        Assert.DoesNotContain("rotate-password", await admin.GetHtmlAsync($"/instances/{instanceId}"), StringComparison.Ordinal);
        // The page itself still answers, and says why it would not do it.
        var page = Flat(await admin.GetHtmlAsync(RotatePath(instanceId)));
        Assert.Contains("The password can only be rotated while the instance is running.", page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostFormAsync(RotatePath(instanceId), [])).StatusCode);
        Assert.Equal(0, await RotationJobsAsync());
    }

    [Fact]
    public async Task InstancePage_OfAnInstanceBeingProvisioned_SaysThereIsNoCredentialYet()
    {
        var (instanceId, _) = await _api.CreateInstanceAsync();
        var admin = await SignedInAsync("admin");

        var credential = Section(Flat(await admin.GetHtmlAsync($"/instances/{instanceId}")), "credential-title");

        Assert.Contains("There is no credential until the instance has been provisioned.", credential, StringComparison.Ordinal);
        Assert.DoesNotContain("rotate-password", credential, StringComparison.Ordinal);
    }

    // --- The page that asks ---------------------------------------------------------------------

    [Fact]
    public async Task RotatePage_SaysWhatWillHappen_AndHasAFormWithNothingToTypeInto()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var admin = await SignedInAsync("admin");

        var page = Flat(await admin.GetHtmlAsync(RotatePath(instanceId)));

        Assert.Contains("<h1>Rotate database password?</h1>", page, StringComparison.Ordinal);
        Assert.Contains("with a new one that Aurora generates", page, StringComparison.Ordinal);
        Assert.Contains("<strong>The current password stops working</strong>", page, StringComparison.Ordinal);
        Assert.Contains("<strong>The new password is shown once</strong>, on request, after the rotation has succeeded", page, StringComparison.Ordinal);
        Assert.Contains("Aurora stores it encrypted", page, StringComparison.Ordinal);
        Assert.Contains("can no longer connect", page, StringComparison.Ordinal);
        Assert.Contains("The database server is not restarted, and no data is touched.", page, StringComparison.Ordinal);
        Assert.Contains($"<a class=\"button\" href=\"/instances/{instanceId}\">Cancel</a>", page, StringComparison.Ordinal);
        Assert.Matches("<button type=\"submit\" class=\"button button-danger-solid\"[^>]*>Rotate password</button>", page);

        // One form, a POST, and the only field in it is the antiforgery token: no password can be
        // typed, pasted or carried along.
        var form = Assert.Single(Form().Matches(page), match => match.Value.Contains("class=\"form\"", StringComparison.Ordinal)).Value;
        Assert.Contains("method=\"post\"", form, StringComparison.Ordinal);
        var inputs = Input().Matches(form).Select(match => match.Value).ToList();
        Assert.Equal("__RequestVerificationToken", Regex.Match(Assert.Single(inputs), "name=\"([^\"]+)\"").Groups[1].Value);
        Assert.DoesNotMatch("type=\"password\"|<textarea|<select", page);
        Assert.DoesNotMatch("(?i)show password|reveal|copy password", page);
    }

    [Fact]
    public async Task RotatePage_Opened_ChangesNothing_HoweverItIsOpened()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var original = await _factory.AdminPasswordAsync(instanceId);
        var admin = await SignedInAsync("admin");

        foreach (var path in new[] { RotatePath(instanceId), $"{RotatePath(instanceId)}?handler=Post", $"{RotatePath(instanceId)}?confirm=true&password=chosen" })
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(path)).StatusCode);
        }

        Assert.Equal(0, await RotationJobsAsync());
        Assert.Null(await _factory.AdminPasswordReplacementAsync(instanceId));
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == original);
    }

    [Fact]
    public async Task RotatePage_Submitted_StartsTheJob_AndGoesToItsPage_WhichSaysHowItWent()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var original = await _factory.AdminPasswordAsync(instanceId);
        var @operator = await SignedInAsync("operator");

        var response = await @operator.PostFormAsync(RotatePath(instanceId), []);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var jobId = await RotationJobAsync();
        Assert.Equal($"/jobs/{jobId}", response.Headers.Location!.OriginalString);

        var pending = Flat(await @operator.GetHtmlAsync($"/jobs/{jobId}"));
        Assert.Contains("Password rotation started. The new password can be shown here once it has finished.", pending, StringComparison.Ordinal);
        Assert.Contains("<h1>Rotate password</h1>", pending, StringComparison.Ordinal);
        Assert.Contains("The password is being rotated", pending, StringComparison.Ordinal);
        Assert.Contains("data-refresh=\"5\"", pending, StringComparison.Ordinal);
        // Started is not done.
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == original);

        // While it runs the instance's page says so, follows along, and does not offer another.
        var during = Flat(await @operator.GetHtmlAsync($"/instances/{instanceId}"));
        var credential = Section(during, "credential-title");
        Assert.Matches($"<dt>Last rotation</dt> <dd> <span class=\"badge badge-progress\"><svg.*?</svg> Pending</span> <a href=\"/jobs/{jobId}\">Follow the rotation</a>", credential);
        Assert.DoesNotContain("rotate-password", during, StringComparison.Ordinal);
        Assert.Contains("data-refresh=\"5\"", during, StringComparison.Ordinal);

        await _factory.ProcessJobAsync(jobId);

        var done = Flat(await @operator.GetHtmlAsync($"/jobs/{jobId}"));
        Assert.Contains("<div class=\"alert-title\">The password was rotated</div>", done, StringComparison.Ordinal);
        Assert.Contains("Aurora has stored and uses. The previous password no longer works.", done, StringComparison.Ordinal);
        Assert.Contains("Completed", done, StringComparison.Ordinal);
        Assert.DoesNotContain("data-refresh", done, StringComparison.Ordinal);
        Assert.True(await InSyncAsync(instanceId));
        Assert.False(await _factory.AdminPasswordAsync(instanceId) == original, "The password was not replaced.");

        var afterwards = Section(Flat(await @operator.GetHtmlAsync($"/instances/{instanceId}")), "credential-title");
        Assert.Matches("<dt>Last rotated</dt> <dd> <time datetime=", afterwards);
        Assert.Matches($"<dt>Last rotation</dt> <dd> <span class=\"badge badge-success\"><svg.*?</svg> Completed</span> <a href=\"/jobs/{jobId}\">Open the job</a>", afterwards);
        Assert.Contains("rotate-password", afterwards, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotationThatFailed_IsShownAsFailed_WithItsCode_AndTheInstancePageSaysHowToFinishIt()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        _factory.SecretFaults.FailNextPromotions(int.MaxValue);
        var admin = await SignedInAsync("admin");
        var viewer = await SignedInAsync("viewer");

        await admin.PostFormAsync(RotatePath(instanceId), []);
        var jobId = await RotationJobAsync();
        await _factory.ProcessJobAsync(jobId);

        var failed = Flat(await admin.GetHtmlAsync($"/jobs/{jobId}"));
        Assert.Contains("<div class=\"alert-title\">The password was not rotated</div>", failed, StringComparison.Ordinal);
        Assert.Contains("<code>CREDENTIAL_ROTATION_SECRET_STORE_FAILED</code>", failed, StringComparison.Ordinal);
        Assert.Contains("it could not be stored as the password Aurora uses", failed, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-store-detail", failed, StringComparison.Ordinal);
        Assert.DoesNotContain("IOException", failed, StringComparison.Ordinal);

        var instancePage = Flat(await admin.GetHtmlAsync($"/instances/{instanceId}"));
        Assert.Contains("<div class=\"alert-title\">The last password rotation did not finish</div>", instancePage, StringComparison.Ordinal);
        Assert.Contains("Rotate the password again to finish it.", instancePage, StringComparison.Ordinal);
        Assert.Matches($"<span class=\"badge badge-danger\"><svg.*?</svg> Failed</span> <a href=\"/jobs/{jobId}\">Open the job</a>", instancePage);
        // A viewer is told, and told who can do something about it.
        var forViewer = Flat(await viewer.GetHtmlAsync($"/instances/{instanceId}"));
        Assert.Contains("An operator or an administrator finishes it by rotating the password again.", forViewer, StringComparison.Ordinal);
        Assert.DoesNotContain("rotate-password", forViewer, StringComparison.Ordinal);

        // The page that asks says it too, and asking again finishes the rotation.
        Assert.Contains("The last rotation did not finish", Flat(await admin.GetHtmlAsync(RotatePath(instanceId))), StringComparison.Ordinal);
        _factory.SecretFaults.FailNextPromotions(0);
        await admin.PostFormAsync(RotatePath(instanceId), []);
        await _factory.ProcessJobAsync(await RotationJobAsync());

        Assert.True(await InSyncAsync(instanceId));
        Assert.Equal(1, _factory.AdminCredentials.Changes);
        Assert.DoesNotContain("did not finish", Flat(await admin.GetHtmlAsync($"/instances/{instanceId}")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotatePage_SubmittedWhileOneIsUnfinished_IsRefused_OnThePage_With409()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var admin = await SignedInAsync("admin");
        await admin.PostFormAsync(RotatePath(instanceId), []);

        var again = await admin.PostFormAsync(RotatePath(instanceId), []);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var page = await HtmlOf(again);
        Assert.Contains("The password of this instance is already being rotated.", page, StringComparison.Ordinal);
        Assert.Contains("CREDENTIAL_ROTATION_IN_PROGRESS", page, StringComparison.Ordinal);
        Assert.Equal(1, await RotationJobsAsync());
    }

    [Fact]
    public async Task OtherPages_RefuseTheirWork_WhileThePasswordIsBeingRotated()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "orders");
        var admin = await SignedInAsync("admin");
        await admin.PostFormAsync(RotatePath(instanceId), []);

        foreach (var (path, fields) in new (string, KeyValuePair<string, string>[])[]
                 {
                     ($"/instances/{instanceId}/databases/create", [new("Input.Name", "another")]),
                     ($"/instances/{instanceId}/databases/{databaseId}/delete", []),
                     ($"/instances/{instanceId}/databases/{databaseId}/backups/create", []),
                     ($"/instances/{instanceId}/delete", [])
                 })
        {
            var refused = await admin.PostFormAsync(path, fields);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("CREDENTIAL_ROTATION_IN_PROGRESS", await HtmlOf(refused), StringComparison.Ordinal);
        }

        Assert.Equal(1, await _factory.WithDbAsync(db => db.Databases.CountAsync()));
        Assert.Equal("ready", (await _api.GetDatabaseAsync(databaseId)).Status());
    }

    // --- Who may, and how -----------------------------------------------------------------------

    [Fact]
    public async Task Viewer_CannotOpenOrSubmitTheRotatePage_AndASignedOutBrowserIsSentToSignIn()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var original = await _factory.AdminPasswordAsync(instanceId);
        var viewer = await SignedInAsync("viewer");
        var admin = await SignedInAsync("admin");
        var signedOut = _factory.CreateBrowser();
        _browsers.Add(signedOut);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(RotatePath(instanceId))).StatusCode);
        // With a real antiforgery token, from a page the viewer may open: the policy is what refuses.
        var asViewer = await viewer.PostFormAsync(RotatePath(instanceId), [], tokenFrom: $"/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.Forbidden, asViewer.StatusCode);

        var asNobody = await signedOut.GetAsync(RotatePath(instanceId));
        Assert.Equal(HttpStatusCode.Redirect, asNobody.StatusCode);
        Assert.Equal("/login", asNobody.Headers.Location!.AbsolutePath);

        // An administrator's POST without the token is not carried out either.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostFormAsync(RotatePath(instanceId), [], withToken: false)).StatusCode);

        Assert.Equal(0, await RotationJobsAsync());
        Assert.Null(await _factory.AdminPasswordReplacementAsync(instanceId));
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == original);
    }

    [Fact]
    public void RotatePage_HasThePolicyOfTheApiEndpointThatDoesTheSame()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        var page = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<PageActionDescriptor>() is not null
                && endpoint.RoutePattern.RawText!.TrimStart('/') == "instances/{id:guid}/rotate-password")
            .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Select(data => data.Policy).Distinct().Single();
        var api = endpoints
            .Where(endpoint => endpoint.RoutePattern.RawText!.TrimStart('/') == "api/v1/instances/{id:guid}/credentials/rotate")
            .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Select(data => data.Policy).Last();

        Assert.Equal("operator", page);
        Assert.Equal("operator", api);
    }

    [Fact]
    public async Task RotatePage_OfAnInstanceThatDoesNotExist_Is404_ForGetAndForPost()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var admin = await SignedInAsync("admin");
        var missing = RotatePath(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(missing)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostFormAsync(missing, [], tokenFrom: RotatePath(instanceId))).StatusCode);
    }

    // --- Nothing is ever shown ------------------------------------------------------------------

    [Fact]
    public async Task NoPassword_IsOnAnyPage_InAnyRedirect_AnyCookie_OrTheLog_BeforeDuringOrAfterARotation()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "orders");
        var admin = await SignedInAsync("admin");
        var original = await _factory.AdminPasswordAsync(instanceId);
        var seen = new List<string>();

        async Task LookAroundAsync(Guid? jobId)
        {
            string[] paths =
            [
                "/", "/instances", $"/instances/{instanceId}", RotatePath(instanceId), $"/instances/{instanceId}/external-access",
                $"/instances/{instanceId}/databases/{databaseId}", "/jobs", $"/jobs?type=rotate_credential&instanceId={instanceId}", "/monitoring"
            ];
            foreach (var path in jobId is null ? paths : [.. paths, $"/jobs/{jobId}"])
            {
                seen.Add(await admin.GetHtmlAsync(path));
            }
        }

        async Task SubmitAsync()
        {
            var response = await admin.PostFormAsync(RotatePath(instanceId), []);
            seen.Add(await response.Content.ReadAsStringAsync());
            seen.Add(response.Headers.Location?.OriginalString ?? string.Empty);
            seen.Add(string.Join("\n", response.SetCookies().Values));
        }

        await LookAroundAsync(null);

        // A rotation that fails with the server already changed, and the one that finishes it.
        _factory.SecretFaults.FailNextPromotions(int.MaxValue);
        await SubmitAsync();
        var waiting = (await _factory.AdminPasswordReplacementAsync(instanceId))!;
        var failedJobId = await RotationJobAsync();
        await LookAroundAsync(failedJobId);
        await _factory.ProcessJobAsync(failedJobId);
        await LookAroundAsync(failedJobId);

        _factory.SecretFaults.FailNextPromotions(0);
        await SubmitAsync();
        var finishedJobId = await RotationJobAsync();
        await _factory.ProcessJobAsync(finishedJobId);
        await LookAroundAsync(finishedJobId);
        Assert.True(await _factory.AdminPasswordAsync(instanceId) == waiting);

        Assert.True(seen.Count > 30);
        foreach (var secret in new[] { original, waiting })
        {
            Assert.False(seen.Any(text => text.Contains(secret, StringComparison.Ordinal)), "A page, a redirect or a cookie carries a password.");
            Assert.False(_factory.Logs.Entries.Any(entry => entry.Contains(secret, StringComparison.Ordinal)), "A log entry carries a password.");
        }

        foreach (var html in seen.Where(text => text.Contains("<html", StringComparison.Ordinal)))
        {
            // No field of any form could hold one, and nothing is tucked away in a comment.
            // (The jobs list keeps the instance or database it is narrowed to in a hidden field: an id.)
            Assert.All(HiddenInput().Matches(html), field => Assert.Matches("name=\"(__RequestVerificationToken|__Invariant|instanceId|databaseId)\"", field.Value));
            Assert.DoesNotMatch("type=\"password\"", html);
            Assert.DoesNotContain("<!--", html, StringComparison.Ordinal);
            Assert.DoesNotMatch("(?i)password\\s*[=:]\\s*[^&<\\s]", html.Replace(":&lt;password&gt;", string.Empty, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ConnectionDetails_AfterARotation_AreWhatTheyWere_WithAPlaceholderWhereThePasswordGoes()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "orders");
        var admin = await SignedInAsync("admin");
        var before = Section(Flat(await admin.GetHtmlAsync($"/instances/{instanceId}/databases/{databaseId}")), "connection-title");

        await admin.PostFormAsync(RotatePath(instanceId), []);
        await _factory.ProcessJobAsync(await RotationJobAsync());

        var after = Section(Flat(await admin.GetHtmlAsync($"/instances/{instanceId}/databases/{databaseId}")), "connection-title");
        Assert.NotEmpty(before);
        Assert.Equal(before, after);
        Assert.Contains("&lt;password&gt;", after, StringComparison.Ordinal);
        Assert.DoesNotContain(await _factory.AdminPasswordAsync(instanceId), after, StringComparison.Ordinal);
    }

    // --- The new password, once -------------------------------------------------------------------

    private static string ResultPath(Guid instanceId, Guid jobId) => $"/instances/{instanceId}/rotate-password/{jobId}/result";

    private async Task<Guid> RotatedAsync(Guid instanceId, HttpClient browser)
    {
        await browser.PostFormAsync(RotatePath(instanceId), []);
        var jobId = await RotationJobAsync();
        await _factory.ProcessJobAsync(jobId);
        return jobId;
    }

    [Fact]
    public async Task JobPage_OfACompletedRotation_OffersToShowTheNewPassword_ToThoseWhoMay_AndDoesNotHaveIt()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var @operator = await SignedInAsync("operator");
        var viewer = await SignedInAsync("viewer");
        var jobId = await RotatedAsync(instanceId, @operator);
        var password = await _factory.AdminPasswordAsync(instanceId);

        var page = Flat(await @operator.GetHtmlAsync($"/jobs/{jobId}"));
        var forViewer = Flat(await viewer.GetHtmlAsync($"/jobs/{jobId}"));

        var result = Section(page, "credential-result-title");
        Assert.Contains("<h2 id=\"credential-result-title\">New password</h2>", result, StringComparison.Ordinal);
        Assert.Contains("The new password can be shown <strong>once</strong>.", result, StringComparison.Ordinal);
        Assert.Contains($"<form method=\"post\" action=\"{ResultPath(instanceId, jobId)}\" class=\"form\">", result, StringComparison.Ordinal);
        Assert.Matches("<button type=\"submit\" class=\"button button-primary\"[^>]*>Show the new password</button>", result);
        Assert.Single(Input().Matches(result), input => input.Value.Contains("__RequestVerificationToken", StringComparison.Ordinal));
        Assert.Single(Input().Matches(result));

        var viewerResult = Section(forViewer, "credential-result-title");
        Assert.Contains("The new password is available, once, to an operator or an administrator.", viewerResult, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", viewerResult, StringComparison.Ordinal);
        Assert.DoesNotContain("Show the new password", forViewer, StringComparison.Ordinal);

        // Looking at the job, by anyone, shows no password and uses nothing up.
        Assert.False(page.Contains(password, StringComparison.Ordinal) || forViewer.Contains(password, StringComparison.Ordinal), "The job page has the password.");
        Assert.Contains("It can be shown once, from the job.", Flat(await @operator.GetHtmlAsync($"/instances/{instanceId}")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("postgres", "postgres")]
    [InlineData("mysql", "root")]
    public async Task ShowTheNewPassword_ShowsUsernameAndPassword_Once_AndAfterwardsSaysItWasRetrieved(string engine, string username)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api, engine: engine);
        var @operator = await SignedInAsync("operator");
        var jobId = await RotatedAsync(instanceId, @operator);
        var password = await _factory.AdminPasswordAsync(instanceId);

        var shown = await @operator.PostFormAsync(ResultPath(instanceId, jobId), [], tokenFrom: $"/jobs/{jobId}");

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.True(shown.Headers.CacheControl!.NoStore, "The page with the password may be stored.");
        Assert.Null(shown.Headers.Location);
        var page = await HtmlOf(shown);
        Assert.Contains("<h1>Password rotated successfully</h1>", page, StringComparison.Ordinal);
        Assert.Contains($"<dt>Username</dt> <dd class=\"mono\">{username}</dd>", page, StringComparison.Ordinal);
        Assert.True(page.Contains($"<code class=\"snippet\" id=\"new-password\">{password}</code>", StringComparison.Ordinal), "The page does not show the new password.");
        Assert.Contains("<div class=\"alert-title\">This password will only be shown once</div>", page, StringComparison.Ordinal);
        Assert.Contains("Store it securely before leaving this page.", page, StringComparison.Ordinal);
        // Copying is a button the user presses: hidden until the script finds copying possible, and nothing copies by itself.
        Assert.Contains("<button type=\"button\" class=\"button\" data-copy=\"#new-password\" hidden>Copy password</button>", page, StringComparison.Ordinal);
        // It is in the page's text, once, and in no field, attribute, script or comment.
        Assert.True(Regex.Matches(page, Regex.Escape(password)).Count == 1, "The password is on the page more than once.");
        Assert.DoesNotMatch("type=\"password\"", page);
        Assert.DoesNotContain("<!--", page, StringComparison.Ordinal);
        Assert.False(Regex.IsMatch(page, $"(value|content|href|action|data-[a-z-]+)=\"[^\"]*{Regex.Escape(password)}"), "The password is in an attribute.");
        Assert.False(Regex.Matches(page, "<script\\b[^>]*>(.*?)</script>").Any(script => script.Groups[1].Value.Contains(password, StringComparison.Ordinal)), "The password is in a script.");
        Assert.False(string.Join("\n", shown.SetCookies().Values).Contains(password, StringComparison.Ordinal), "The password is in a cookie.");

        // Sending the form again, which is what reloading the page does: refused, and no password.
        var again = await @operator.PostFormAsync(ResultPath(instanceId, jobId), [], tokenFrom: $"/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var refused = await HtmlOf(again);
        Assert.Contains("Credential already retrieved. It cannot be displayed again.", refused, StringComparison.Ordinal);
        Assert.Contains("CREDENTIAL_RESULT_ALREADY_RETRIEVED", refused, StringComparison.Ordinal);
        Assert.False(refused.Contains(password, StringComparison.Ordinal), "The refused request shows the password.");

        // Going to the address shows nothing and leads back to the job, which says so too and offers nothing.
        var opened = await @operator.GetAsync(ResultPath(instanceId, jobId));
        Assert.Equal(HttpStatusCode.Redirect, opened.StatusCode);
        Assert.Equal($"/jobs/{jobId}", opened.Headers.Location!.OriginalString);
        var job = Flat(await @operator.GetHtmlAsync($"/jobs/{jobId}"));
        Assert.Contains("Credential already retrieved. It cannot be displayed again.", Section(job, "credential-result-title"), StringComparison.Ordinal);
        Assert.DoesNotContain("Show the new password", job, StringComparison.Ordinal);

        // And no page has it afterwards.
        foreach (var path in new[] { $"/jobs/{jobId}", $"/instances/{instanceId}", RotatePath(instanceId), "/jobs", "/", "/monitoring" })
        {
            Assert.False((await @operator.GetHtmlAsync(path)).Contains(password, StringComparison.Ordinal), "A page still has the password.");
        }

        Assert.False(_factory.Logs.Entries.Any(entry => entry.Contains(password, StringComparison.Ordinal)), "A log entry carries the password.");
    }

    [Fact]
    public async Task ShowTheNewPassword_OfARotationAnOperatorAskedFor_WorksForAnAdministrator()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var jobId = await RotatedAsync(instanceId, await SignedInAsync("operator"));
        var admin = await SignedInAsync("admin");

        var shown = await admin.PostFormAsync(ResultPath(instanceId, jobId), [], tokenFrom: $"/jobs/{jobId}");

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.True((await HtmlOf(shown)).Contains(await _factory.AdminPasswordAsync(instanceId), StringComparison.Ordinal), "The page does not show the new password.");
    }

    [Fact]
    public async Task ShowTheNewPassword_IsRefusedToAViewer_AndWithoutTheToken_AndNeitherUsesItUp()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var admin = await SignedInAsync("admin");
        var viewer = await SignedInAsync("viewer");
        var jobId = await RotatedAsync(instanceId, admin);
        var password = await _factory.AdminPasswordAsync(instanceId);

        var asViewer = await viewer.PostFormAsync(ResultPath(instanceId, jobId), [], tokenFrom: $"/instances/{instanceId}");
        var withoutToken = await admin.PostFormAsync(ResultPath(instanceId, jobId), [], withToken: false);
        var openedByViewer = await viewer.GetAsync(ResultPath(instanceId, jobId));

        Assert.Equal(HttpStatusCode.Forbidden, asViewer.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, openedByViewer.StatusCode);
        foreach (var response in new[] { asViewer, withoutToken, openedByViewer })
        {
            Assert.False((await response.Content.ReadAsStringAsync()).Contains(password, StringComparison.Ordinal), "A refused request shows the password.");
        }

        // Still there for someone who may ask properly.
        var shown = await admin.PostFormAsync(ResultPath(instanceId, jobId), [], tokenFrom: $"/jobs/{jobId}");
        Assert.True((await HtmlOf(shown)).Contains(password, StringComparison.Ordinal), "The page does not show the new password.");
    }

    [Fact]
    public async Task ShowTheNewPassword_ForARotationThatFailed_OrAnotherInstancesJob_ShowsNothing()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var otherId = await _factory.CreateRunningInstanceAsync(_api, name: "other-db");
        var admin = await SignedInAsync("admin");
        var otherJobId = await RotatedAsync(otherId, admin);
        _factory.AdminCredentials.FailNextChanges(int.MaxValue);
        var failedJobId = await RotatedAsync(instanceId, admin);

        var failed = Flat(await admin.GetHtmlAsync($"/jobs/{failedJobId}"));
        Assert.DoesNotContain("credential-result-title", failed, StringComparison.Ordinal);
        Assert.DoesNotContain("Show the new password", failed, StringComparison.Ordinal);

        var forced = await admin.PostFormAsync(ResultPath(instanceId, failedJobId), [], tokenFrom: $"/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.Conflict, forced.StatusCode);
        Assert.Contains("CREDENTIAL_RESULT_NOT_AVAILABLE", await HtmlOf(forced), StringComparison.Ordinal);
        // Another instance's job under this instance's address is no job of it.
        var crossed = await admin.PostFormAsync(ResultPath(instanceId, otherJobId), [], tokenFrom: $"/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);

        foreach (var response in new[] { forced, crossed })
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.False(
                text.Contains(await _factory.AdminPasswordAsync(instanceId), StringComparison.Ordinal) || text.Contains(await _factory.AdminPasswordAsync(otherId), StringComparison.Ordinal),
                "A refused request shows a password.");
        }
    }

    [Fact]
    public void ResultPage_HasThePolicyOfTheApiEndpointThatDoesTheSame()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        string Policy(string route) => endpoints
            .Where(endpoint => endpoint.RoutePattern.RawText!.TrimStart('/') == route)
            .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Select(data => data.Policy).Last()!;

        Assert.Equal("operator", Policy("instances/{id:guid}/rotate-password/{jobId:guid}/result"));
        Assert.Equal("operator", Policy("api/v1/instances/{id:guid}/credentials/rotate/{jobId:guid}/result"));
    }

    [GeneratedRegex("<form\\b.*?</form>")]
    private static partial Regex Form();

    [GeneratedRegex("<input\\b[^>]*>")]
    private static partial Regex Input();

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*>")]
    private static partial Regex HiddenInput();
}
