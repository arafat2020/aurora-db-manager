using System.Net;
using System.Text.RegularExpressions;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Ui;

/// <summary>
/// The pages for a database's backup schedule, for jobs, and for monitoring, through the real
/// Web host: real sign-ins and antiforgery tokens, and the real schedule service, calculator,
/// scheduler, job service, health checks and monitoring summary behind the pages. The clock is
/// the test's, so every time on a page is one the test knows; the scheduler makes a pass when
/// the test says so.
/// </summary>
public sealed partial class UiSchedulingAndMonitoringTests : IDisposable
{
    private const string UserPassword = "a-perfectly-fine-password";

    // A Monday, noon UTC: 18:00 in Dhaka, 14:00 in Berlin.
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly List<IDisposable> _disposables = [];
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly WebFactory _factory;
    private readonly HttpClient _api;

    public UiSchedulingAndMonitoringTests()
    {
        _factory = Track(new WebFactory { BootstrapAdmin = (Browser.Admin, Browser.Password), Clock = _clock });
        _api = Track(_factory.CreateClientAs(UserRole.Admin));
    }

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }
    }

    private T Track<T>(T disposable)
        where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    private async Task<HttpClient> SignedInAsync(string role)
    {
        var browser = Track(_factory.CreateBrowser());
        if (role == "admin")
        {
            await Browser.SignInAsync(browser);
        }
        else
        {
            if (!await _factory.WithDbAsync(db => db.Users.AnyAsync(user => user.Username == $"the-{role}")))
            {
                await _api.CreateUserAsync($"the-{role}", UserPassword, role);
            }

            await Browser.SignInAsync(browser, $"the-{role}", UserPassword);
        }

        return browser;
    }

    private static string Flat(string html) => Regex.Replace(html, "\\s+", " ");

    private static async Task<string> HtmlOf(HttpResponseMessage response) => Flat(await response.Content.ReadAsStringAsync());

    private sealed record Target(Guid InstanceId, Guid DatabaseId)
    {
        public string Database => $"/instances/{InstanceId}/databases/{DatabaseId}";

        public string Schedule => $"{Database}/schedule";

        public string Edit => $"{Schedule}/edit";

        public string Delete => $"{Schedule}/delete";
    }

    private async Task<Target> DatabaseAsync(string name = "orders")
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_api);
        return new Target(instanceId, await _factory.CreateReadyDatabaseAsync(_api, instanceId, name));
    }

    private static KeyValuePair<string, string>[] ScheduleForm(string cron = "0 0 * * *", string zone = "Asia/Dhaka", bool enabled = true) =>
        enabled
            ? [new("Input.CronExpression", cron), new("Input.TimeZoneId", zone), new("Input.Enabled", "true")]
            // An unticked box is not sent, as a browser does not send it.
            : [new("Input.CronExpression", cron), new("Input.TimeZoneId", zone)];

    private async Task ScheduledAsync(Target target, string cron = "0 0 * * *", string zone = "Asia/Dhaka", bool enabled = true)
    {
        var response = await _api.PostAsync(
            $"{DatabasesUrl}/{target.DatabaseId}/backup-schedule",
            System.Net.Http.Json.JsonContent.Create(new { cronExpression = cron, timeZoneId = zone, enabled }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private Task<(string Cron, string Zone, bool Enabled, DateTime? NextRunAt)?> RecordAsync(Guid databaseId) => _factory.WithDbAsync(async db =>
    {
        var schedule = await db.BackupSchedules.AsNoTracking().SingleOrDefaultAsync(s => s.DatabaseId == databaseId);
        return schedule is null
            ? ((string, string, bool, DateTime?)?)null
            : (schedule.CronExpression, schedule.TimeZoneId, schedule.Enabled, schedule.NextRunAt);
    });

    private static string Section(string html, string id) =>
        Regex.Match(html, $"<section class=\"section\" aria-labelledby=\"{id}\">.*?</section>").Value;

    // === The schedule page =======================================================================

    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task DatabaseWithoutASchedule_SaysSo_AndOffersOneToThoseWhoMay(string role)
    {
        var target = await DatabaseAsync();
        var browser = await SignedInAsync(role);

        var html = Flat(await browser.GetHtmlAsync(target.Schedule));

        Assert.Contains("<h1>Backup schedule</h1>", html, StringComparison.Ordinal);
        Assert.Contains("When the database orders, in production-db, is backed up by itself.", html, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"{target.Database}\">orders</a>", html, StringComparison.Ordinal);
        Assert.Contains("<h2>No backup schedule configured</h2>", html, StringComparison.Ordinal);
        Assert.Equal(role != "viewer", html.Contains("Create a schedule to automatically back up this database.", StringComparison.Ordinal));
        Assert.Equal(role != "viewer" ? 2 : 0, Regex.Matches(html, $"href=\"{target.Edit}\"").Count);
        // One schedule per database: there is no list, and no adding another.
        Assert.DoesNotContain("<table", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task Schedule_IsShownAsTheServiceHasIt_WithTheNextRunItCalculated(string role)
    {
        var target = await DatabaseAsync();
        await ScheduledAsync(target, "0 0 * * *", "Asia/Dhaka");
        var fromApi = await (await _api.GetAsync($"{DatabasesUrl}/{target.DatabaseId}/backup-schedule")).ReadJsonAsync(HttpStatusCode.OK);
        var browser = await SignedInAsync(role);

        var html = Flat(await browser.GetHtmlAsync(target.Schedule));

        Assert.Matches("<h1>Backup schedule</h1> <span class=\"badge badge-success\"><svg.*?</svg> Enabled</span>", html);
        Assert.Contains("<dt>Cron</dt> <dd><code>0 0 * * *</code>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Time zone</dt> <dd><span class=\"mono\">Asia/Dhaka</span>", html, StringComparison.Ordinal);
        // Midnight in Dhaka is 18:00 UTC: the instant the service calculated, and that instant on the zone's clock.
        Assert.Equal("2026-10-05T18:00:00Z", fromApi.GetProperty("nextRunAt").GetString());
        Assert.Contains(
            "<dt>Next run</dt> <dd> <time datetime=\"2026-10-05T18:00:00Z\">2026-10-05 18:00 UTC</time> <span class=\"muted\">which is 2026-10-06 00:00 in Asia/Dhaka</span> </dd>",
            html,
            StringComparison.Ordinal);
        Assert.Contains("<dt>Last updated</dt> <dd> <time datetime=\"2026-10-05T12:00:00Z\">", html, StringComparison.Ordinal);
        Assert.Contains($"<dt>ID</dt> <dd class=\"mono\">{fromApi.GetProperty("id").GetGuid()}</dd>", html, StringComparison.Ordinal);
        Assert.Contains("A backup the schedule causes is an ordinary backup", html, StringComparison.Ordinal);

        // Who may change it is offered the ways to; a viewer is offered none.
        var mayChange = role != "viewer";
        Assert.Equal(mayChange, html.Contains($"<a class=\"button\" href=\"{target.Edit}\">Edit schedule</a>", StringComparison.Ordinal));
        Assert.Equal(mayChange, html.Contains($"action=\"{target.Edit}?handler=Disable\"", StringComparison.Ordinal));
        Assert.Equal(mayChange, html.Contains($"href=\"{target.Delete}\"", StringComparison.Ordinal));
        Assert.DoesNotContain("Create schedule", html, StringComparison.Ordinal);
        // Nothing is said about a last scheduled backup: nothing on record tells one from a backup asked for by hand.
        Assert.DoesNotContain("Last scheduled", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Last run", html, StringComparison.Ordinal);
    }

    // === Creating ================================================================================

    [Theory]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task OperatorAndAdmin_CreateASchedule_WhichBacksNothingUpYet(string role)
    {
        var target = await DatabaseAsync();
        var browser = await SignedInAsync(role);

        var form = Flat(await browser.GetHtmlAsync(target.Edit));
        await browser.GetAsync($"{target.Edit}?handler=Enable&Input.CronExpression=0+0+*+*+*&Input.TimeZoneId=UTC");
        Assert.Null(await RecordAsync(target.DatabaseId));

        Assert.Contains("<h1>Create backup schedule</h1>", form, StringComparison.Ordinal);
        Assert.Contains("<label class=\"field-label\" for=\"Input_CronExpression\">", form, StringComparison.Ordinal);
        Assert.Contains("<label class=\"field-label\" for=\"Input_TimeZoneId\">", form, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"cron-hint\"", form, StringComparison.Ordinal);
        Assert.Contains("<code>0 0 * * *</code> is every day at midnight", form, StringComparison.Ordinal);
        Assert.Contains("The server checks the expression when the schedule is saved.", form, StringComparison.Ordinal);
        Assert.Contains("It is never taken from your browser or from the server", form, StringComparison.Ordinal);
        // Suggestions for the time zone, as the server knows them; a text field, so any IANA name can be typed.
        Assert.Contains("list=\"time-zones\"", form, StringComparison.Ordinal);
        Assert.Contains("<option value=\"UTC\"></option>", form, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Asia/Dhaka\"></option>", form, StringComparison.Ordinal);
        Assert.Contains("<input type=\"checkbox\" name=\"Input.Enabled\" id=\"Input_Enabled\" value=\"true\" checked=\"checked\"", form, StringComparison.Ordinal);
        Assert.Contains("data-busy-label=\"Saving…\">Create schedule</button>", form, StringComparison.Ordinal);
        Assert.Contains($"<a class=\"button\" href=\"{target.Schedule}\">Cancel</a>", form, StringComparison.Ordinal);

        var response = await browser.PostFormAsync(target.Edit, ScheduleForm("30 2 * * *", "Europe/Berlin"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(target.Schedule, response.Headers.Location!.OriginalString);
        // 02:30 in Berlin, in October, is 00:30 UTC: worked out by the service, not by the page.
        Assert.Equal(("30 2 * * *", "Europe/Berlin", true, new DateTime(2026, 10, 6, 0, 30, 0, DateTimeKind.Utc)), await RecordAsync(target.DatabaseId));
        var html = Flat(await browser.GetHtmlAsync(target.Schedule));
        Assert.Matches("<div class=\"alert alert-success\" role=\"status\">.*?Backup schedule created\\.", html);
        Assert.Contains("<code>30 2 * * *</code>", html, StringComparison.Ordinal);
        Assert.Contains("<time datetime=\"2026-10-06T00:30:00Z\">2026-10-06 00:30 UTC</time> <span class=\"muted\">which is 2026-10-06 02:30 in Europe/Berlin</span>", html, StringComparison.Ordinal);
        // A schedule was saved. No backup was made, and none is claimed.
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
        Assert.DoesNotContain("backed up successfully", html, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await _factory.RunSchedulerAsync());
    }

    [Fact]
    public async Task ScheduleCreatedDisabled_IsKept_AndHasNoNextRun()
    {
        var target = await DatabaseAsync();
        var op = await SignedInAsync("operator");

        var response = await op.PostFormAsync(target.Edit, ScheduleForm(enabled: false));
        var html = Flat(await op.GetHtmlAsync(response.Headers.Location!.OriginalString));

        Assert.Equal(("0 0 * * *", "Asia/Dhaka", false, null), await RecordAsync(target.DatabaseId));
        Assert.Matches("<h1>Backup schedule</h1> <span class=\"badge badge-neutral\"><svg.*?</svg> Disabled</span>", html);
        Assert.Contains("<dt>Next run</dt> <dd> <span>Disabled</span> <span class=\"muted\">A disabled schedule has no next run.</span> </dd>", html, StringComparison.Ordinal);
        Assert.Contains($"action=\"{target.Edit}?handler=Enable\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "UTC", "Input.CronExpression", "cronExpression must be a cron expression of five fields: minute, hour, day of month, month, day of week.")]
    [InlineData("every night", "UTC", "Input.CronExpression", "cronExpression must be a cron expression of five fields")]
    [InlineData("0 0 * *", "UTC", "Input.CronExpression", "cronExpression must be a cron expression of five fields")]
    [InlineData("0 0 * * * *", "UTC", "Input.CronExpression", "cronExpression must be a cron expression of five fields")]
    [InlineData("61 0 * * *", "UTC", "Input.CronExpression", "cronExpression must be a cron expression of five fields")]
    [InlineData("0 0 31 2 *", "UTC", "Input.CronExpression", "cronExpression must be a cron expression of five fields")]
    [InlineData("0 0 * * *", "", "Input.TimeZoneId", "timeZoneId must be the IANA name of a time zone, such as &#x27;Asia/Dhaka&#x27; or &#x27;UTC&#x27;.")]
    [InlineData("0 0 * * *", "Dhaka", "Input.TimeZoneId", "timeZoneId must be the IANA name of a time zone")]
    [InlineData("0 0 * * *", "+06:00", "Input.TimeZoneId", "timeZoneId must be the IANA name of a time zone")]
    [InlineData("0 0 * * *", "<script>alert(1)</script>", "Input.TimeZoneId", "timeZoneId must be the IANA name of a time zone")]
    public async Task WhatTheCalculatorDoesNotAccept_IsRefusedAtItsField_KeptInTheForm_AndNothingIsSaved(string cron, string zone, string field, string message)
    {
        var fresh = await DatabaseAsync(name: "fresh");
        var scheduled = new Target(fresh.InstanceId, await _factory.CreateReadyDatabaseAsync(_api, fresh.InstanceId, "scheduled"));
        await ScheduledAsync(scheduled, "15 3 * * *", "UTC");
        var before = await RecordAsync(scheduled.DatabaseId);
        var op = await SignedInAsync("operator");

        foreach (var target in new[] { fresh, scheduled })
        {
            var response = await op.PostFormAsync(target.Edit, ScheduleForm(cron, zone));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var html = await HtmlOf(response);
            Assert.Matches($"<span class=\"field-error field-validation-error\" data-valmsg-for=\"{Regex.Escape(field)}\" data-valmsg-replace=\"true\" id=\"[A-Za-z_]+-error\">{Regex.Escape(message)}", html);
            // What was typed is still there, as text.
            Assert.Contains($"value=\"{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(cron)}\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        }

        Assert.Null(await RecordAsync(fresh.DatabaseId));
        Assert.Equal(before, await RecordAsync(scheduled.DatabaseId));
    }

    [Fact]
    public async Task CreatingASchedule_ForADatabaseThatCannotBeBackedUp_IsRefusedWithTheStableCode()
    {
        var ready = await DatabaseAsync();
        var (creatingId, _) = await _api.CreateDatabaseAsync(ready.InstanceId, "being_created");
        var creating = new Target(ready.InstanceId, creatingId);
        var op = await SignedInAsync("operator");

        var warned = await op.GetHtmlAsync(creating.Edit);
        var notReady = await op.PostFormAsync(creating.Edit, ScheduleForm());
        await _factory.SetInstanceStatusAsync(ready.InstanceId, InstanceStatus.Stopped);
        var stopped = await op.PostFormAsync(ready.Edit, ScheduleForm(), tokenFrom: "/instances");

        Assert.Contains("A schedule can be given only to a ready database in a running instance.", warned, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
        Assert.Contains("<div class=\"alert-title\">DATABASE_NOT_READY</div> <div>Only a ready database can be given a backup schedule.</div>", await HtmlOf(notReady), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);
        var html = await HtmlOf(stopped);
        Assert.Contains("<div class=\"alert-title\">INSTANCE_NOT_READY</div> <div>Backup schedules need a running instance.</div>", html, StringComparison.Ordinal);
        Assert.Contains("value=\"0 0 * * *\"", html, StringComparison.Ordinal);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.BackupSchedules.CountAsync()));
        // What cannot be given a schedule is not offered one.
        Assert.DoesNotContain("/schedule/edit", await op.GetHtmlAsync(creating.Schedule), StringComparison.Ordinal);
    }

    // === Changing ================================================================================

    [Theory]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task OperatorAndAdmin_EditASchedule_StartingFromWhatItSays_AndTheNextRunIsWorkedOutAnew(string role)
    {
        var target = await DatabaseAsync();
        await ScheduledAsync(target, "0 0 * * *", "Asia/Dhaka");
        var browser = await SignedInAsync(role);

        var form = Flat(await browser.GetHtmlAsync(target.Edit));
        var response = await browser.PostFormAsync(target.Edit, ScheduleForm("0 6 * * 1", "UTC"));

        Assert.Contains("<h1>Edit backup schedule</h1>", form, StringComparison.Ordinal);
        // The current values are in the form: nothing has to be typed again.
        Assert.Matches("<input class=\"input mono\"[^>]*id=\"Input_CronExpression\"[^>]*value=\"0 0 \\* \\* \\*\"", form);
        Assert.Matches("<input class=\"input mono\"[^>]*id=\"Input_TimeZoneId\"[^>]*value=\"Asia/Dhaka\"", form);
        Assert.Contains("checked=\"checked\"", form, StringComparison.Ordinal);
        Assert.Contains(">Save schedule</button>", form, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        // Mondays at 06:00 UTC: this Monday's has passed, so the next one.
        Assert.Equal(("0 6 * * 1", "UTC", true, new DateTime(2026, 10, 12, 6, 0, 0, DateTimeKind.Utc)), await RecordAsync(target.DatabaseId));
        var html = Flat(await browser.GetHtmlAsync(target.Schedule));
        Assert.Matches("<div class=\"alert alert-success\" role=\"status\">.*?Backup schedule updated\\.", html);
        Assert.Contains("<time datetime=\"2026-10-12T06:00:00Z\">2026-10-12 06:00 UTC</time>", html, StringComparison.Ordinal);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
        Assert.Equal(1, await _factory.WithDbAsync(db => db.BackupSchedules.CountAsync()));
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task DisablingAndEnabling_KeepTheSchedule_AndTheNextRunIsTheServices(string role)
    {
        var target = await DatabaseAsync();
        await ScheduledAsync(target, "0 0 * * *", "Asia/Dhaka");
        var browser = await SignedInAsync(role);

        var disabled = await browser.PostFormAsync($"{target.Edit}?handler=Disable", [], tokenFrom: target.Schedule);
        var whenDisabled = Flat(await browser.GetHtmlAsync(target.Schedule));

        Assert.Equal(HttpStatusCode.Redirect, disabled.StatusCode);
        Assert.Equal(target.Schedule, disabled.Headers.Location!.OriginalString);
        // Only the flag changed; a disabled schedule has no next run.
        Assert.Equal(("0 0 * * *", "Asia/Dhaka", false, null), await RecordAsync(target.DatabaseId));
        Assert.Matches("<div class=\"alert alert-success\" role=\"status\">.*?Backup schedule disabled\\.", whenDisabled);
        Assert.Matches("</h1> <span class=\"badge badge-neutral\"><svg.*?</svg> Disabled</span>", whenDisabled);
        Assert.Contains("<dt>Next run</dt> <dd> <span>Disabled</span>", whenDisabled, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-10-05 18:00 UTC", whenDisabled, StringComparison.Ordinal);
        Assert.Contains($"action=\"{target.Edit}?handler=Enable\"", whenDisabled, StringComparison.Ordinal);

        // Seven hours later, past the occurrence it would have had, it is enabled again: it starts
        // from the next occurrence after that moment, and does not make up for the one it missed.
        _clock.Advance(TimeSpan.FromHours(7));
        var enabled = await browser.PostFormAsync($"{target.Edit}?handler=Enable", [], tokenFrom: target.Schedule);
        var whenEnabled = Flat(await browser.GetHtmlAsync(target.Schedule));

        Assert.Equal(HttpStatusCode.Redirect, enabled.StatusCode);
        Assert.Equal(("0 0 * * *", "Asia/Dhaka", true, new DateTime(2026, 10, 6, 18, 0, 0, DateTimeKind.Utc)), await RecordAsync(target.DatabaseId));
        Assert.Matches("<div class=\"alert alert-success\" role=\"status\">.*?Backup schedule enabled\\.", whenEnabled);
        Assert.Contains("<time datetime=\"2026-10-06T18:00:00Z\">2026-10-06 18:00 UTC</time> <span class=\"muted\">which is 2026-10-07 00:00 in Asia/Dhaka</span>", whenEnabled, StringComparison.Ordinal);
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
        Assert.Empty(await _factory.RunSchedulerAsync());
    }

    // === Deleting ================================================================================

    [Theory]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task OperatorAndAdmin_DeleteASchedule_OnlyByPostingTheConfirmation(string role)
    {
        var target = await DatabaseAsync();
        await ScheduledAsync(target);
        var backupId = await _factory.CreateCompletedBackupAsync(_api, target.DatabaseId);
        var browser = await SignedInAsync(role);

        var question = Flat(await browser.GetHtmlAsync(target.Delete));
        await browser.GetAsync($"{target.Delete}?handler=Post&confirm=true");
        Assert.NotNull(await RecordAsync(target.DatabaseId));

        Assert.Contains("<h1>Delete backup schedule?</h1>", question, StringComparison.Ordinal);
        Assert.Contains("<dt>Database</dt> <dd><strong class=\"mono\">orders</strong> <span class=\"muted\">in production-db</span></dd>", question, StringComparison.Ordinal);
        Assert.Contains("<dt>Cron</dt> <dd><code>0 0 * * *</code></dd> <dt>Time zone</dt> <dd class=\"mono\">Asia/Dhaka</dd>", question, StringComparison.Ordinal);
        Assert.Contains("<strong>Automatic backups of this database stop.</strong>", question, StringComparison.Ordinal);
        Assert.Contains("Backups that exist are kept", question, StringComparison.Ordinal);
        Assert.Contains($"<a class=\"button\" href=\"{target.Schedule}\">Cancel</a>", question, StringComparison.Ordinal);
        Assert.Contains("data-busy-label=\"Deleting…\">Delete schedule</button>", question, StringComparison.Ordinal);

        var response = await browser.PostFormAsync(target.Delete, []);
        var html = Flat(await browser.GetHtmlAsync(response.Headers.Location!.OriginalString));

        Assert.Equal(target.Schedule, response.Headers.Location!.OriginalString);
        Assert.Null(await RecordAsync(target.DatabaseId));
        Assert.Matches("<div class=\"alert alert-success\" role=\"status\">.*?Backup schedule deleted\\. The database is no longer backed up automatically\\.", html);
        Assert.Contains("<h2>No backup schedule configured</h2>", html, StringComparison.Ordinal);
        // The backup that existed is still there.
        Assert.Equal("completed", (await _api.GetBackupAsync(backupId)).Status());

        // What is not there cannot be deleted, enabled or disabled.
        foreach (var gone in new[]
                 {
                     await browser.GetAsync(target.Delete),
                     await browser.PostFormAsync(target.Delete, [], tokenFrom: target.Schedule),
                     await browser.PostFormAsync($"{target.Edit}?handler=Disable", [], tokenFrom: target.Schedule)
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            Assert.Contains("<h1>Backup schedule not found</h1>", await gone.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    // === Who may, and how ========================================================================

    [Fact]
    public async Task Viewer_SeesTheSchedule_AndCanChangeNothingOfIt_WhateverTheyAskFor()
    {
        var target = await DatabaseAsync();
        await ScheduledAsync(target);
        var before = await RecordAsync(target.DatabaseId);
        var viewer = await SignedInAsync("viewer");

        var responses = new[]
        {
            await viewer.GetAsync(target.Edit),
            await viewer.GetAsync(target.Delete),
            // With a genuine antiforgery token of their own session: it is the policy that refuses.
            await viewer.PostFormAsync(target.Edit, ScheduleForm("* * * * *", "UTC"), tokenFrom: target.Schedule),
            await viewer.PostFormAsync($"{target.Edit}?handler=Disable", [], tokenFrom: target.Schedule),
            await viewer.PostFormAsync($"{target.Edit}?handler=Enable", [], tokenFrom: target.Schedule),
            await viewer.PostFormAsync(target.Delete, [], tokenFrom: target.Schedule)
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("You don&#x27;t have permission", html, StringComparison.Ordinal);
            Assert.Contains("Request ID:", html, StringComparison.Ordinal);
        }

        Assert.Equal(before, await RecordAsync(target.DatabaseId));
    }

    [Fact]
    public async Task ChangingASchedule_NeedsTheAntiforgeryToken_AndASignIn()
    {
        var target = await DatabaseAsync();
        await ScheduledAsync(target);
        var before = await RecordAsync(target.DatabaseId);
        var admin = await SignedInAsync("admin");
        var anonymous = Track(_factory.CreateBrowser());

        var responses = new[]
        {
            await admin.PostFormAsync(target.Edit, ScheduleForm("* * * * *", "UTC"), withToken: false),
            await admin.PostFormAsync($"{target.Edit}?handler=Disable", [], withToken: false),
            await admin.PostFormAsync(target.Delete, [], withToken: false),
            await admin.PostAsync(target.Delete, new FormUrlEncodedContent([new("__RequestVerificationToken", "CfDJ8forged")]))
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Request ID:", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        foreach (var path in new[] { target.Schedule, target.Edit, target.Delete, "/monitoring", "/jobs", $"/jobs/{Guid.NewGuid()}" })
        {
            var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/login?returnUrl=", response.Headers.Location!.PathAndQuery, StringComparison.Ordinal);
        }

        Assert.Equal(before, await RecordAsync(target.DatabaseId));
    }

    [Fact]
    public void SchedulePages_HaveThePoliciesOfTheApi_AndThereIsNoTopLevelSchedulesPage()
    {
        var pages = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<PageActionDescriptor>() is not null)
            .GroupBy(endpoint => "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Select(data => data.Policy).Distinct().SingleOrDefault());
        const string schedule = "/instances/{id:guid}/databases/{databaseId:guid}/schedule";

        Assert.Null(pages[schedule]);
        Assert.Equal("operator", pages[$"{schedule}/edit"]);
        Assert.Equal("operator", pages[$"{schedule}/delete"]);
        Assert.Null(pages["/jobs"]);
        Assert.Null(pages["/jobs/{id:guid}"]);
        Assert.Null(pages["/monitoring"]);
        Assert.DoesNotContain("/schedules", pages.Keys);
    }

    [Fact]
    public async Task WhatIsNotThere_IsNotFound_AndAScheduleIsOnlyFoundUnderItsOwnDatabase()
    {
        var target = await DatabaseAsync();
        var elsewhere = await DatabaseAsync(name: "elsewhere");
        var admin = await SignedInAsync("admin");
        var nobody = Guid.CreateVersion7();

        foreach (var (path, missing) in new[]
                 {
                     (new Target(target.InstanceId, nobody).Schedule, "Database"),
                     (new Target(target.InstanceId, nobody).Edit, "Database"),
                     (new Target(nobody, target.DatabaseId).Schedule, "Instance"),
                     (new Target(elsewhere.InstanceId, target.DatabaseId).Schedule, "Database"),
                     (target.Delete, "Backup schedule"),
                     ($"/jobs/{nobody}", "Job")
                 })
        {
            var response = await admin.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains($"<h1>{missing} not found</h1>", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/schedules")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/jobs/not-an-id")).StatusCode);
    }

    // === The scheduler, and what a schedule leads to =============================================

    [Fact]
    public async Task WhenAScheduleIsDue_TheSchedulerMakesAnOrdinaryBackup_AndThePagesShowTheScheduleMovedOn()
    {
        var target = await DatabaseAsync();
        var op = await SignedInAsync("operator");
        await op.PostFormAsync(target.Edit, ScheduleForm("0 0 * * *", "Asia/Dhaka"));

        // Half a minute past midnight in Dhaka.
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 5, 18, 0, 30, TimeSpan.Zero));
        var jobs = await _factory.RunSchedulerAsync();
        var jobId = Assert.Single(jobs);
        var schedule = Flat(await op.GetHtmlAsync(target.Schedule));
        var backups = Flat(await op.GetHtmlAsync($"{target.Database}/backups"));
        var database = Flat(await op.GetHtmlAsync(target.Database));
        await _factory.ProcessJobAsync(jobId);
        var done = Flat(await op.GetHtmlAsync($"{target.Database}/backups"));
        var job = Flat(await op.GetHtmlAsync($"/jobs/{jobId}"));

        // The schedule owes its next occurrence, a day later; the backup it caused is among the backups.
        Assert.Contains("<time datetime=\"2026-10-06T18:00:00Z\">2026-10-06 18:00 UTC</time>", schedule, StringComparison.Ordinal);
        Assert.Matches("<span class=\"badge badge-progress\"><svg.*?</svg> Pending</span>", backups);
        Assert.Matches("<span class=\"badge badge-success\"><svg.*?</svg> Completed</span>", done);
        Assert.Matches("</svg> Verified</span>", done);
        Assert.Matches("<h1>Back up database</h1> <span class=\"badge badge-success\"><svg.*?</svg> Completed</span>", job);
        // The database page: its schedule in a line, and the way to it.
        Assert.Matches("<strong>Schedule:</strong> <span class=\"badge badge-success\"><svg.*?</svg> Enabled</span> <code>0 0 \\* \\* \\*</code> <span class=\"mono\">Asia/Dhaka</span>", database);
        Assert.Contains("next run <time datetime=\"2026-10-06T18:00:00Z\">", database, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"{target.Schedule}\">View schedule</a>", database, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabasePage_ShowsItsInstancesHealth_AndSaysThatIsWhatItIs()
    {
        // The real provisioner and the real runtime probe, over the in-memory Docker Engine.
        using var factory = new WebFactory { BootstrapAdmin = (Browser.Admin, Browser.Password), UseDockerProvisioner = true };
        using var api = factory.CreateClientAs(UserRole.Admin);
        using var browser = factory.CreateBrowser();
        await Browser.SignInAsync(browser);
        var instanceId = await factory.CreateRunningInstanceAsync(api);
        var databaseId = await factory.CreateReadyDatabaseAsync(api, instanceId, "orders");
        var path = $"/instances/{instanceId}/databases/{databaseId}";

        var healthy = Flat(await browser.GetHtmlAsync(path));
        factory.Docker.Exec = () => 1;
        var degraded = Flat(await browser.GetHtmlAsync(path));

        Assert.Matches("<dt>Instance health</dt> <dd> <span class=\"badge badge-success\"><svg.*?</svg> Healthy</span>", healthy);
        Assert.Contains("This is the health of the server the database is in; a single database has no check of its own.", healthy, StringComparison.Ordinal);
        Assert.Matches("<dt>Instance health</dt> <dd> <span class=\"badge badge-warning\"><svg.*?</svg> Degraded</span>", degraded);
        Assert.DoesNotContain("Database health", healthy, StringComparison.Ordinal);
        Assert.Contains("<strong>Schedule:</strong> <span class=\"muted\">No backup schedule configured.</span>", healthy, StringComparison.Ordinal);
    }

    // === Jobs ====================================================================================

    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task EverySignedInUser_SeesTheJobs_WithWhatEachIs_HowItWent_AndWhatItWorkedOn(string role)
    {
        var target = await DatabaseAsync();
        var backupId = await _factory.CreateCompletedBackupAsync(_api, target.DatabaseId);
        var (_, failing) = await _api.CreateBackupAsync(target.DatabaseId);
        _factory.DumpTools.FailAllRuns("raw-tool-detail: FATAL password authentication failed");
        await _factory.ProcessJobAsync(failing);
        var (pendingInstance, pending) = await _api.CreateInstanceAsync(name: "still-provisioning");
        var browser = await SignedInAsync(role);

        var html = Flat(await browser.GetHtmlAsync("/jobs"));

        Assert.Contains("<caption>Jobs, newest first</caption>", html, StringComparison.Ordinal);
        foreach (var column in new[] { "Operation", "Status", "Attempt", "Created", "Started", "Finished", "Worked on" })
        {
            Assert.Contains($"<th scope=\"col\">{column}</th>", html, StringComparison.Ordinal);
        }

        Assert.Contains($"<td class=\"cell-primary\"><a href=\"/jobs/{pending}\">Provision instance</a></td>", html, StringComparison.Ordinal);
        Assert.Contains($"<td class=\"cell-primary\"><a href=\"/jobs/{failing}\">Back up database</a></td>", html, StringComparison.Ordinal);
        Assert.Matches("<span class=\"badge badge-progress\"><svg.*?</svg> Pending</span>", html);
        Assert.Matches("<span class=\"badge badge-success\"><svg.*?</svg> Completed</span>", html);
        // A failed job: the word, the stable code, and nothing of what the tool said.
        Assert.Matches("<span class=\"badge badge-danger\"><svg.*?</svg> Failed</span> <code>BACKUP_PROCESS_FAILED</code>", html);
        Assert.Contains("<td>3 of 3</td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-tool-detail", html, StringComparison.Ordinal);
        // What each job worked on, as ways there.
        Assert.Contains($"<a href=\"/instances/{pendingInstance}\">Instance</a>", html, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"{target.Database}\">Database</a>", html, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"{target.Database}/backups/{backupId}\">Backup</a>", html, StringComparison.Ordinal);
        Assert.Contains("Showing 1–5 of 5.", html, StringComparison.Ordinal);
        // Something is pending, so the page keeps itself up to date.
        Assert.Contains("data-refresh=\"5\"", html, StringComparison.Ordinal);
        // Nothing can be done to a job from here.
        Assert.DoesNotContain("method=\"post\" class=\"form\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jobs_ArePagedAndFiltered_ByTheServicesOwnQuery_AndTheFiltersStayInTheAddress()
    {
        var target = await DatabaseAsync();
        for (var i = 0; i < 22; i++)
        {
            await _factory.CreateCompletedBackupAsync(_api, target.DatabaseId);
        }

        var (_, failing) = await _api.CreateBackupAsync(target.DatabaseId);
        _factory.DumpTools.FailAllRuns();
        await _factory.ProcessJobAsync(failing);
        var other = await _factory.CreateRunningInstanceAsync(_api, name: "other");
        var viewer = await SignedInAsync("viewer");

        var all = Flat(await viewer.GetHtmlAsync("/jobs"));
        var backups = Flat(await viewer.GetHtmlAsync("/jobs?type=backup_database"));
        var secondPage = Flat(await viewer.GetHtmlAsync("/jobs?type=backup_database&page=2"));
        var failed = Flat(await viewer.GetHtmlAsync("/jobs?status=failed&type=backup_database"));
        var ofOther = Flat(await viewer.GetHtmlAsync($"/jobs?instanceId={other}"));
        var ofDatabase = Flat(await viewer.GetHtmlAsync($"/jobs?databaseId={target.DatabaseId}&status=completed"));
        var none = Flat(await viewer.GetHtmlAsync("/jobs?type=restore_database"));
        var beyond = Flat(await viewer.GetHtmlAsync("/jobs?type=backup_database&page=9"));

        Assert.Contains("Showing 1–20 of 26.", all, StringComparison.Ordinal);
        Assert.Contains("<a class=\"button\" href=\"/jobs?page=2\" rel=\"next\">Next</a>", all, StringComparison.Ordinal);
        // Every status and every operation the application has can be filtered by, under the names the API takes.
        foreach (var status in Enum.GetValues<JobStatus>())
        {
            Assert.Contains($"<option value=\"{System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString())}\"", all, StringComparison.Ordinal);
        }

        foreach (var type in Enum.GetValues<JobType>())
        {
            var value = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(type.ToString());
            Assert.Contains($"<option value=\"{value}\"", all, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/jobs?type={value}")).StatusCode);
        }

        Assert.Contains("Showing 1–20 of 23.", backups, StringComparison.Ordinal);
        Assert.DoesNotContain("Provision instance</a>", backups, StringComparison.Ordinal);
        Assert.Contains("<option value=\"backup_database\" selected=\"selected\">Back up database</option>", backups, StringComparison.Ordinal);
        // The next page keeps the filter.
        Assert.Contains("href=\"/jobs?type=backup_database&amp;page=2\" rel=\"next\"", backups, StringComparison.Ordinal);
        Assert.Contains("Showing 21–23 of 23.", secondPage, StringComparison.Ordinal);
        Assert.Contains("href=\"/jobs?type=backup_database\" rel=\"prev\"", secondPage, StringComparison.Ordinal);
        Assert.Contains("Showing 1–1 of 1.", failed, StringComparison.Ordinal);
        Assert.Contains("<code>BACKUP_PROCESS_FAILED</code>", failed, StringComparison.Ordinal);
        Assert.Contains("<option value=\"failed\" selected=\"selected\">Failed</option>", failed, StringComparison.Ordinal);
        Assert.Contains("Showing 1–1 of 1.", ofOther, StringComparison.Ordinal);
        Assert.Contains($"<input type=\"hidden\" name=\"instanceId\" value=\"{other}\" />", ofOther, StringComparison.Ordinal);
        Assert.Contains("Showing 1–20 of 23.", ofDatabase, StringComparison.Ordinal);
        Assert.Contains($"Showing the jobs of database <span class=\"mono\">{target.DatabaseId}</span>.", ofDatabase, StringComparison.Ordinal);
        Assert.Contains("<h2>No jobs match</h2>", none, StringComparison.Ordinal);
        Assert.Contains("href=\"/jobs\">Clear filters</a>", none, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", none, StringComparison.Ordinal);
        Assert.Contains("<h2>Nothing on this page</h2>", beyond, StringComparison.Ordinal);
        Assert.Contains("href=\"/jobs?type=backup_database\">Back to the first page</a>", beyond, StringComparison.Ordinal);

        // What the service's query refuses, the page refuses: no status of its own, no page that is not one.
        foreach (var path in new[] { "/jobs?page=0", "/jobs?status=exploded", "/jobs?type=drop_database", "/jobs?instanceId=not-an-id", "/jobs?pageSize=5000" })
        {
            var response = await viewer.GetAsync(path);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Request ID:", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NoJobsAtAll_SaysSo()
    {
        var viewer = await SignedInAsync("viewer");

        var html = Flat(await viewer.GetHtmlAsync("/jobs"));

        Assert.Contains("<h2>No jobs have been recorded yet</h2>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Clear filters", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobPage_ShowsWhatTheJobIs_WhatItWorkedOn_AndForAFailedOneTheStableCodeAndMessage_AndNothingElse()
    {
        var target = await DatabaseAsync();
        var backupId = await _factory.CreateCompletedBackupAsync(_api, target.DatabaseId);
        var completed = await _factory.WithDbAsync(db => db.Jobs.Where(job => job.BackupId == backupId).Select(job => job.Id).SingleAsync());
        var (_, failing) = await _api.CreateBackupAsync(target.DatabaseId);
        _factory.DumpTools.FailAllRuns("raw-tool-detail: FATAL password authentication failed for user postgres at /var/lib/aurora");
        await _factory.ProcessJobAsync(failing);
        var (_, pending) = await _api.CreateInstanceAsync(name: "still-provisioning");
        var password = await _factory.AdminPasswordAsync(target.InstanceId);
        var failure = (await _api.GetJobAsync(failing)).GetProperty("error");
        var viewer = await SignedInAsync("viewer");

        var done = Flat(await viewer.GetHtmlAsync($"/jobs/{completed}"));
        var failed = Flat(await viewer.GetHtmlAsync($"/jobs/{failing}"));
        var waiting = Flat(await viewer.GetHtmlAsync($"/jobs/{pending}"));

        Assert.Matches("<h1>Back up database</h1> <span class=\"badge badge-success\"><svg.*?</svg> Completed</span>", done);
        Assert.Contains("<a href=\"/jobs\">Jobs</a>", done, StringComparison.Ordinal);
        Assert.Contains("<dt>Attempt</dt> <dd>1 of 3</dd>", done, StringComparison.Ordinal);
        Assert.Matches("<dt>Created</dt> <dd> <time datetime=\"[^\"]+\">[^<]+ UTC</time> </dd> <dt>Started</dt> <dd> <time", done);
        Assert.Contains($"<a href=\"/instances/{target.InstanceId}\">production-db</a> <span class=\"muted\">PostgreSQL 16</span>", done, StringComparison.Ordinal);
        Assert.Contains($"<a class=\"mono\" href=\"{target.Database}\">orders</a>", done, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"{target.Database}/backups/{backupId}\">The backup that was made</a>", done, StringComparison.Ordinal);
        Assert.Contains($"<dt>ID</dt> <dd class=\"mono\">{completed}</dd>", done, StringComparison.Ordinal);
        Assert.Contains($"href=\"/jobs?databaseId={target.DatabaseId}\"", done, StringComparison.Ordinal);
        Assert.DoesNotContain("data-refresh", done, StringComparison.Ordinal);
        Assert.DoesNotContain("<dt>Error</dt>", done, StringComparison.Ordinal);

        Assert.Matches("<h1>Back up database</h1> <span class=\"badge badge-danger\"><svg.*?</svg> Failed</span>", failed);
        Assert.Contains("<dt>Error</dt> <dd><code>BACKUP_PROCESS_FAILED</code></dd>", failed, StringComparison.Ordinal);
        Assert.Contains(
            $"<dt>Message</dt> <dd>{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(failure.GetProperty("message").GetString()!)}</dd>",
            failed,
            StringComparison.Ordinal);
        Assert.Contains("Failed for good: every attempt was used.", failed, StringComparison.Ordinal);
        foreach (var leaked in new[] { "raw-tool-detail", "authentication failed", "/var/lib/aurora", password, "Exception", "   at ", "PGPASSWORD" })
        {
            Assert.DoesNotContain(leaked, failed, StringComparison.Ordinal);
        }

        Assert.Matches("<h1>Provision instance</h1> <span class=\"badge badge-progress\"><svg.*?</svg> Pending</span>", waiting);
        Assert.Contains("Waiting to be picked up.", waiting, StringComparison.Ordinal);
        Assert.Contains("data-refresh=\"5\"", waiting, StringComparison.Ordinal);
        Assert.DoesNotContain("<dt>Database</dt>", waiting, StringComparison.Ordinal);
    }

    // === Monitoring ==============================================================================

    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task Monitoring_OnAQuietInstallation_IsHealthy_AndSaysWhatEachThingIs(string role)
    {
        var browser = await SignedInAsync(role);

        var html = Flat(await browser.GetHtmlAsync("/monitoring"));
        var health = Section(html, "health-title");
        var scheduler = Section(html, "scheduler-title");

        Assert.Contains("<h1>Monitoring</h1>", html, StringComparison.Ordinal);
        Assert.Contains("As of <time datetime=\"2026-10-05T12:00:00Z\">2026-10-05 12:00 UTC</time> · <a href=\"/monitoring\">Refresh</a>", html, StringComparison.Ordinal);
        Assert.Matches("<dt>Aurora</dt> <dd> <span class=\"badge badge-success\"><svg.*?</svg> Healthy</span> <span class=\"muted\"> Ready: Aurora can do all of its work\\.", health);
        Assert.Matches("<dt>System database</dt> <dd> <span class=\"badge badge-success\"><svg.*?</svg> Healthy</span>", health);
        Assert.Matches("<dt>Docker</dt> <dd> <span class=\"badge badge-success\"><svg.*?</svg> Healthy</span>", health);
        // The check of a storage this server does not use says so, rather than "healthy".
        Assert.Matches("<dt>S3 backup storage</dt> <dd> <span class=\"badge badge-neutral\"><svg.*?</svg> Not in use</span>", health);
        Assert.DoesNotContain("Degraded", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhealthy", html, StringComparison.Ordinal);

        Assert.Matches("<dt>Schedules</dt> <dd> <span class=\"badge badge-success\"><svg.*?</svg> On time</span>", scheduler);
        Assert.Contains("<dt>Enabled schedules</dt> <dd>0</dd>", scheduler, StringComparison.Ordinal);
        Assert.Contains("None: no schedule is enabled.", scheduler, StringComparison.Ordinal);
        Assert.Contains("None since this process started.", scheduler, StringComparison.Ordinal);
        Assert.Contains("<h2>No recent failures</h2>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/jobs?status=failed\"", html, StringComparison.Ordinal);
        // A page to read: nothing on it changes anything.
        Assert.DoesNotContain("<form method=\"post\" class=\"form\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Monitoring_ShowsWhatTheSummaryAndTheHealthChecksSay_WhenThingsAreNotWell()
    {
        var target = await DatabaseAsync();
        await ScheduledAsync(target, "0 0 * * *", "Asia/Dhaka");
        var (_, failing) = await _api.CreateBackupAsync(target.DatabaseId);
        _factory.DumpTools.FailAllRuns("raw-tool-detail");
        await _factory.ProcessJobAsync(failing);
        var (brokenInstance, breaking) = await _api.CreateInstanceAsync(name: "broken");
        _factory.Provisioner.FailAllCalls();
        await _factory.ProcessJobAsync(breaking);
        await _api.CreateInstanceAsync(name: "still-provisioning");
        // An hour past the schedule's run, and no scheduler pass: it is overdue. And Docker cannot be reached.
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 5, 19, 0, 0, TimeSpan.Zero));
        _factory.Docker.Unavailable = true;
        var summary = await (await _api.GetAsync(MonitoringSummaryUrl)).ReadJsonAsync(HttpStatusCode.OK);
        var viewer = await SignedInAsync("viewer");

        var html = Flat(await viewer.GetHtmlAsync("/monitoring"));
        var health = Section(html, "health-title");
        var scheduler = Section(html, "scheduler-title");
        var failures = Section(html, "failures-title");

        // Docker is needed but not for everything: degraded, as readiness has it; never the reason why.
        Assert.Matches("<dt>Aurora</dt> <dd> <span class=\"badge badge-warning\"><svg.*?</svg> Degraded</span> <span class=\"muted\"> Working, but not everything it needs can be reached\\.", health);
        Assert.Matches("<dt>Docker</dt> <dd> <span class=\"badge badge-warning\"><svg.*?</svg> Degraded</span>", health);
        Assert.Matches("<dt>System database</dt> <dd> <span class=\"badge badge-success\"><svg.*?</svg> Healthy</span>", health);
        Assert.DoesNotContain("raw-daemon-detail", html, StringComparison.Ordinal);
        Assert.DoesNotContain("docker.sock", html, StringComparison.Ordinal);

        // The scheduler: what the summary says, in its own terms.
        Assert.Equal(1, summary.GetProperty("scheduler").GetProperty("overdueSchedules").GetInt32());
        Assert.Matches("<dt>Schedules</dt> <dd> <span class=\"badge badge-warning\"><svg.*?</svg> 1 overdue</span>", scheduler);
        Assert.Contains("<dt>Enabled schedules</dt> <dd>1</dd>", scheduler, StringComparison.Ordinal);
        Assert.Contains("<time datetime=\"2026-10-05T18:00:00Z\">2026-10-05 18:00 UTC</time>", scheduler, StringComparison.Ordinal);

        // The counts are the summary's.
        var instances = summary.GetProperty("instances");
        Assert.Contains(
            $"<div class=\"stat-value\">{instances.GetProperty("total").GetInt32()}</div> <div class=\"stat-detail\">{instances.GetProperty("running").GetInt32()} running · {instances.GetProperty("provisioning").GetInt32()} provisioning · {instances.GetProperty("stopped").GetInt32()} stopped · {instances.GetProperty("failed").GetInt32()} failed</div>",
            html,
            StringComparison.Ordinal);
        Assert.Equal((3, 1, 1, 1), (instances.GetProperty("total").GetInt32(), instances.GetProperty("running").GetInt32(), instances.GetProperty("provisioning").GetInt32(), instances.GetProperty("failed").GetInt32()));
        Assert.Contains("Backups</div> <div class=\"stat-value\">0</div> <div class=\"stat-detail\">0 running · 0 pending · 1 failed</div>", html, StringComparison.Ordinal);
        Assert.Contains("Jobs</div> <div class=\"stat-value\">1</div> <div class=\"stat-detail\">0 running · 1 pending · 2 failed</div>", html, StringComparison.Ordinal);

        // The failures, each with its stable code and the way to its job and to what it worked on.
        Assert.Contains($"<td class=\"cell-primary\"><a href=\"/jobs/{failing}\">Back up database</a></td> <td><code>BACKUP_PROCESS_FAILED</code></td>", failures, StringComparison.Ordinal);
        Assert.Contains($"<td class=\"cell-primary\"><a href=\"/jobs/{breaking}\">Provision instance</a></td> <td><code>PROVISIONING_FAILED</code></td>", failures, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"/instances/{brokenInstance}\">Instance</a>", failures, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"{target.Database}\">Database</a>", failures, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-tool-detail", html, StringComparison.Ordinal);

        // The overview says the same, more briefly, and its failures lead to their jobs too.
        var overview = Flat(await viewer.GetHtmlAsync("/"));
        Assert.Contains($"<a href=\"/jobs/{failing}\">{failing}</a>", overview, StringComparison.Ordinal);
        Assert.Contains("1 overdue", overview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Monitoring_ShowsTheSchedulersLastPass_OnceItHasMadeOne()
    {
        var target = await DatabaseAsync();
        await ScheduledAsync(target, "0 0 * * *", "Asia/Dhaka");
        var viewer = await SignedInAsync("viewer");
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 5, 13, 30, 0, TimeSpan.Zero));
        await _factory.RunSchedulerAsync();
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 5, 13, 31, 0, TimeSpan.Zero));

        var scheduler = Section(Flat(await viewer.GetHtmlAsync("/monitoring")), "scheduler-title");

        Assert.Contains("<dt>Last pass</dt> <dd> <time datetime=\"2026-10-05T13:30:00Z\">2026-10-05 13:30 UTC</time>", scheduler, StringComparison.Ordinal);
        Assert.Contains("<dt>Next run</dt> <dd> <time datetime=\"2026-10-05T18:00:00Z\">", scheduler, StringComparison.Ordinal);
        Assert.Contains("</svg> On time</span>", scheduler, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemDatabaseGone_MonitoringIsAnErrorPage_WithNothingOfTheFailure()
    {
        var viewer = await SignedInAsync("viewer");
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE backup_schedules"));

        var response = await viewer.GetAsync("/monitoring");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Something went wrong", html, StringComparison.Ordinal);
        Assert.Contains("Request ID:", html, StringComparison.Ordinal);
        foreach (var leaked in new[] { "Exception", "SQLite", "no such table", "backup_schedules", "   at " })
        {
            Assert.DoesNotContain(leaked, html, StringComparison.OrdinalIgnoreCase);
        }
    }

    // === What no page contains ===================================================================

    [Fact]
    public async Task NoCredential_AndNothingTheContentSecurityPolicyWouldBlock_OnAnyOfThesePages()
    {
        var target = await DatabaseAsync();
        await ScheduledAsync(target);
        var (_, failing) = await _api.CreateBackupAsync(target.DatabaseId);
        _factory.DumpTools.FailAllRuns("raw-tool-detail PGPASSWORD=hunter2");
        await _factory.ProcessJobAsync(failing);
        var password = await _factory.AdminPasswordAsync(target.InstanceId);
        var admin = await SignedInAsync("admin");
        var responses = new List<HttpResponseMessage>();
        foreach (var path in new[]
                 {
                     target.Schedule, target.Edit, target.Delete, "/jobs", "/jobs?status=failed", $"/jobs/{failing}", "/monitoring", "/",
                     target.Database, $"/jobs/{Guid.CreateVersion7()}", "/jobs?page=0"
                 })
        {
            responses.Add(await admin.GetAsync(path));
        }

        responses.Add(await admin.PostFormAsync(target.Edit, ScheduleForm("not a cron", "UTC")));

        foreach (var response in responses)
        {
            var html = await response.Content.ReadAsStringAsync();

            foreach (var secret in new[] { password, WebFactory.SigningKey, "hunter2", "PGPASSWORD", "POSTGRES_PASSWORD", "raw-tool-detail", "eyJhbGciOi", "Exception", "   at " })
            {
                Assert.DoesNotContain(secret, html, StringComparison.Ordinal);
            }

            Assert.Equal(WebProgram.PageContentSecurityPolicy, string.Join("", response.Headers.GetValues("Content-Security-Policy")));
            Assert.Contains("no-store", string.Join("", response.Headers.GetValues("Cache-Control")), StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{32}$", string.Join("", response.Headers.GetValues("X-Request-Id")));
            Assert.Empty(InlineScript().Matches(html));
            Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(StyleAttribute().Matches(html));
            Assert.Empty(EventHandlerAttribute().Matches(html));
            Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
            // Every form that changes something is a POST with a token; the only GET form is the jobs filter.
            Assert.All(
                Form().Matches(html).Where(form => !form.Value.Contains("class=\"filters\"", StringComparison.Ordinal)),
                form => Assert.Contains("method=\"post\"", form.Value, StringComparison.Ordinal));
            Assert.Equal(
                Form().Matches(html).Count(form => form.Value.Contains("method=\"post\"", StringComparison.Ordinal)),
                Regex.Matches(html, "name=\"__RequestVerificationToken\"").Count);
        }

        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Script_HasNoCronParser_AndNoTimeZoneOfItsOwn()
    {
        using var browser = _factory.CreateBrowser();

        var script = await (await browser.GetAsync("/js/aurora.js")).Content.ReadAsStringAsync();

        foreach (var absent in new[] { "cron", "timeZone", "Intl.DateTimeFormat", "getTimezoneOffset", "setInterval", "WebSocket", "EventSource", "signalr" })
        {
            Assert.DoesNotContain(absent, script, StringComparison.OrdinalIgnoreCase);
        }
    }

    [GeneratedRegex("<script(?![^>]*\\bsrc=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    [GeneratedRegex("<[^>]+\\sstyle\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttribute();

    [GeneratedRegex("<[^>]+\\son[a-z]+\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerAttribute();

    [GeneratedRegex("<form[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Form();
}
