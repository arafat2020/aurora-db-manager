using System.Net;
using System.Text.RegularExpressions;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Infrastructure.Docker;
using AuroraDbManager.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuroraDbManager.Api.Tests.Ui;

/// <summary>
/// The pages that manage instances and their databases, through the real Web host: users sign
/// in through the form, forms are submitted with the antiforgery token of the page they are on,
/// and what a page asked for is carried out by the real application services and the real job
/// system. Jobs are run by the test, one at a time, so every state between "asked for" and
/// "done" can be looked at; nothing is made synchronous to make it testable.
/// </summary>
public sealed partial class UiInstanceAndDatabaseTests : IDisposable
{
    private const string UserPassword = "a-perfectly-fine-password";

    private readonly WebFactory _factory = new() { BootstrapAdmin = (Browser.Admin, Browser.Password) };
    private readonly HttpClient _api;
    private readonly List<HttpClient> _browsers = [];

    public UiInstanceAndDatabaseTests()
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

    private Task<HttpClient> SignedInAsync(string role) => SignedInAsync(_factory, _api, role);

    private async Task<HttpClient> SignedInAsync(WebFactory factory, HttpClient api, string role)
    {
        var browser = factory.CreateBrowser();
        _browsers.Add(browser);
        if (role == "admin")
        {
            await browser.SignInAsync();
        }
        else
        {
            await api.CreateUserAsync($"the-{role}", UserPassword, role);
            await browser.SignInAsync($"the-{role}", UserPassword);
        }

        return browser;
    }

    private static string Flat(string html) => Regex.Replace(html, "\\s+", " ");

    /// <summary>Text as Razor writes it into a page.</summary>
    private static string Encoded(string text) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(text);

    private static async Task<string> HtmlOf(HttpResponseMessage response) => Flat(await response.Content.ReadAsStringAsync());

    private static KeyValuePair<string, string>[] InstanceForm(
        string name = "orders", string engineVersion = "postgres:16", string cpu = "2", string memoryMb = "2048", string storageGb = "50") =>
    [
        new("Input.Name", name), new("Input.EngineVersion", engineVersion), new("Input.Cpu", cpu),
        new("Input.MemoryMb", memoryMb), new("Input.StorageGb", storageGb)
    ];

    private static KeyValuePair<string, string>[] DatabaseForm(string name) => [new("Input.Name", name)];

    private Task<int> InstanceCountAsync() => _factory.WithDbAsync(db => db.Instances.CountAsync());

    private Task<int> DatabaseCountAsync() => _factory.WithDbAsync(db => db.Databases.CountAsync());

    private Task<Guid> PendingJobOfAsync(Guid instanceId) => _factory.WithDbAsync(db => db.Jobs
        .Where(job => job.InstanceId == instanceId && job.CompletedAt == null)
        .Select(job => job.Id)
        .SingleAsync());

    /// <summary>Follows the redirect a successful form leads to, and returns where it led and what is there.</summary>
    private static async Task<(string Path, string Html)> FollowAsync(HttpClient browser, HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var path = response.Headers.Location!.OriginalString;
        return (path, Flat(await browser.GetHtmlAsync(path)));
    }

    // === Instances ===============================================================================

    [Theory]
    [InlineData("/instances/create")]
    [InlineData("/instances/0199c5a0-0000-7000-8000-000000000001")]
    [InlineData("/instances/0199c5a0-0000-7000-8000-000000000001/delete")]
    [InlineData("/instances/0199c5a0-0000-7000-8000-000000000001/databases")]
    [InlineData("/instances/0199c5a0-0000-7000-8000-000000000001/databases/create")]
    [InlineData("/instances/0199c5a0-0000-7000-8000-000000000001/databases/0199c5a0-0000-7000-8000-000000000002")]
    [InlineData("/instances/0199c5a0-0000-7000-8000-000000000001/databases/0199c5a0-0000-7000-8000-000000000002/delete")]
    public async Task Anonymous_IsRedirectedToTheLoginPage_FromEveryPage_AndFromEveryForm(string path)
    {
        using var browser = _factory.CreateBrowser();

        var get = await browser.GetAsync(path);
        var post = await browser.PostAsync(path, new FormUrlEncodedContent(InstanceForm()));

        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
        Assert.Equal($"/login?returnUrl={Uri.EscapeDataString(path)}", get.Headers.Location!.PathAndQuery);
        // Not signed in and no antiforgery token: refused either way, and nothing is done.
        Assert.Contains(post.StatusCode, new[] { HttpStatusCode.Redirect, HttpStatusCode.BadRequest });
        Assert.Equal(0, await InstanceCountAsync());
    }

    [Fact]
    public async Task InstanceList_Empty_OffersCreationToAnAdministrator_AndOnlyExplainsToAViewer()
    {
        var admin = await SignedInAsync("admin");
        var viewer = await SignedInAsync("viewer");

        var adminHtml = Flat(await admin.GetHtmlAsync("/instances"));
        var viewerHtml = Flat(await viewer.GetHtmlAsync("/instances"));

        Assert.Contains("<h2>No database instances yet</h2>", adminHtml, StringComparison.Ordinal);
        Assert.Contains("Create your first PostgreSQL or MySQL instance.", adminHtml, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(adminHtml, "href=\"/instances/create\"").Count);
        Assert.DoesNotContain("<table", adminHtml, StringComparison.Ordinal);

        Assert.Contains("<h2>No database instances yet</h2>", viewerHtml, StringComparison.Ordinal);
        Assert.Contains("An administrator creates them.", viewerHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("/instances/create", viewerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Viewer_ListsInstances_AndOpensOne_ButIsOfferedNoChange()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api, name: "production-db");
        var viewer = await SignedInAsync("viewer");

        var list = Flat(await viewer.GetHtmlAsync("/instances"));
        var details = Flat(await viewer.GetHtmlAsync($"/instances/{instanceId}"));

        Assert.Contains($"<a href=\"/instances/{instanceId}\">production-db</a>", list, StringComparison.Ordinal);
        Assert.Contains("1 core · 1 GB · 20 GB disk", list, StringComparison.Ordinal);
        Assert.Contains($"href=\"/instances/{instanceId}/databases\"", list, StringComparison.Ordinal);
        Assert.DoesNotContain("/delete", list, StringComparison.Ordinal);
        Assert.DoesNotContain("/create", list, StringComparison.Ordinal);

        Assert.Contains("<h1>production-db</h1>", details, StringComparison.Ordinal);
        Assert.Contains("<p class=\"page-description\">PostgreSQL 16</p>", details, StringComparison.Ordinal);
        Assert.Contains("<nav class=\"breadcrumbs\" aria-label=\"Breadcrumb\">", details, StringComparison.Ordinal);
        Assert.Contains("<a href=\"/instances\">Instances</a>", details, StringComparison.Ordinal);
        Assert.Contains("<span aria-current=\"location\">production-db</span>", details, StringComparison.Ordinal);
        // The status in words, and the resources as a person reads them.
        Assert.Matches("<h1>production-db</h1> <span class=\"badge badge-success\"><svg.*?</svg> Running</span>", details);
        Assert.Contains("<dt>CPU</dt> <dd>1 core</dd>", details, StringComparison.Ordinal);
        Assert.Contains("<dt>Memory</dt> <dd>1 GB</dd>", details, StringComparison.Ordinal);
        Assert.Contains("<dt>Storage</dt> <dd>20 GB</dd>", details, StringComparison.Ordinal);
        Assert.Contains($"<dd class=\"mono\">{instanceId}</dd>", details, StringComparison.Ordinal);
        Assert.Contains("<td class=\"cell-primary\">Provision instance</td>", details, StringComparison.Ordinal);
        Assert.Contains($"href=\"/jobs?instanceId={instanceId}\"", details, StringComparison.Ordinal);
        // Nothing a viewer could not do is offered.
        Assert.DoesNotContain("Delete", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Create database", details, StringComparison.Ordinal);
        Assert.Contains("An operator or an administrator creates them.", details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    public async Task ViewerAndOperator_CannotCreateOrDeleteAnInstance_WhateverTheyAskFor(string role)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var browser = await SignedInAsync(role);

        var createPage = await browser.GetAsync("/instances/create");
        var deletePage = await browser.GetAsync($"/instances/{instanceId}/delete");
        // With a genuine antiforgery token of their own session: it is the policy that refuses.
        var create = await browser.PostFormAsync("/instances/create", InstanceForm(), tokenFrom: "/instances");
        var delete = await browser.PostFormAsync($"/instances/{instanceId}/delete", [], tokenFrom: "/instances");

        foreach (var response in new[] { createPage, deletePage, create, delete })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("You don&#x27;t have permission", html, StringComparison.Ordinal);
            Assert.Contains("Request ID:", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<form method=\"post\" class=\"form\"", html, StringComparison.Ordinal);
        }

        Assert.Equal(1, await InstanceCountAsync());
        Assert.Empty(_factory.Provisioner.DeprovisionedInstanceIds);
    }

    [Fact]
    public async Task Admin_CreatesAnInstance_WhichIsProvisioning_UntilItsJobHasRun()
    {
        var admin = await SignedInAsync("admin");
        var form = Flat(await admin.GetHtmlAsync("/instances/create"));

        var response = await admin.PostFormAsync("/instances/create", InstanceForm(name: "orders", engineVersion: "mysql:8.4", cpu: "4", memoryMb: "8192", storageGb: "100"));
        var (path, html) = await FollowAsync(admin, response);

        // The form offers what the image catalog has, and nothing of its own.
        foreach (var engine in Enum.GetValues<InstanceEngine>())
        {
            foreach (var version in DockerImageResolver.SupportedVersions(engine))
            {
                Assert.Contains($"<option value=\"{engine.ToString().ToLowerInvariant()}:{version}\">", form, StringComparison.Ordinal);
            }
        }

        Assert.Equal(1 + Enum.GetValues<InstanceEngine>().Sum(engine => DockerImageResolver.SupportedVersions(engine).Count), Regex.Matches(form, "<option ").Count);
        Assert.Contains("<label class=\"field-label\" for=\"Input_Name\">", form, StringComparison.Ordinal);
        Assert.Contains("<label class=\"field-label\" for=\"Input_EngineVersion\">", form, StringComparison.Ordinal);
        Assert.Contains("<label class=\"field-label\" for=\"Input_Cpu\">", form, StringComparison.Ordinal);
        Assert.Contains("data-busy-label=\"Creating…\"", form, StringComparison.Ordinal);
        Assert.Contains("<a class=\"button\" href=\"/instances\">Cancel</a>", form, StringComparison.Ordinal);

        // Accepted, and said to be exactly that: started, not finished.
        var instance = await _factory.WithDbAsync(db => db.Instances.AsNoTracking().SingleAsync());
        Assert.Equal($"/instances/{instance.Id}", path);
        Assert.Equal(("orders", InstanceEngine.Mysql, "8.4", 4, 8192, 100), (instance.Name, instance.Engine, instance.Version, instance.Cpu, instance.MemoryMb, instance.StorageGb));
        Assert.Equal(InstanceStatus.Provisioning, instance.Status);
        Assert.Matches("<div class=\"alert alert-progress\" role=\"status\">.*?Instance creation started\\. The instance is being provisioned\\.", html);
        Assert.Matches("<h1>orders</h1> <span class=\"badge badge-progress\"><svg.*?</svg> Provisioning</span>", html);
        Assert.Contains("<p class=\"page-description\">MySQL 8.4</p>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Memory</dt> <dd>8 GB</dd>", html, StringComparison.Ordinal);
        Assert.Contains("data-refresh=\"5\"", html, StringComparison.Ordinal);
        Assert.Matches("</svg> Not checked</span>", html);
        Assert.DoesNotContain("Unhealthy", html, StringComparison.Ordinal);
        Assert.Matches("Provision instance</td> <td> <span class=\"badge badge-progress\"><svg.*?</svg> Pending</span>", html);
        // What cannot be done to an instance that is being provisioned is not offered.
        Assert.DoesNotContain("Delete instance", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Create database", html, StringComparison.Ordinal);
        Assert.Contains("Databases can be created once the instance is running.", html, StringComparison.Ordinal);
        Assert.Equal(0, _factory.Provisioner.CallCount);

        // The message is said once.
        var again = Flat(await admin.GetHtmlAsync(path));
        Assert.DoesNotContain("Instance creation started", again, StringComparison.Ordinal);

        // The job runs, and only then is the instance running.
        await _factory.ProcessJobAsync(await PendingJobOfAsync(instance.Id));
        var running = Flat(await admin.GetHtmlAsync(path));
        Assert.Matches("<h1>orders</h1> <span class=\"badge badge-success\"><svg.*?</svg> Running</span>", running);
        Assert.DoesNotContain("data-refresh", running, StringComparison.Ordinal);
        Assert.Contains($"href=\"/instances/{instance.Id}/delete\"", running, StringComparison.Ordinal);
        Assert.Contains($"href=\"/instances/{instance.Id}/databases/create\"", running, StringComparison.Ordinal);
        Assert.Matches("Provision instance</td> <td> <span class=\"badge badge-success\"><svg.*?</svg> Completed</span>", running);
    }

    [Fact]
    public async Task InstanceThatFailedToProvision_ShowsItsStatus_ItsErrorCode_AndItsMessage()
    {
        var (instanceId, jobId) = await _api.CreateInstanceAsync(name: "broken");
        _factory.Provisioner.FailAllCalls();
        await _factory.ProcessJobAsync(jobId);
        var viewer = await SignedInAsync("viewer");

        var html = Flat(await viewer.GetHtmlAsync($"/instances/{instanceId}"));

        Assert.Matches("<h1>broken</h1> <span class=\"badge badge-danger\"><svg.*?</svg> Failed</span>", html);
        Assert.Matches("<div class=\"alert alert-danger\" role=\"alert\">.*?<div class=\"alert-title\">PROVISIONING_FAILED</div> <div>Simulated provisioning failure\\.</div>", html);
        Assert.Contains("<code>PROVISIONING_FAILED</code>", html, StringComparison.Ordinal);
        Assert.Contains("Databases can only be created in a running instance.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-refresh", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "postgres:16", "2", "2048", "50", "name is required.")]
    [InlineData("orders", "", "2", "2048", "50", "Choose one of the listed engines and versions.")]
    [InlineData("orders", "postgres:9.6", "2", "2048", "50", "Choose one of the listed engines and versions.")]
    [InlineData("orders", "oracle:19", "2", "2048", "50", "Choose one of the listed engines and versions.")]
    [InlineData("orders", "postgres:16", "0", "2048", "50", "cpu must be between 1 and 256.")]
    [InlineData("orders", "postgres:16", "257", "2048", "50", "cpu must be between 1 and 256.")]
    [InlineData("orders", "postgres:16", "2", "1048577", "50", "memoryMb must be between 1 and 1048576.")]
    [InlineData("orders", "postgres:16", "2", "2048", "65537", "storageGb must be between 1 and 65536.")]
    [InlineData("orders", "postgres:16", "", "2048", "50", "cpu is required.")]
    [InlineData("orders", "postgres:16", "two", "2048", "50", "is not valid")]
    [InlineData("line\nbreak", "postgres:16", "2", "2048", "50", "name must not contain control characters.")]
    public async Task InstanceForm_IsValidatedByTheRequestsOwnRules_KeepsWhatWasTyped_AndCreatesNothing(
        string name, string engineVersion, string cpu, string memoryMb, string storageGb, string message)
    {
        var admin = await SignedInAsync("admin");

        var response = await admin.PostFormAsync("/instances/create", InstanceForm(name, engineVersion, cpu, memoryMb, storageGb));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var html = await HtmlOf(response);
        Assert.Contains("<h1>Create instance</h1>", html, StringComparison.Ordinal);
        Assert.Contains(message, html, StringComparison.Ordinal);
        Assert.Contains("field-validation-error", html, StringComparison.Ordinal);
        Assert.Contains("value=\"2048\"", html.Replace("value=\"1048577\"", "value=\"2048\"", StringComparison.Ordinal), StringComparison.Ordinal);
        if (engineVersion == "postgres:16")
        {
            Assert.Contains("<option value=\"postgres:16\" selected=\"selected\">", html, StringComparison.Ordinal);
        }

        Assert.Equal(0, await InstanceCountAsync());
    }

    [Fact]
    public async Task InstanceNameTooLong_IsRefusedByTheSameLimitAsTheApi()
    {
        var admin = await SignedInAsync("admin");

        var response = await admin.PostFormAsync("/instances/create", InstanceForm(name: new string('n', Instance.NameMaxLength + 1)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("name must be at most 100 characters.", await HtmlOf(response), StringComparison.Ordinal);
        Assert.Equal(0, await InstanceCountAsync());
    }

    [Fact]
    public async Task Admin_DeletesAnInstance_OnlyByPostingTheConfirmation()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api, name: "old-reports");
        await _factory.CreateReadyDatabaseAsync(_api, instanceId, "reports");
        await _factory.CreateReadyDatabaseAsync(_api, instanceId, "archive");
        var admin = await SignedInAsync("admin");

        // Asking is a GET, and changes nothing, however often.
        var question = Flat(await admin.GetHtmlAsync($"/instances/{instanceId}/delete"));
        await admin.GetHtmlAsync($"/instances/{instanceId}/delete");
        Assert.Equal(1, await InstanceCountAsync());
        Assert.Empty(_factory.Provisioner.DeprovisionedInstanceIds);

        Assert.Contains("<h1>Delete instance?</h1>", question, StringComparison.Ordinal);
        Assert.Contains("This permanently removes the instance <strong>old-reports</strong>, its database server and all the data in it.", question, StringComparison.Ordinal);
        Assert.Contains("<strong>2 databases</strong> in this instance are deleted with it.", question, StringComparison.Ordinal);
        Assert.Contains("This cannot be undone.", question, StringComparison.Ordinal);
        Assert.Contains($"<a class=\"button\" href=\"/instances/{instanceId}\">Cancel</a>", question, StringComparison.Ordinal);
        Assert.Contains("<button type=\"submit\" class=\"button button-danger-solid\" data-busy-label=\"Deleting…\">Delete instance</button>", question, StringComparison.Ordinal);
        // Nothing is ticked, typed or chosen for the user.
        Assert.DoesNotContain("checked", question, StringComparison.Ordinal);

        var response = await admin.PostFormAsync($"/instances/{instanceId}/delete", []);
        var (path, html) = await FollowAsync(admin, response);

        Assert.Equal("/instances", path);
        Assert.Matches("<div class=\"alert alert-success\" role=\"status\">.*?Instance &#x201C;old-reports&#x201D; was deleted\\.", html);
        Assert.Contains("No database instances yet", html, StringComparison.Ordinal);
        Assert.Equal(0, await InstanceCountAsync());
        Assert.Equal(0, await DatabaseCountAsync());
        Assert.Equal([instanceId], _factory.Provisioner.DeprovisionedInstanceIds);

        // Gone, and said to be: the page of what was deleted, and a second delete of it.
        var gone = await admin.GetAsync($"/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Contains("<h1>Instance not found</h1>", await gone.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostFormAsync($"/instances/{instanceId}/delete", [], tokenFrom: "/instances")).StatusCode);
    }

    [Fact]
    public async Task DeletingAnInstance_ThatTheApplicationWillNotDelete_ShowsWhy_WithTheStableCode_AndDeletesNothing()
    {
        var (provisioning, _) = await _api.CreateInstanceAsync(name: "still-provisioning");
        var busy = await _factory.CreateRunningInstanceAsync(_api, name: "busy");
        await _api.CreateDatabaseAsync(busy, "being_created");
        var backedUp = await _factory.CreateRunningInstanceAsync(_api, name: "backed-up");
        await _api.CreateBackupAsync(await _factory.CreateReadyDatabaseAsync(_api, backedUp, "app"));
        var admin = await SignedInAsync("admin");

        foreach (var (instanceId, code, message) in new[]
                 {
                     (provisioning, "INSTANCE_PROVISIONING", "Instance cannot be deleted while provisioning is in progress."),
                     (busy, "DATABASE_OPERATION_IN_PROGRESS", "Instance cannot be deleted while one of its databases is being created or deleted."),
                     (backedUp, "BACKUP_OPERATION_IN_PROGRESS", "Instance cannot be deleted while one of its databases is being backed up.")
                 })
        {
            var response = await admin.PostFormAsync($"/instances/{instanceId}/delete", []);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var html = await HtmlOf(response);
            Assert.Matches($"<div class=\"alert alert-warning\" role=\"alert\">.*?<div class=\"alert-title\">{code}</div> <div>{Regex.Escape(message)}</div>", html);
            // Still the question, so it can be asked again once the instance is free.
            Assert.Contains("<h1>Delete instance?</h1>", html, StringComparison.Ordinal);
        }

        Assert.Equal(3, await InstanceCountAsync());
        Assert.Empty(_factory.Provisioner.DeprovisionedInstanceIds);
    }

    [Fact]
    public async Task DeletingAnInstance_WhenItsServerCannotBeRemoved_SaysSo_AndKeepsTheInstance()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        _factory.Provisioner.DeprovisionFailure = new Application.Instances.InstanceProvisioningException(
            "DOCKER_UNAVAILABLE", "Docker is not available.", new InvalidOperationException("raw-daemon-detail unix:///var/run/docker.sock"));
        var admin = await SignedInAsync("admin");

        var response = await admin.PostFormAsync($"/instances/{instanceId}/delete", []);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var html = await HtmlOf(response);
        Assert.Contains("<div class=\"alert-title\">DOCKER_UNAVAILABLE</div> <div>Docker is not available.</div>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-daemon-detail", html, StringComparison.Ordinal);
        Assert.DoesNotContain("docker.sock", html, StringComparison.Ordinal);
        Assert.Equal(1, await InstanceCountAsync());
    }

    [Fact]
    public async Task InstanceList_IsPaged_ByTheServicesOwnPages()
    {
        for (var i = 0; i < 23; i++)
        {
            await _api.CreateInstanceAsync(name: $"instance-{i:00}");
        }

        var viewer = await SignedInAsync("viewer");

        var first = Flat(await viewer.GetHtmlAsync("/instances"));
        var second = Flat(await viewer.GetHtmlAsync("/instances?page=2"));
        var beyond = Flat(await viewer.GetHtmlAsync("/instances?page=9"));
        var nonsense = await viewer.GetAsync("/instances?page=0");

        Assert.Equal(20, Regex.Matches(first, "<td class=\"cell-primary\">").Count);
        Assert.Contains("Showing 1–20 of 23.", first, StringComparison.Ordinal);
        Assert.Contains("<a class=\"button\" href=\"/instances?page=2\" rel=\"next\">Next</a>", first, StringComparison.Ordinal);
        Assert.DoesNotContain("rel=\"prev\"", first, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(second, "<td class=\"cell-primary\">").Count);
        Assert.Contains("Showing 21–23 of 23.", second, StringComparison.Ordinal);
        Assert.Contains("<a class=\"button\" href=\"/instances\" rel=\"prev\">Previous</a>", second, StringComparison.Ordinal);
        Assert.DoesNotContain("rel=\"next\"", second, StringComparison.Ordinal);
        // Past the last page there is no blank table, but a way back.
        Assert.Contains("<h2>Nothing on this page</h2>", beyond, StringComparison.Ordinal);
        Assert.Contains("<a class=\"button state-action\" href=\"/instances\">Back to the first page</a>", beyond, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", beyond, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
        Assert.Contains("That request could not be processed", await nonsense.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // === Instance health =========================================================================

    [Fact]
    public async Task InstanceHealth_IsWhatTheHealthServiceFinds_InWords_AndLookingChangesNothing()
    {
        // The real provisioner and the real runtime probe, over the in-memory Docker Engine.
        using var factory = new WebFactory { BootstrapAdmin = (Browser.Admin, Browser.Password), UseDockerProvisioner = true };
        using var api = factory.CreateClientAs(UserRole.Admin);
        var instanceId = await factory.CreateRunningInstanceAsync(api);
        var container = DockerResourceNaming.ContainerName(instanceId);
        var viewer = await SignedInAsync(factory, api, "viewer");
        var path = $"/instances/{instanceId}";

        var healthy = Flat(await viewer.GetHtmlAsync(path));
        factory.Docker.Exec = () => 1;
        var notAnswering = Flat(await viewer.GetHtmlAsync(path));
        factory.Docker.Containers[container] = factory.Docker.Containers[container] with { State = DockerContainerState.Exited };
        var stopped = Flat(await viewer.GetHtmlAsync(path));
        factory.Docker.Containers.Remove(container);
        var missing = Flat(await viewer.GetHtmlAsync(path));
        factory.Docker.Unavailable = true;
        var unknown = Flat(await viewer.GetHtmlAsync(path));

        Assert.Contains("<h2 id=\"health-title\">Health</h2>", healthy, StringComparison.Ordinal);
        Assert.Matches("<dt>Health</dt> <dd> <span class=\"badge badge-success\"><svg.*?</svg> Healthy</span> <span class=\"muted\">The database server is running and accepting connections\\.</span>", healthy);
        Assert.Contains("<dt>Server</dt> <dd>Running</dd> <dt>Connections</dt> <dd>Accepted</dd>", healthy, StringComparison.Ordinal);
        Assert.Matches("Checked <time datetime=\"[^\"]+\">[^<]+ UTC</time>", healthy);
        Assert.Contains($"<a href=\"{path}\">Check again</a>", healthy, StringComparison.Ordinal);

        Assert.Matches("<dt>Health</dt> <dd> <span class=\"badge badge-warning\"><svg.*?</svg> Degraded</span> <span class=\"muted\">The database server is running but not accepting connections\\.", notAnswering);
        Assert.Contains("<dt>Server</dt> <dd>Running</dd> <dt>Connections</dt> <dd>Not accepted</dd>", notAnswering, StringComparison.Ordinal);

        Assert.Matches("<dt>Health</dt> <dd> <span class=\"badge badge-danger\"><svg.*?</svg> Unhealthy</span> <span class=\"muted\">The database server is stopped\\.</span>", stopped);
        Assert.Contains("<dt>Server</dt> <dd>Stopped</dd>", stopped, StringComparison.Ordinal);

        Assert.Matches("</svg> Unhealthy</span> <span class=\"muted\">The database server could not be found\\.</span>", missing);
        Assert.Contains("<dt>Server</dt> <dd>Not found</dd>", missing, StringComparison.Ordinal);

        Assert.Matches("</svg> Degraded</span> <span class=\"muted\">Docker could not be asked", unknown);
        Assert.Contains("<dt>Server</dt> <dd>Unknown</dd> <dt>Connections</dt> <dd>Unknown</dd>", unknown, StringComparison.Ordinal);

        // What Docker calls things stays in Docker, and the record is as it was: health only looks.
        foreach (var html in new[] { healthy, notAnswering, stopped, missing, unknown })
        {
            // The Health section, that is; where the server is on the Docker network is the Connection section's to say.
            var health = Regex.Match(html, "<section class=\"section\" aria-labelledby=\"health-title\">.*?</section>").Value;
            Assert.NotEmpty(health);
            Assert.DoesNotContain(container, health, StringComparison.Ordinal);
            Assert.DoesNotContain("container", health, StringComparison.OrdinalIgnoreCase);
            Assert.Matches("<h1>production-db</h1> <span class=\"badge badge-success\"><svg.*?</svg> Running</span>", html);
        }

        Assert.Equal(InstanceStatus.Running, await factory.WithDbAsync(db => db.Instances.Select(instance => instance.Status).SingleAsync()));
    }

    // === Databases ===============================================================================

    [Fact]
    public async Task DatabaseList_Empty_OffersCreationToAnOperator_AndOnlyExplainsToAViewer()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var op = await SignedInAsync("operator");
        var viewer = await SignedInAsync("viewer");

        var operatorHtml = Flat(await op.GetHtmlAsync($"/instances/{instanceId}/databases"));
        var viewerHtml = Flat(await viewer.GetHtmlAsync($"/instances/{instanceId}/databases"));

        Assert.Contains("<h1>Databases</h1>", operatorHtml, StringComparison.Ordinal);
        Assert.Contains("<h2>No databases yet</h2>", operatorHtml, StringComparison.Ordinal);
        Assert.Contains("Create the first database in this instance.", operatorHtml, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(operatorHtml, $"href=\"/instances/{instanceId}/databases/create\"").Count);
        Assert.DoesNotContain("<table", operatorHtml, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"/instances/{instanceId}\">production-db</a>", operatorHtml, StringComparison.Ordinal);

        Assert.Contains("<h2>No databases yet</h2>", viewerHtml, StringComparison.Ordinal);
        Assert.Contains("An operator or an administrator creates them.", viewerHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("/databases/create", viewerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Viewer_ListsDatabases_AndOpensOne_ButIsOfferedNoChange()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api, name: "production-db", engine: "mysql");
        var ready = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "orders");
        var (creating, _) = await _api.CreateDatabaseAsync(instanceId, "invoices");
        var viewer = await SignedInAsync("viewer");

        var list = Flat(await viewer.GetHtmlAsync($"/instances/{instanceId}/databases"));
        var onInstance = Flat(await viewer.GetHtmlAsync($"/instances/{instanceId}"));
        var details = Flat(await viewer.GetHtmlAsync($"/instances/{instanceId}/databases/{ready}"));

        foreach (var html in new[] { list, onInstance })
        {
            Assert.Contains("<caption>Databases of production-db, newest first</caption>", html, StringComparison.Ordinal);
            Assert.Contains($"<td class=\"cell-primary\"><a href=\"/instances/{instanceId}/databases/{ready}\">orders</a></td>", html, StringComparison.Ordinal);
            Assert.Contains($"<td class=\"cell-primary\"><a href=\"/instances/{instanceId}/databases/{creating}\">invoices</a></td>", html, StringComparison.Ordinal);
            Assert.Matches("<span class=\"badge badge-success\"><svg.*?</svg> Ready</span>", html);
            Assert.Matches("<span class=\"badge badge-progress\"><svg.*?</svg> Creating</span>", html);
            // Something is on its way, so the page says so and keeps itself up to date.
            Assert.Contains("data-refresh=\"5\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("/delete", html, StringComparison.Ordinal);
            Assert.DoesNotContain("/create", html, StringComparison.Ordinal);
        }

        Assert.Contains("Showing 1–2 of 2.", list, StringComparison.Ordinal);
        Assert.Contains("All databases (2)", onInstance, StringComparison.Ordinal);

        Assert.Matches("<h1>orders</h1> <span class=\"badge badge-success\"><svg.*?</svg> Ready</span>", details);
        Assert.Contains("<dt>Name</dt> <dd class=\"mono\">orders</dd>", details, StringComparison.Ordinal);
        Assert.Matches($"<dt>Instance</dt> <dd> <a href=\"/instances/{instanceId}\">production-db</a> <span class=\"badge badge-success\">", details);
        Assert.Contains("<dt>Engine</dt> <dd>MySQL 16</dd>", details, StringComparison.Ordinal);
        Assert.Matches("<dt>Created</dt> <dd> <time datetime=\"[^\"]+\">[^<]+ UTC</time> </dd>", details);
        Assert.Contains($"<dd class=\"mono\">{ready}</dd>", details, StringComparison.Ordinal);
        Assert.Contains("<td class=\"cell-primary\">Create database</td>", details, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"/instances/{instanceId}/databases\">Databases</a>", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Delete", details, StringComparison.Ordinal);
        // Infrastructure, not a console: nothing to run SQL with, on any of these pages.
        foreach (var html in new[] { list, onInstance, details })
        {
            Assert.DoesNotContain("<textarea", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("query", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("console", html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Viewer_CannotCreateOrDeleteADatabase_WhateverTheyAskFor()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "orders");
        var viewer = await SignedInAsync("viewer");

        var createPage = await viewer.GetAsync($"/instances/{instanceId}/databases/create");
        var deletePage = await viewer.GetAsync($"/instances/{instanceId}/databases/{databaseId}/delete");
        var create = await viewer.PostFormAsync($"/instances/{instanceId}/databases/create", DatabaseForm("sneaky"), tokenFrom: "/instances");
        var delete = await viewer.PostFormAsync($"/instances/{instanceId}/databases/{databaseId}/delete", [], tokenFrom: "/instances");

        foreach (var response in new[] { createPage, deletePage, create, delete })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("You don&#x27;t have permission", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal("ready", (await _api.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(1, await DatabaseCountAsync());
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task OperatorAndAdmin_CreateADatabase_WhichIsCreating_UntilItsJobHasRun(string role)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var browser = await SignedInAsync(role);
        var form = Flat(await browser.GetHtmlAsync($"/instances/{instanceId}/databases/create"));

        var response = await browser.PostFormAsync($"/instances/{instanceId}/databases/create", DatabaseForm("orders_2026"));
        var (path, html) = await FollowAsync(browser, response);

        Assert.Contains("<label class=\"field-label\" for=\"Input_Name\">", form, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"name-hint\"", form, StringComparison.Ordinal);
        Assert.Contains("data-busy-label=\"Creating…\"", form, StringComparison.Ordinal);

        var database = await _factory.WithDbAsync(db => db.Databases.AsNoTracking().SingleAsync());
        Assert.Equal($"/instances/{instanceId}/databases/{database.Id}", path);
        Assert.Equal("orders_2026", database.Name);
        // Started, and said to be exactly that; nowhere does the page claim more.
        Assert.Matches("<div class=\"alert alert-progress\" role=\"status\">.*?Database creation started\\.", html);
        Assert.DoesNotContain("created successfully", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Database created", html, StringComparison.Ordinal);
        Assert.Matches("<h1>orders_2026</h1> <span class=\"badge badge-progress\"><svg.*?</svg> Creating</span>", html);
        Assert.Contains("data-refresh=\"5\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Delete database", html, StringComparison.Ordinal);
        Assert.False(_factory.DatabaseServers.Exists(instanceId, "orders_2026"));

        await _factory.ProcessJobAsync(await PendingJobOfAsync(instanceId));
        var ready = Flat(await browser.GetHtmlAsync(path));

        Assert.Matches("<h1>orders_2026</h1> <span class=\"badge badge-success\"><svg.*?</svg> Ready</span>", ready);
        Assert.DoesNotContain("data-refresh", ready, StringComparison.Ordinal);
        Assert.DoesNotContain("Database creation started", ready, StringComparison.Ordinal);
        Assert.Contains($"href=\"/instances/{instanceId}/databases/{database.Id}/delete\"", ready, StringComparison.Ordinal);
        Assert.True(_factory.DatabaseServers.Exists(instanceId, "orders_2026"));
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task OperatorAndAdmin_DeleteADatabase_OnlyByPostingTheConfirmation_AndItIsDeletingUntilItsJobHasRun(string role)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "orders");
        var browser = await SignedInAsync(role);
        var deletePath = $"/instances/{instanceId}/databases/{databaseId}/delete";

        var question = Flat(await browser.GetHtmlAsync(deletePath));
        await browser.GetHtmlAsync(deletePath);
        Assert.Equal("ready", (await _api.GetDatabaseAsync(databaseId)).Status());

        Assert.Contains("<h1>Delete database?</h1>", question, StringComparison.Ordinal);
        Assert.Contains("This permanently removes the database <strong class=\"mono\">orders</strong> and all the data in it from <strong>production-db</strong>.", question, StringComparison.Ordinal);
        Assert.Contains("This cannot be undone.", question, StringComparison.Ordinal);
        Assert.Contains($"<a class=\"button\" href=\"/instances/{instanceId}/databases/{databaseId}\">Cancel</a>", question, StringComparison.Ordinal);
        Assert.Contains(">Delete database</button>", question, StringComparison.Ordinal);

        var response = await browser.PostFormAsync(deletePath, []);
        var (path, html) = await FollowAsync(browser, response);

        Assert.Equal($"/instances/{instanceId}/databases", path);
        Assert.Matches("<div class=\"alert alert-progress\" role=\"status\">.*?Deletion of database &#x201C;orders&#x201D; started\\.", html);
        Assert.DoesNotContain("was deleted", html, StringComparison.Ordinal);
        Assert.Matches("orders</a></td> <td> <span class=\"badge badge-progress\"><svg.*?</svg> Deleting</span>", html);
        Assert.DoesNotContain("/delete", html, StringComparison.Ordinal);
        Assert.True(_factory.DatabaseServers.Exists(instanceId, "orders"));

        // Asking again while it is being deleted is refused by the service, in its words.
        var again = await browser.PostFormAsync(deletePath, []);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("<div class=\"alert-title\">DATABASE_DELETING</div> <div>Database is already being deleted.</div>", await HtmlOf(again), StringComparison.Ordinal);

        await _factory.ProcessJobAsync(await PendingJobOfAsync(instanceId));
        var after = Flat(await browser.GetHtmlAsync(path));
        var gone = await browser.GetAsync($"/instances/{instanceId}/databases/{databaseId}");

        Assert.Contains("<h2>No databases yet</h2>", after, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Contains("<h1>Database not found</h1>", await gone.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(_factory.DatabaseServers.Exists(instanceId, "orders"));
    }

    [Theory]
    [InlineData("", "name is required.")]
    [InlineData("Orders", "name must start with a lowercase letter and contain only lowercase letters, digits and underscores.")]
    [InlineData("1st", "name must start with a lowercase letter and contain only lowercase letters, digits and underscores.")]
    [InlineData("drop table;--", "name must start with a lowercase letter and contain only lowercase letters, digits and underscores.")]
    [InlineData("postgres", "name &#x27;postgres&#x27; is reserved by the database engine.")]
    [InlineData("a234567890123456789012345678901234567890123456789012345678901234", "name must be at most 63 characters.")]
    public async Task DatabaseForm_IsValidatedByTheApplicationsNameRule_KeepsWhatWasTyped_AndCreatesNothing(string name, string message)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var op = await SignedInAsync("operator");

        var response = await op.PostFormAsync($"/instances/{instanceId}/databases/create", DatabaseForm(name));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var html = await HtmlOf(response);
        Assert.Contains("<h1>Create database</h1>", html, StringComparison.Ordinal);
        Assert.Contains($"<span class=\"field-error field-validation-error\" data-valmsg-for=\"Input.Name\" data-valmsg-replace=\"true\">{message}</span>", html, StringComparison.Ordinal);
        Assert.Contains("input-validation-error", html, StringComparison.Ordinal);
        Assert.Contains($"value=\"{Encoded(name)}\"", html, StringComparison.Ordinal);
        Assert.Equal(0, await DatabaseCountAsync());
    }

    [Fact]
    public async Task CreatingADatabase_ThatTheApplicationWillNotCreate_ShowsWhy_WithTheStableCode()
    {
        var running = await _factory.CreateRunningInstanceAsync(_api, name: "running");
        await _factory.CreateReadyDatabaseAsync(_api, running, "orders");
        var (provisioning, _) = await _api.CreateInstanceAsync(name: "still-provisioning");
        var op = await SignedInAsync("operator");

        var duplicate = await op.PostFormAsync($"/instances/{running}/databases/create", DatabaseForm("orders"));
        var twice = await op.PostFormAsync($"/instances/{running}/databases/create", DatabaseForm("orders"));
        var warned = Flat(await op.GetHtmlAsync($"/instances/{provisioning}/databases/create"));
        var notReady = await op.PostFormAsync($"/instances/{provisioning}/databases/create", DatabaseForm("orders"));

        foreach (var response in new[] { duplicate, twice })
        {
            // The same request arriving twice is as safe as it arriving once.
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var html = await HtmlOf(response);
            Assert.Contains("<div class=\"alert-title\">DATABASE_ALREADY_EXISTS</div> <div>The instance already has a database with this name.</div>", html, StringComparison.Ordinal);
            Assert.Contains("value=\"orders\"", html, StringComparison.Ordinal);
        }

        Assert.Contains("A database can be created only while the instance is running.", warned, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
        Assert.Contains("<div class=\"alert-title\">INSTANCE_NOT_READY</div> <div>Database operations need a running instance.</div>", await HtmlOf(notReady), StringComparison.Ordinal);
        Assert.Equal(1, await DatabaseCountAsync());
    }

    [Fact]
    public async Task DeletingADatabase_ThatIsBeingBackedUp_IsRefusedWithTheStableCode_AndItStaysReady()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "orders");
        await _api.CreateBackupAsync(databaseId);
        var op = await SignedInAsync("operator");

        var response = await op.PostFormAsync($"/instances/{instanceId}/databases/{databaseId}/delete", []);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            "<div class=\"alert-title\">BACKUP_OPERATION_IN_PROGRESS</div> <div>Database cannot be deleted while a backup of it is in progress.</div>",
            await HtmlOf(response),
            StringComparison.Ordinal);
        Assert.Equal("ready", (await _api.GetDatabaseAsync(databaseId)).Status());
    }

    [Fact]
    public async Task DatabaseThatFailed_ShowsItsStatus_ItsErrorCode_AndItsMessage_AndIsNotOfferedForDeletion()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var (databaseId, jobId) = await _api.CreateDatabaseAsync(instanceId, "orders");
        _factory.DatabaseServers.FailAllCalls();
        await _factory.ProcessJobAsync(jobId);
        var op = await SignedInAsync("operator");

        var details = Flat(await op.GetHtmlAsync($"/instances/{instanceId}/databases/{databaseId}"));
        var list = Flat(await op.GetHtmlAsync($"/instances/{instanceId}/databases"));
        var database = await _api.GetDatabaseAsync(databaseId);
        var code = database.GetProperty("error").GetProperty("code").GetString()!;
        var message = database.GetProperty("error").GetProperty("message").GetString()!;

        Assert.Matches("<h1>orders</h1> <span class=\"badge badge-danger\"><svg.*?</svg> Failed</span>", details);
        Assert.Contains($"<div class=\"alert-title\">{code}</div> <div>{Encoded(message)}</div>", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Delete database", details, StringComparison.Ordinal);
        Assert.Contains($"</svg> Failed</span> <code>{code}</code>", list, StringComparison.Ordinal);
        Assert.DoesNotContain("/delete", list, StringComparison.Ordinal);
    }

    // === Not found ===============================================================================

    [Fact]
    public async Task WhatIsNotThere_IsNotFound_AndThePageSaysWhatWasMissing()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api, name: "one");
        var otherInstance = await _factory.CreateRunningInstanceAsync(_api, name: "other");
        var databaseOfOther = await _factory.CreateReadyDatabaseAsync(_api, otherInstance, "orders");
        var admin = await SignedInAsync("admin");
        var nobody = Guid.CreateVersion7();

        foreach (var (path, missing) in new[]
                 {
                     ($"/instances/{nobody}", "Instance"),
                     ($"/instances/{nobody}/delete", "Instance"),
                     ($"/instances/{nobody}/databases", "Instance"),
                     ($"/instances/{nobody}/databases/create", "Instance"),
                     ($"/instances/{nobody}/databases/{databaseOfOther}", "Instance"),
                     ($"/instances/{instanceId}/databases/{nobody}", "Database"),
                     ($"/instances/{instanceId}/databases/{nobody}/delete", "Database"),
                     // A database of another instance is not found under this one.
                     ($"/instances/{instanceId}/databases/{databaseOfOther}", "Database"),
                     ($"/instances/{instanceId}/databases/{databaseOfOther}/delete", "Database")
                 })
        {
            var response = await admin.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains($"<h1>{missing} not found</h1>", html, StringComparison.Ordinal);
            Assert.Contains($"Request ID: <span class=\"mono\">{string.Join("", response.Headers.GetValues("X-Request-Id"))}</span>", html, StringComparison.Ordinal);
            Assert.Contains("Sign out", html, StringComparison.Ordinal);
        }

        // Posting to what is not there, or to another instance's database, does nothing either.
        var posted = await admin.PostFormAsync($"/instances/{instanceId}/databases/{databaseOfOther}/delete", [], tokenFrom: "/instances");
        Assert.Equal(HttpStatusCode.NotFound, posted.StatusCode);
        Assert.Equal("ready", (await _api.GetDatabaseAsync(databaseOfOther)).Status());

        // An address that is not an id at all is no page.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/instances/not-an-id")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/databases")).StatusCode);
    }

    // === Security ================================================================================

    [Fact]
    public async Task EveryChange_NeedsTheAntiforgeryToken_AndWithoutItNothingChanges()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "orders");
        var admin = await SignedInAsync("admin");

        var responses = new[]
        {
            await admin.PostFormAsync("/instances/create", InstanceForm(), withToken: false),
            await admin.PostFormAsync($"/instances/{instanceId}/databases/create", DatabaseForm("sneaky"), withToken: false),
            await admin.PostFormAsync($"/instances/{instanceId}/databases/{databaseId}/delete", [], withToken: false),
            await admin.PostFormAsync($"/instances/{instanceId}/delete", [], withToken: false),
            // A token that is not this session's is no token.
            await admin.PostAsync($"/instances/{instanceId}/delete", new FormUrlEncodedContent([new("__RequestVerificationToken", "CfDJ8forged")]))
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("That request could not be processed", html, StringComparison.Ordinal);
            Assert.Contains("Request ID:", html, StringComparison.Ordinal);
        }

        Assert.Equal(1, await InstanceCountAsync());
        Assert.Equal(1, await DatabaseCountAsync());
        Assert.Equal("ready", (await _api.GetDatabaseAsync(databaseId)).Status());
        Assert.Empty(_factory.Provisioner.DeprovisionedInstanceIds);
    }

    [Fact]
    public async Task Get_NeverChangesAnything_OnAnyPage()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "orders");
        var admin = await SignedInAsync("admin");
        var jobs = await _factory.WithDbAsync(db => db.Jobs.CountAsync());

        foreach (var path in PagesOf(instanceId, databaseId))
        {
            // With whatever a link could carry: a GET is still only a GET.
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{path}?handler=Post&confirm=true&Input.Name=sneaky")).StatusCode);
        }

        Assert.Equal(1, await InstanceCountAsync());
        Assert.Equal(1, await DatabaseCountAsync());
        Assert.Equal("ready", (await _api.GetDatabaseAsync(databaseId)).Status());
        Assert.Equal(jobs, await _factory.WithDbAsync(db => db.Jobs.CountAsync()));
        Assert.Empty(_factory.Provisioner.DeprovisionedInstanceIds);
    }

    private static string[] PagesOf(Guid instanceId, Guid databaseId) =>
    [
        "/instances", "/instances/create", $"/instances/{instanceId}", $"/instances/{instanceId}/delete",
        $"/instances/{instanceId}/databases", $"/instances/{instanceId}/databases/create",
        $"/instances/{instanceId}/databases/{databaseId}", $"/instances/{instanceId}/databases/{databaseId}/delete"
    ];

    [Fact]
    public void PagesThatChangeThings_HaveThePoliciesOfTheApiEndpointsThatDoTheSame()
    {
        var policies = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<PageActionDescriptor>() is not null)
            .GroupBy(endpoint => "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Select(data => data.Policy).Distinct().SingleOrDefault());

        Assert.Equal("admin", policies["/instances/create"]);
        Assert.Equal("admin", policies["/instances/{id:guid}/delete"]);
        Assert.Equal("operator", policies["/instances/{id:guid}/databases/create"]);
        Assert.Equal("operator", policies["/instances/{id:guid}/databases/{databaseId:guid}/delete"]);
        // Reading is for every signed-in user: no policy of their own, so the fallback one.
        Assert.Null(policies["/instances"]);
        Assert.Null(policies["/instances/{id:guid}"]);
        Assert.Null(policies["/instances/{id:guid}/databases"]);
        Assert.Null(policies["/instances/{id:guid}/databases/{databaseId:guid}"]);
        Assert.DoesNotContain("/databases", policies.Keys);
    }

    [Fact]
    public async Task Credentials_NeverAppear_InAnyPage_OrInTheLog()
    {
        var admin = await SignedInAsync("admin");
        var (_, created) = await FollowAsync(admin, await admin.PostFormAsync("/instances/create", InstanceForm(name: "orders")));
        var instanceId = await _factory.WithDbAsync(db => db.Instances.Select(instance => instance.Id).SingleAsync());
        await _factory.ProcessJobAsync(await PendingJobOfAsync(instanceId));
        var (_, createdDatabase) = await FollowAsync(admin, await admin.PostFormAsync($"/instances/{instanceId}/databases/create", DatabaseForm("orders")));
        var databaseId = await _factory.WithDbAsync(db => db.Databases.Select(database => database.Id).SingleAsync());
        await _factory.ProcessJobAsync(await PendingJobOfAsync(instanceId));
        var password = await _factory.AdminPasswordAsync(instanceId);
        Assert.True(password.Length >= 16);

        var pages = new List<string> { created, createdDatabase };
        foreach (var path in PagesOf(instanceId, databaseId))
        {
            pages.Add(await admin.GetHtmlAsync(path));
        }

        pages.Add(await HtmlOf(await admin.PostFormAsync($"/instances/{instanceId}/databases/create", DatabaseForm("orders"))));

        foreach (var html in pages)
        {
            Assert.DoesNotContain(password, html, StringComparison.Ordinal);
            // The Connection section says where the password goes and that it is not shown; nothing
            // else on a page has reason to mention one, and nothing anywhere holds one.
            var outsideConnection = Regex.Replace(html, "<section class=\"section\" aria-labelledby=\"connection-title\">.*?</section>", string.Empty, RegexOptions.Singleline);
            foreach (var word in new[] { "password", "secret", "connection string", "POSTGRES_PASSWORD" })
            {
                Assert.DoesNotContain(word, outsideConnection, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotContain("POSTGRES_PASSWORD", html, StringComparison.Ordinal);
            Assert.DoesNotMatch("(?i)password\\s*[=:]\\s*[^&<\\s]", html.Replace("postgres:&lt;password&gt;", string.Empty, StringComparison.Ordinal));

            // The only hidden fields of any form are the antiforgery token and the framework's
            // note of which fields are numbers; neither holds anything of an instance.
            Assert.All(HiddenInput().Matches(html), field => Assert.Matches("name=\"(__RequestVerificationToken|__Invariant)\"", field.Value));
        }

        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(Browser.Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatAUserTyped_IsShownAsText_OnEveryPage_AndInTheMessageAfterADelete()
    {
        const string hostile = "<script>alert(1)</script><img src=x onerror=alert(2)>\"'";
        var encoded = Encoded(hostile);
        var admin = await SignedInAsync("admin");
        await admin.PostFormAsync("/instances/create", InstanceForm(name: hostile));
        var instanceId = await _factory.WithDbAsync(db => db.Instances.Select(instance => instance.Id).SingleAsync());
        await _factory.ProcessJobAsync(await PendingJobOfAsync(instanceId));
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "orders");

        var pages = new List<string>();
        foreach (var path in PagesOf(instanceId, databaseId))
        {
            pages.Add(await admin.GetHtmlAsync(path));
        }

        // What was typed into a form that was refused comes back as text too.
        pages.Add(await (await admin.PostFormAsync($"/instances/{instanceId}/databases/create", DatabaseForm(hostile))).Content.ReadAsStringAsync());
        var (_, afterDelete) = await FollowAsync(admin, await admin.PostFormAsync($"/instances/{instanceId}/delete", []));
        pages.Add(afterDelete);

        Assert.Equal(hostile, await _factory.WithDbAsync(async db => (await db.Instances.Select(instance => instance.Name).ToListAsync()).SingleOrDefault() ?? hostile));
        foreach (var html in pages)
        {
            Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
            Assert.Empty(InlineScript().Matches(html));
        }

        Assert.All(pages.Where((_, index) => index is not 0 and not 1 and not 8), html => Assert.Contains(encoded, html, StringComparison.Ordinal));
        Assert.Contains("&#x201C;" + encoded + "&#x201D; was deleted.", afterDelete, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("viewer")]
    public async Task NewPages_CarryTheSecurityHeaders_AndContainNothingTheirContentSecurityPolicyWouldBlock(string role)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "orders");
        await _api.CreateDatabaseAsync(instanceId, "being_created");
        var browser = await SignedInAsync(role);
        string[] extra = [$"/instances/{Guid.CreateVersion7()}", "/instances?page=0"];

        foreach (var path in PagesOf(instanceId, databaseId).Concat(extra))
        {
            var response = await browser.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();

            Assert.Equal(WebProgram.PageContentSecurityPolicy, string.Join("", response.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("nosniff", string.Join("", response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("DENY", string.Join("", response.Headers.GetValues("X-Frame-Options")));
            Assert.Contains("no-store", string.Join("", response.Headers.GetValues("Cache-Control")), StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{32}$", string.Join("", response.Headers.GetValues("X-Request-Id")));

            Assert.Empty(InlineScript().Matches(html));
            Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(StyleAttribute().Matches(html));
            Assert.Empty(EventHandlerAttribute().Matches(html));
            Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http-equiv", html, StringComparison.OrdinalIgnoreCase);
            Assert.All(FormAction().Matches(html).Select(match => match.Groups[1].Value), action => Assert.StartsWith("/", action, StringComparison.Ordinal));
            Assert.All(Link().Matches(html).Select(match => match.Groups[1].Value).Where(href => href != "#main"), href => Assert.StartsWith("/", href, StringComparison.Ordinal));
            // Every form is a POST with a token; nothing changes through a link.
            Assert.All(Form().Matches(html), form => Assert.Contains("method=\"post\"", form.Value, StringComparison.Ordinal));
            Assert.Equal(Form().Matches(html).Count, Regex.Matches(html, "name=\"__RequestVerificationToken\"").Count);
        }
    }

    [Fact]
    public async Task MessageForTheNextPage_TravelsInAnHttpOnlyCookie_NotInTheAddress()
    {
        var admin = await SignedInAsync("admin");

        var response = await admin.PostFormAsync("/instances/create", InstanceForm());

        var cookie = Assert.Contains("aurora.flash", response.SetCookies());
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Instance", cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("?", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshScript_ReloadsOnlyWhileThePageIsLookedAt_ALimitedNumberOfTimes_AndTalksToNobody()
    {
        using var browser = _factory.CreateBrowser();

        var script = await (await browser.GetAsync("/js/aurora.js")).Content.ReadAsStringAsync();

        Assert.Contains("[data-refresh]", script, StringComparison.Ordinal);
        Assert.Contains("document.visibilityState !== \"visible\"", script, StringComparison.Ordinal);
        Assert.Contains("visibilitychange", script, StringComparison.Ordinal);
        Assert.Contains("refreshCount >= refreshLimit", script, StringComparison.Ordinal);
        Assert.Contains("replaceState", script, StringComparison.Ordinal);
        Assert.DoesNotContain("location.hash =", script, StringComparison.Ordinal);
        Assert.Contains("window.location.reload()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("setInterval", script, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "fetch(", "XMLHttpRequest", "WebSocket", "EventSource", "signalr", "localStorage", "sessionStorage" })
        {
            Assert.DoesNotContain(forbidden, script, StringComparison.OrdinalIgnoreCase);
        }
    }

    [GeneratedRegex("<script(?![^>]*\\bsrc=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    [GeneratedRegex("<[^>]+\\sstyle\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttribute();

    [GeneratedRegex("<[^>]+\\son[a-z]+\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerAttribute();

    [GeneratedRegex("<form[^>]+\\saction=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex FormAction();

    [GeneratedRegex("<form[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Form();

    [GeneratedRegex("<a [^>]*href=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    [GeneratedRegex("<input[^>]+type=\"hidden\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInput();
}
