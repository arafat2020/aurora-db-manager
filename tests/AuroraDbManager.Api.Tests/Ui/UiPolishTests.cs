using System.Net;
using System.Text.RegularExpressions;
using AuroraDbManager.Api.Domain.Users;

namespace AuroraDbManager.Api.Tests.Ui;

/// <summary>
/// What holds for every page of the UI, whatever it is about: its structure, the names of its
/// controls, how a refused field says so, the words it uses, and what it loads. These are about
/// meaning and behaviour, which a change of appearance must not change; how a page looks is not
/// tested here.
/// </summary>
public sealed partial class UiPolishTests : IDisposable
{
    private const string UserPassword = "a-perfectly-fine-password";

    private readonly WebFactory _factory = new() { BootstrapAdmin = (Browser.Admin, Browser.Password) };
    private readonly HttpClient _api;
    private readonly List<HttpClient> _browsers = [];

    public UiPolishTests()
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

    /// <summary>An installation with one of everything, and the addresses of every page about it.</summary>
    private async Task<List<string>> SeededPagesAsync()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "orders");
        var backupId = await _factory.CreateCompletedBackupAsync(_api, databaseId);
        var (_, failing) = await _api.CreateBackupAsync(databaseId);
        _factory.DumpTools.FailAllRuns();
        await _factory.ProcessJobAsync(failing);
        _factory.DumpTools.ClearScript();
        await _factory.ProcessJobAsync(await ApiFactory.RequestRestoreAsync(_api, backupId));
        var schedule = await _api.PostAsync(
            $"{ApiClientExtensions.DatabasesUrl}/{databaseId}/backup-schedule",
            System.Net.Http.Json.JsonContent.Create(new { cronExpression = "0 0 * * *", timeZoneId = "Asia/Dhaka" }));
        Assert.Equal(HttpStatusCode.Created, schedule.StatusCode);
        await _api.PostAsync($"{ApiClientExtensions.InstancesUrl}/{instanceId}/external-access", null);

        var instance = $"/instances/{instanceId}";
        var database = $"{instance}/databases/{databaseId}";
        var backup = $"{database}/backups/{backupId}";
        return
        [
            "/", "/instances", "/instances/create", instance, $"{instance}/delete", $"{instance}/external-access",
            $"{instance}/databases", $"{instance}/databases/create", database, $"{database}/delete",
            $"{database}/backups", $"{database}/backups/create", backup, $"{backup}/restore",
            $"{database}/schedule", $"{database}/schedule/edit", $"{database}/schedule/delete",
            "/jobs", "/jobs?status=failed", $"/jobs/{failing}", "/monitoring", "/users"
        ];
    }

    // --- Structure -------------------------------------------------------------------------------

    [Fact]
    public async Task EveryPage_HasOneMainHeading_ItsLandmarks_NamedControls_AndTablesThatSayWhatTheirColumnsAre()
    {
        var pages = await SeededPagesAsync();
        var admin = await SignedInAsync("admin");

        foreach (var path in pages)
        {
            var html = await admin.GetHtmlAsync(path);

            Assert.True(Regex.Matches(html, "<h1[ >]").Count == 1, $"{path} has {Regex.Matches(html, "<h1[ >]").Count} main headings");
            // No level is skipped on the way down.
            var levels = Heading().Matches(html).Select(match => int.Parse(match.Groups[1].Value)).ToList();
            Assert.True(levels.Zip(levels.Skip(1)).All(pair => pair.Second <= pair.First + 1), $"{path} skips a heading level: {string.Join(' ', levels)}");

            Assert.Contains("<a class=\"skip-link\" href=\"#main\">", html, StringComparison.Ordinal);
            Assert.Contains("<main class=\"content\" id=\"main\">", html, StringComparison.Ordinal);
            Assert.Contains("<nav class=\"sidebar\" id=\"sections\" aria-label=\"Sections\">", html, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(html, "aria-current=\"page\""));
            // A page under another says where it is.
            if (path.Count(character => character == '/') > 1 && !path.StartsWith("/jobs?", StringComparison.Ordinal))
            {
                Assert.Contains("<nav class=\"breadcrumbs\" aria-label=\"Breadcrumb\">", html, StringComparison.Ordinal);
            }

            // Every table says what it lists and what its columns are.
            foreach (Match table in Table().Matches(html))
            {
                Assert.Contains("<caption>", table.Value, StringComparison.Ordinal);
                Assert.Contains("<th scope=\"col\">", table.Value, StringComparison.Ordinal);
                Assert.DoesNotContain("<th>", table.Value, StringComparison.Ordinal);
            }

            // Every control a person fills in has a label that is its own.
            foreach (Match control in Control().Matches(html))
            {
                var id = Regex.Match(control.Value, "\\sid=\"([^\"]+)\"").Groups[1].Value;
                Assert.True(id.Length > 0, $"{path}: a control has no id: {control.Value}");
                Assert.True(
                    html.Contains($"for=\"{id}\"", StringComparison.Ordinal) || Regex.IsMatch(html, $"<label[^>]*>\\s*<input[^>]*id=\"{Regex.Escape(id)}\""),
                    $"{path}: the control {id} has no label");
            }

            // Every button and link says what it does, in words.
            Assert.Empty(EmptyButton().Matches(html));
            // Nothing is a button that is not one.
            Assert.DoesNotContain("role=\"button\"", html, StringComparison.Ordinal);
            Assert.DoesNotMatch("<div[^>]*\\sdata-(href|action)=", html);
            // Status is a word with a mark, wherever it appears.
            Assert.All(Badge().Matches(html), badge => Assert.Matches("(?s)<svg[^>]*aria-hidden=\"true\".*?</svg>\\s*\\S", badge.Value));
        }
    }

    [Fact]
    public async Task PagesLoadTheirOwnStylesheetAndScript_AndNothingElse()
    {
        var pages = await SeededPagesAsync();
        var admin = await SignedInAsync("admin");

        foreach (var path in pages)
        {
            var html = await admin.GetHtmlAsync(path);

            var loaded = Loaded().Matches(html).Select(match => WebUtility.HtmlDecode(match.Groups[1].Value).Split('?')[0]).Order().ToList();
            // One stylesheet, one script, one icon; no framework, no font, no library, from anywhere.
            Assert.Equal(["/css/aurora.css", "/favicon.svg", "/js/aurora.js"], loaded);
            Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
            Assert.DoesNotContain("@font-face", html, StringComparison.Ordinal);
        }
    }

    // --- Forms -----------------------------------------------------------------------------------

    [Fact]
    public async Task FieldTheServerRefused_IsMarkedInvalid_AndDescribedByItsMessage_AndOnlyThatField()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "orders");
        var admin = await SignedInAsync("admin");
        var schedulePath = $"/instances/{instanceId}/databases/{databaseId}/schedule/edit";

        var instance = await (await admin.PostFormAsync(
            "/instances/create",
            [new("Input.Name", "orders"), new("Input.EngineVersion", "postgres:16"), new("Input.Cpu", "0"), new("Input.MemoryMb", "512"), new("Input.StorageGb", "1")]))
            .Content.ReadAsStringAsync();
        var database = await (await admin.PostFormAsync($"/instances/{instanceId}/databases/create", [new("Input.Name", "Not Valid")])).Content.ReadAsStringAsync();
        var schedule = await (await admin.PostFormAsync(schedulePath, [new("Input.CronExpression", "whenever"), new("Input.TimeZoneId", "UTC"), new("Input.Enabled", "true")]))
            .Content.ReadAsStringAsync();
        var untouched = await admin.GetHtmlAsync("/instances/create");

        // The refused field: invalid, and described by its hint, if it has one, and then by its message.
        Assert.Contains("aria-invalid=\"true\"", Control(instance, "Input_Cpu"), StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"Input_Cpu-error\"", Control(instance, "Input_Cpu"), StringComparison.Ordinal);
        Assert.Contains("id=\"Input_Cpu-error\">cpu must be between 1 and 256.</span>", instance, StringComparison.Ordinal);
        Assert.Contains("aria-invalid=\"true\"", Control(database, "Input_Name"), StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"name-hint Input_Name-error\"", Control(database, "Input_Name"), StringComparison.Ordinal);
        Assert.Contains("aria-invalid=\"true\"", Control(schedule, "Input_CronExpression"), StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"cron-hint Input_CronExpression-error\"", Control(schedule, "Input_CronExpression"), StringComparison.Ordinal);
        Assert.Contains("id=\"Input_CronExpression-error\">cronExpression must be a cron expression", schedule, StringComparison.Ordinal);

        // The fields that were accepted are not marked, and keep only their hint.
        Assert.DoesNotContain("aria-invalid", Control(instance, "Input_MemoryMb"), StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"memory-hint\"", Control(instance, "Input_MemoryMb"), StringComparison.Ordinal);
        Assert.DoesNotContain("aria-invalid", Control(schedule, "Input_TimeZoneId"), StringComparison.Ordinal);
        // A form that has not been sent has nothing invalid on it, and every message has its place ready.
        Assert.DoesNotContain("aria-invalid", untouched, StringComparison.Ordinal);
        Assert.Contains("id=\"Input_Cpu-error\"", untouched, StringComparison.Ordinal);
        // What is required says so to a person and to the browser.
        Assert.Contains(" required ", Control(untouched, "Input_Name"), StringComparison.Ordinal);
        Assert.Contains("<span class=\"field-required\" aria-hidden=\"true\">*</span>", untouched, StringComparison.Ordinal);
    }

    /// <summary>The tag of the control with that id.</summary>
    private static string Control(string html, string id)
    {
        var tag = Regex.Match(html, $"<(?:input|select|textarea)\\b[^>]*\\sid=\"{Regex.Escape(id)}\"[^>]*>");
        Assert.True(tag.Success, $"There is no control {id}.");
        return tag.Value;
    }

    // --- Words -----------------------------------------------------------------------------------

    [Fact]
    public async Task Pages_UseOneWordForOneThing()
    {
        var pages = await SeededPagesAsync();
        var admin = await SignedInAsync("admin");
        var all = new List<(string Path, string Html)>();
        foreach (var path in pages)
        {
            all.Add((path, await admin.GetHtmlAsync(path)));
        }

        foreach (var (path, html) in all)
        {
            // "Server" is an instance's database server. The machine is "this host", and the application is "Aurora".
            Assert.False(html.Contains("this server", StringComparison.OrdinalIgnoreCase), $"{path} says \"this server\"");
            foreach (var variant in new[] { " DB ", "DBs", "Back-up", "back-up", "Timezone", "timezone", "Time-zone", "Data base", "Log in", "Login<", "Sign-in<" })
            {
                Assert.False(html.Contains(variant, StringComparison.Ordinal), $"{path} says \"{variant.Trim()}\"");
            }

            // Going to a row's page is "Open", wherever the row is.
            Assert.DoesNotContain(">Details<span class=\"visually-hidden\">", html, StringComparison.Ordinal);
            Assert.DoesNotContain(">View<", html, StringComparison.Ordinal);
            // Every instant is written the same way, in UTC and saying so.
            Assert.All(Time().Matches(html), time => Assert.Matches("^<time datetime=\"\\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\dZ\">\\d{4}-\\d\\d-\\d\\d \\d\\d:\\d\\d UTC</time>$", time.Value));
        }

        // What creates is "Create …", what removes is "Delete …"; nothing is "Add", "New", "Remove" or "Destroy".
        var labels = all.SelectMany(page => ButtonLabel().Matches(page.Html).Select(match => Regex.Replace(match.Groups[1].Value, "<[^>]+>|\\s+", " ").Trim())).Distinct().ToList();
        Assert.Contains(labels, label => label.StartsWith("Create ", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.StartsWith("Delete", StringComparison.Ordinal));
        Assert.DoesNotContain(labels, label => Regex.IsMatch(label, "^(Add|New|Remove|Destroy|Drop|Submit|OK|Yes|No)\\b"));
    }

    // --- Roles -----------------------------------------------------------------------------------

    [Fact]
    public async Task Viewer_IsOfferedNoChange_OnAnyPageTheyMaySee()
    {
        var pages = await SeededPagesAsync();
        var viewer = await SignedInAsync("viewer");
        string[] changing = ["/create", "/delete", "/edit", "/restore", "/external-access"];

        foreach (var path in pages)
        {
            var response = await viewer.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();

            if (changing.Any(suffix => path.EndsWith(suffix, StringComparison.Ordinal)) || path == "/users")
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                continue;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // No link to a page that changes something, and no form but the one that signs out.
            Assert.All(changing, suffix => Assert.DoesNotMatch($"href=\"[^\"]*{Regex.Escape(suffix)}\"", html));
            Assert.All(FormTag().Matches(html), form => Assert.True(
                form.Value.Contains("action=\"/logout\"", StringComparison.Ordinal) || form.Value.Contains("class=\"filters\"", StringComparison.Ordinal),
                $"{path} offers a viewer a form: {form.Value}"));
        }
    }

    // --- The stylesheet and the script, as far as their meaning goes --------------------------------

    [Fact]
    public async Task DarkTheme_DefinesEveryColourTheLightThemeDoes()
    {
        using var browser = _factory.CreateBrowser();
        var styles = await (await browser.GetAsync("/css/aurora.css")).Content.ReadAsStringAsync();

        var light = Regex.Match(styles, ":root\\s*\\{(.*?)\\n\\}", RegexOptions.Singleline).Groups[1].Value;
        var dark = Regex.Match(styles, "@media \\(prefers-color-scheme: dark\\)\\s*\\{\\s*:root\\s*\\{(.*?)\\}", RegexOptions.Singleline).Groups[1].Value;
        var lightColours = ColourToken().Matches(light).Select(match => match.Groups[1].Value).ToHashSet();
        var darkColours = Token().Matches(dark).Select(match => match.Groups[1].Value).ToHashSet();

        Assert.True(lightColours.Count > 20);
        // A colour the dark theme forgot would be the light one, on a dark page.
        Assert.Empty(lightColours.Except(darkColours));
        // And no colour is written anywhere but in the two themes: everything else names a token.
        var rules = styles[(styles.IndexOf("/* 2. Base", StringComparison.Ordinal))..];
        Assert.Empty(Regex.Matches(rules, "#[0-9a-fA-F]{3,8}\\b").Select(match => match.Value));
    }

    [Fact]
    public async Task NarrowScreens_GetTablesARowAtATime_OnlyOnceTheScriptHasNamedTheColumns()
    {
        using var browser = _factory.CreateBrowser();
        var styles = await (await browser.GetAsync("/css/aurora.css")).Content.ReadAsStringAsync();
        var script = await (await browser.GetAsync("/js/aurora.js")).Content.ReadAsStringAsync();

        // The stacked layout is only ever inside the narrow-screen rules, and only for tables the script marked.
        var narrow = styles[(styles.IndexOf("@media (max-width: 52rem)", StringComparison.Ordinal))..];
        Assert.Contains(".table-stack td::before", narrow, StringComparison.Ordinal);
        Assert.Contains("content: attr(data-label);", narrow, StringComparison.Ordinal);
        Assert.DoesNotContain(".table-stack", styles[..styles.IndexOf("@media (max-width: 52rem)", StringComparison.Ordinal)], StringComparison.Ordinal);
        // Without the script a table stays a table, inside a frame that scrolls.
        Assert.Matches("\\.table-wrap \\{[^}]*overflow-x: auto;", styles);
        // The script that marks a table also says what its parts are, since the layout no longer does.
        Assert.Contains("table.classList.add(\"table-stack\")", script, StringComparison.Ordinal);
        Assert.Contains("cell.dataset.label = names[index]", script, StringComparison.Ordinal);
        foreach (var role in new[] { "\"table\"", "\"rowgroup\"", "\"row\"", "\"columnheader\"", "\"cell\"" })
        {
            Assert.Contains($"setAttribute(\"role\", {role})", script, StringComparison.Ordinal);
        }

        // Focus is always shown, and motion is only the one spinner, which stops for those who ask.
        Assert.Matches(":focus-visible \\{[^}]*outline: 2px solid var\\(--focus\\);", styles);
        Assert.DoesNotMatch("outline:\\s*(none|0)\\b", styles);
        Assert.Single(Regex.Matches(styles, "@keyframes"));
        Assert.Contains("@media (prefers-reduced-motion: reduce)", styles, StringComparison.Ordinal);
    }

    [GeneratedRegex("<h([1-4])[ >]")]
    private static partial Regex Heading();

    [GeneratedRegex("<table.*?</table>", RegexOptions.Singleline)]
    private static partial Regex Table();

    [GeneratedRegex("<(?:input(?![^>]*type=\"hidden\")|select|textarea)\\b[^>]*>")]
    private static partial Regex Control();

    [GeneratedRegex("<(?:a|button)\\b[^>]*>\\s*</(?:a|button)>")]
    private static partial Regex EmptyButton();

    [GeneratedRegex("<span class=\"badge badge-[a-z]+\">.*?</span>", RegexOptions.Singleline)]
    private static partial Regex Badge();

    [GeneratedRegex("<(?:script|link|img)[^>]+(?:src|href)=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex Loaded();

    [GeneratedRegex("<time[^>]*>[^<]*</time>")]
    private static partial Regex Time();

    [GeneratedRegex("<(?:a class=\"button[^\"]*\"[^>]*|button[^>]*)>(.*?)</(?:a|button)>", RegexOptions.Singleline)]
    private static partial Regex ButtonLabel();

    [GeneratedRegex("<form[^>]*>")]
    private static partial Regex FormTag();

    // A token whose value is a colour.
    [GeneratedRegex("(--[a-z-]+):\\s*#")]
    private static partial Regex ColourToken();

    [GeneratedRegex("(--[a-z-]+):")]
    private static partial Regex Token();
}
