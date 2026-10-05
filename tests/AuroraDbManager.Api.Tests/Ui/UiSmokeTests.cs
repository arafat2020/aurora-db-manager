using System.Net;
using System.Text.Json;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Web;
using AuroraDbManager.Web.Components;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Ui;

/// <summary>
/// The pages render, and show what the application services say: the overview from monitoring,
/// the tables from the same listings the API serves. Data is created through the API of the same
/// host, as an automation client would, and then looked at through the pages, as a person would.
/// </summary>
public sealed class UiSmokeTests : IDisposable
{
    private readonly WebFactory _factory = new() { BootstrapAdmin = (Browser.Admin, Browser.Password) };
    private readonly HttpClient _api;
    private readonly HttpClient _browser;

    public UiSmokeTests()
    {
        _api = _factory.CreateClientAs(UserRole.Admin);
        _browser = _factory.CreateBrowser();
    }

    public void Dispose()
    {
        _browser.Dispose();
        _api.Dispose();
        _factory.Dispose();
    }

    private static string Flat(string html) => System.Text.RegularExpressions.Regex.Replace(html, "\\s+", " ");

    // --- Overview -----------------------------------------------------------------------------

    [Fact]
    public async Task Dashboard_OnAnEmptyInstallation_ShowsHealthyStatus_Zeroes_AndAnEmptyStateForFailures()
    {
        await _browser.SignInAsync();

        var html = Flat(await _browser.GetHtmlAsync("/"));

        Assert.Contains("<h1>Overview</h1>", html, StringComparison.Ordinal);
        Assert.Contains("System status", html, StringComparison.Ordinal);
        Assert.Contains("<dt>System database</dt>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Docker</dt>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Degraded", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhealthy", html, StringComparison.Ordinal);
        foreach (var card in new[] { "Instances", "Running jobs", "Backups in progress", "Recent failures" })
        {
            Assert.Contains($"{card}</div> <div class=\"stat-value\">0</div>", html, StringComparison.Ordinal);
        }

        Assert.Contains("No recent failures", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_ShowsWhatMonitoringReports_CountsStatusAndRecentFailures()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api, name: "production-db");
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "app");
        await _api.CreateInstanceAsync(name: "still-provisioning");
        var (_, backupJob) = await _api.CreateBackupAsync(databaseId);
        _factory.DumpTools.FailAllRuns();
        await _factory.ProcessJobAsync(backupJob);
        _factory.Docker.Unavailable = true;
        var summary = await (await _api.GetAsync(MonitoringSummaryUrl)).ReadJsonAsync(HttpStatusCode.OK);
        await _browser.SignInAsync();

        var html = Flat(await _browser.GetHtmlAsync("/"));

        // The same numbers the API's summary gives.
        Assert.Equal(2, summary.GetProperty("instances").GetProperty("total").GetInt32());
        Assert.Contains("Instances</div> <div class=\"stat-value\">2</div> <div class=\"stat-detail\">1 running · 0 failed</div>", html, StringComparison.Ordinal);
        Assert.Contains("Running jobs</div> <div class=\"stat-value\">0</div> <div class=\"stat-detail\">1 pending</div>", html, StringComparison.Ordinal);
        Assert.Contains("Recent failures</div> <div class=\"stat-value\">1</div>", html, StringComparison.Ordinal);

        // Status in words, with its reason: Docker cannot be reached, and a job failed.
        Assert.Contains("<dt>Docker</dt> <dd><span class=\"badge badge-warning\">", html, StringComparison.Ordinal);
        Assert.Contains("Degraded", html, StringComparison.Ordinal);
        Assert.Contains("1 recent failure", html, StringComparison.Ordinal);

        // The failure itself: what it was and its stable code, never its message.
        Assert.Contains("<td class=\"cell-primary\">Back up database</td>", html, StringComparison.Ordinal);
        Assert.Contains("<code>BACKUP_PROCESS_FAILED</code>", html, StringComparison.Ordinal);
        Assert.Contains(backupJob.ToString(), html, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-tool-detail", html, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-daemon-detail", html, StringComparison.Ordinal);
    }

    // --- Tables -------------------------------------------------------------------------------

    [Fact]
    public async Task Instances_Empty_ShowsAnEmptyState_ThatSuitsTheRole()
    {
        await _browser.SignInAsync();

        var html = await _browser.GetHtmlAsync("/instances");

        Assert.Contains("No database instances yet", html, StringComparison.Ordinal);
        Assert.Contains("Create your first PostgreSQL or MySQL instance.", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/instances/create\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Instances_AreListed_WithTheirStatusInWords_AndTheErrorCodeOfAFailedOne()
    {
        var production = await _factory.CreateRunningInstanceAsync(_api, name: "production-db");
        var analytics = await _factory.CreateRunningInstanceAsync(_api, name: "analytics", engine: "mysql");
        var (_, pending) = await _api.CreateInstanceAsync(name: "still-provisioning");
        var (_, failing) = await _api.CreateInstanceAsync(name: "broken");
        _factory.Provisioner.FailAllCalls();
        await _factory.ProcessJobAsync(failing);
        await _browser.SignInAsync();

        var html = Flat(await _browser.GetHtmlAsync("/instances"));

        Assert.Contains("<caption>Instances, newest first</caption>", html, StringComparison.Ordinal);
        Assert.Contains("<th scope=\"col\">Status</th>", html, StringComparison.Ordinal);
        Assert.Contains($"<td class=\"cell-primary\"><a href=\"/instances/{production}\">production-db</a></td> <td>PostgreSQL 16</td>", html, StringComparison.Ordinal);
        Assert.Contains($"<td class=\"cell-primary\"><a href=\"/instances/{analytics}\">analytics</a></td> <td>MySQL 16</td>", html, StringComparison.Ordinal);
        // Every status is a word with a mark, not a colour.
        Assert.Matches("<span class=\"badge badge-success\"><svg[^>]*aria-hidden=\"true\".*?</svg> Running</span>", html);
        Assert.Matches("<span class=\"badge badge-progress\"><svg.*?</svg> Provisioning</span>", html);
        Assert.Matches("<span class=\"badge badge-danger\"><svg.*?</svg> Failed</span> <code>PROVISIONING_FAILED</code>", html);
        Assert.Contains("Showing 1–4 of 4.", html, StringComparison.Ordinal);
        Assert.Contains("<time datetime=\"", html, StringComparison.Ordinal);
        Assert.NotEqual(pending, failing);
    }

    [Fact]
    public async Task Jobs_AreListed_NewestFirst_AtMostAPage()
    {
        for (var i = 0; i < 23; i++)
        {
            await _api.CreateInstanceAsync(name: $"instance-{i}");
        }

        await _browser.SignInAsync();

        var html = Flat(await _browser.GetHtmlAsync("/jobs"));

        Assert.Equal(20, System.Text.RegularExpressions.Regex.Matches(html, "<td class=\"cell-primary\"><a href=\"/jobs/[0-9a-f-]{36}\">Provision instance</a></td>").Count);
        Assert.Contains("Showing 1–20 of 23.", html, StringComparison.Ordinal);
        Assert.Matches("<span class=\"badge badge-progress\"><svg.*?</svg> Pending</span>", html);
    }

    [Fact]
    public async Task WhatAUserTyped_IsShownAsText_NeverAsMarkup()
    {
        const string hostile = "<script>alert(1)</script><img src=x onerror=alert(2)>";
        await _api.CreateInstanceAsync(name: hostile);
        await _browser.SignInAsync();

        var html = await _browser.GetHtmlAsync("/instances");

        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    // --- The building blocks -------------------------------------------------------------------

    [Fact]
    public async Task StyleGuide_ShowsEveryBuildingBlock_InDevelopment()
    {
        await _browser.SignInAsync();

        var html = Flat(await _browser.GetHtmlAsync("/styleguide"));

        foreach (var block in new[]
                 {
                     "badge badge-success", "badge badge-progress", "badge badge-neutral", "badge badge-warning", "badge badge-danger",
                     "button button-primary", "button button-danger", "alert alert-success", "alert alert-danger", "class=\"table\"",
                     "class=\"state\"", "class=\"spinner\"", "class=\"field-hint\"", "data-confirm=", "class=\"page-header\"", "data-confirm-dialog"
                 })
        {
            Assert.Contains(block, html, StringComparison.Ordinal);
        }

        // Every status a later page will need has its word.
        foreach (var status in new[] { "Running", "Stopped", "Failed", "Provisioning", "Creating", "Ready", "Deleting", "Pending", "Completed" })
        {
            Assert.Contains($"</svg> {status}</span>", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Form_IsValidatedOnTheServer_AndShowsItsErrorsAtTheField()
    {
        await _browser.SignInAsync();

        var invalid = await _browser.PostFormAsync("/styleguide", [new("Sample.Name", "Not A Valid Name!"), new("Sample.Engine", "postgres")]);
        var missing = await _browser.PostFormAsync("/styleguide", [new("Sample.Engine", "postgres")]);
        var valid = await _browser.PostFormAsync("/styleguide", [new("Sample.Name", "orders_2026"), new("Sample.Engine", "mysql")]);
        var forged = await _browser.PostFormAsync("/styleguide", [new("Sample.Name", "orders_2026")], withToken: false);

        var invalidHtml = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("Use lowercase letters, digits and underscores, starting with a letter.", invalidHtml, StringComparison.Ordinal);
        Assert.Contains("input-validation-error", invalidHtml, StringComparison.Ordinal);
        // What was typed is kept in the field, as text.
        Assert.Contains("value=\"Not A Valid Name!\"", invalidHtml, StringComparison.Ordinal);
        Assert.Contains("Enter a name.", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("The server accepted", await valid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
    }

    [Fact]
    public async Task StyleGuide_DoesNotExist_OutsideDevelopment()
    {
        using var factory = new WebFactory { BootstrapAdmin = (Browser.Admin, Browser.Password), EnvironmentName = "Production" };
        using var browser = factory.CreateBrowser(baseAddress: "https://aurora.example.test");
        await browser.SignInAsync();

        var response = await browser.GetAsync("/styleguide");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Page not found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // --- Components and configuration ----------------------------------------------------------

    [Fact]
    public void Navigation_MarksTheSectionAPageBelongsTo()
    {
        Assert.True(NavigationMenu.Overview.IsCurrent("/"));
        Assert.False(NavigationMenu.Overview.IsCurrent("/instances"));
        Assert.True(NavigationMenu.Instances.IsCurrent("/instances"));
        Assert.True(NavigationMenu.Instances.IsCurrent("/Instances/0199/databases"));
        Assert.False(NavigationMenu.Instances.IsCurrent("/instances-archive"));
    }

    [Fact]
    public void ApplicationName_IsConfigurable_AndWebSettingsAreChecked()
    {
        using var factory = new WebFactory();
        Assert.Null(new WebOptions().Validate());
        Assert.Contains("ApplicationName", new WebOptions { ApplicationName = " " }.Validate(), StringComparison.Ordinal);
        Assert.Contains("SessionHours", new WebOptions { SessionHours = 0 }.Validate(), StringComparison.Ordinal);
        Assert.Contains("SessionHours", new WebOptions { SessionHours = 1000 }.Validate(), StringComparison.Ordinal);
    }

    [Fact]
    public void UiHostsSettings_AreTheApiHostsSettings_PlusItsOwnSection()
    {
        // Each host ships a settings file; the application's part of it must not drift between them.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        foreach (var name in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            using var api = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "AuroraDbManager.Api", name)));
            using var web = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "AuroraDbManager.Web", name)));

            var apiSettings = api.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetRawText().Replace(" ", "").Replace("\n", ""));
            var webSettings = web.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetRawText().Replace(" ", "").Replace("\n", ""));

            Assert.Equal(apiSettings.Keys.Order(), webSettings.Keys.Where(key => key != "Web").Order());
            Assert.All(apiSettings, setting => Assert.Equal(setting.Value, webSettings[setting.Key]));
            Assert.DoesNotContain("SigningKey", web.RootElement.GetRawText(), StringComparison.Ordinal);
        }
    }
}
