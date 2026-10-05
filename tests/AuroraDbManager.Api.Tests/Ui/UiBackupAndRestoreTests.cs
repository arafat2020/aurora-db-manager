using System.Net;
using System.Text.RegularExpressions;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Tests.Backups;
using AuroraDbManager.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuroraDbManager.Api.Tests.Ui;

/// <summary>
/// The pages for a database's backups and for restoring from them, through the real Web host:
/// real sign-ins, real antiforgery tokens, and the real backup and restore services, job system,
/// storages and integrity checks behind the pages. Only the dump programs and the object store
/// are stand-ins, as in every other test. Jobs are run by the test, one at a time, so every
/// state between "asked for" and "done" is looked at as it is.
/// </summary>
public sealed partial class UiBackupAndRestoreTests : IDisposable
{
    private const string UserPassword = "a-perfectly-fine-password";
    private const string S3AccessKey = "AKIAAURORAUITESTKEY0";
    private const string S3SecretKey = "aurora-ui-test-secret-key-4c81d7a2e9";

    private readonly List<IDisposable> _disposables = [];
    private readonly WebFactory _factory;
    private readonly HttpClient _api;

    public UiBackupAndRestoreTests()
    {
        _factory = Track(new WebFactory { BootstrapAdmin = (Browser.Admin, Browser.Password) });
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

    private Task<HttpClient> SignedInAsync(string role) => SignedInAsync(_factory, _api, role);

    private async Task<HttpClient> SignedInAsync(WebFactory factory, HttpClient api, string role)
    {
        var browser = Track(factory.CreateBrowser());
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

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string Flat(string html) => Regex.Replace(html, "\\s+", " ");

    private static async Task<string> HtmlOf(HttpResponseMessage response) => Flat(await response.Content.ReadAsStringAsync());

    private static string Encoded(string text) => System.Text.Encodings.Web.HtmlEncoder.Default.Encode(text);

    private sealed record Target(Guid InstanceId, Guid DatabaseId)
    {
        public string Backups => $"/instances/{InstanceId}/databases/{DatabaseId}/backups";

        public string Create => $"{Backups}/create";

        public string Backup(Guid backupId) => $"{Backups}/{backupId}";

        public string Restore(Guid backupId) => $"{Backups}/{backupId}/restore";
    }

    private Task<Target> DatabaseAsync(string engine = "postgres", string name = "orders") => DatabaseAsync(_factory, _api, engine, name);

    private static async Task<Target> DatabaseAsync(WebFactory factory, HttpClient api, string engine = "postgres", string name = "orders")
    {
        var instanceId = await factory.CreateRunningInstanceAsync(api, engine: engine);
        return new Target(instanceId, await factory.CreateReadyDatabaseAsync(api, instanceId, name));
    }

    private Task<int> BackupCountAsync() => _factory.WithDbAsync(db => db.Backups.CountAsync());

    private Task<int> RestoreJobCountAsync() => _factory.WithDbAsync(db => db.Jobs.CountAsync(job => job.Type == JobType.RestoreDatabase));

    private Task<Guid> UnfinishedJobAsync(Guid databaseId) => _factory.WithDbAsync(db => db.Jobs
        .Where(job => job.DatabaseId == databaseId && job.CompletedAt == null)
        .Select(job => job.Id)
        .SingleAsync());

    private static WebFactory S3Application(TempDatabase database, string backupRoot, FakeS3ObjectStore objects, BackupStorageType defaultStorage, bool s3Configured = true) => new()
    {
        BootstrapAdmin = (Browser.Admin, Browser.Password),
        DatabasePath = database.Path,
        BackupRootPath = backupRoot,
        ObjectStore = objects,
        ConfigureBackups = options =>
        {
            options.StorageType = defaultStorage;
            if (s3Configured)
            {
                options.S3.Bucket = FakeS3ObjectStore.Bucket;
                options.S3.Region = "us-east-1";
                options.S3.AccessKey = S3AccessKey;
                options.S3.SecretKey = S3SecretKey;
            }
        }
    };

    // === The list ================================================================================

    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task EverySignedInUser_SeesADatabasesBackups_InItsContext_AndOnlyThoseWhoMayAreOfferedChanges(string role)
    {
        var target = await DatabaseAsync(engine: "mysql", name: "shop");
        var completed = await _factory.CreateCompletedBackupAsync(_api, target.DatabaseId);
        var browser = await SignedInAsync(role);

        var html = Flat(await browser.GetHtmlAsync(target.Backups));

        // Which database, of which instance, of which engine: said in words and in the way back.
        Assert.Contains("<h1>Backups</h1>", html, StringComparison.Ordinal);
        Assert.Contains("Backups of the database shop, in production-db, a MySQL 16 instance.", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"/instances\">Instances</a>", html, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"/instances/{target.InstanceId}\">production-db</a>", html, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"/instances/{target.InstanceId}/databases/{target.DatabaseId}\">shop</a>", html, StringComparison.Ordinal);
        Assert.Contains("<span aria-current=\"location\">Backups</span>", html, StringComparison.Ordinal);

        Assert.Contains("<caption>Backups of shop, newest first</caption>", html, StringComparison.Ordinal);
        foreach (var column in new[] { "Created", "Status", "Size", "Storage", "Integrity" })
        {
            Assert.Contains($"<th scope=\"col\">{column}</th>", html, StringComparison.Ordinal);
        }

        Assert.Matches($"<td class=\"cell-primary\"><a href=\"{target.Backup(completed)}\"> <time datetime=\"[^\"]+\">[^<]+ UTC</time> </a></td>", html);
        // Status, storage and integrity in words, never a colour alone.
        Assert.Matches("<span class=\"badge badge-success\"><svg.*?</svg> Completed</span>", html);
        Assert.Contains("<td>Local</td>", html, StringComparison.Ordinal);
        Assert.Matches("<span class=\"badge badge-success\"><svg.*?</svg> Verified</span>", html);
        Assert.Matches("<td> \\d+ B </td>", html);
        Assert.Contains("Showing 1–1 of 1.", html, StringComparison.Ordinal);

        var mayChange = role != "viewer";
        Assert.Equal(mayChange, html.Contains($"href=\"{target.Create}\"", StringComparison.Ordinal));
        Assert.Equal(mayChange, html.Contains($"href=\"{target.Restore(completed)}\"", StringComparison.Ordinal));
        // There is no deleting a backup, for anyone.
        Assert.DoesNotContain("Delete", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_OffersCreationToAnOperator_AndOnlyExplainsToAViewer()
    {
        var target = await DatabaseAsync();
        var op = await SignedInAsync("operator");
        var viewer = await SignedInAsync("viewer");

        var forOperator = Flat(await op.GetHtmlAsync(target.Backups));
        var forViewer = Flat(await viewer.GetHtmlAsync(target.Backups));

        Assert.Contains("<h2>No backups yet</h2>", forOperator, StringComparison.Ordinal);
        Assert.Contains("Create a backup of this database to protect its data.", forOperator, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(forOperator, $"href=\"{target.Create}\"").Count);
        Assert.DoesNotContain("<table", forOperator, StringComparison.Ordinal);

        Assert.Contains("<h2>No backups yet</h2>", forViewer, StringComparison.Ordinal);
        Assert.Contains("An operator or an administrator creates them.", forViewer, StringComparison.Ordinal);
        Assert.DoesNotContain("/backups/create", forViewer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_IsPaged_ByTheServicesOwnPages_AndAPageThatIsNotOneIsRefused()
    {
        var target = await DatabaseAsync();
        for (var i = 0; i < 23; i++)
        {
            await _factory.CreateCompletedBackupAsync(_api, target.DatabaseId);
        }

        var viewer = await SignedInAsync("viewer");

        var first = Flat(await viewer.GetHtmlAsync(target.Backups));
        var second = Flat(await viewer.GetHtmlAsync($"{target.Backups}?page=2"));
        var beyond = Flat(await viewer.GetHtmlAsync($"{target.Backups}?page=9"));
        var nonsense = await viewer.GetAsync($"{target.Backups}?page=0");
        var tooMany = await viewer.GetAsync($"{target.Backups}?pageSize=5000");

        Assert.Equal(20, Regex.Matches(first, "<td class=\"cell-primary\">").Count);
        Assert.Contains("Showing 1–20 of 23.", first, StringComparison.Ordinal);
        Assert.Contains($"<a class=\"button\" href=\"{target.Backups}?page=2\" rel=\"next\">Next</a>", first, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(second, "<td class=\"cell-primary\">").Count);
        Assert.Contains("Showing 21–23 of 23.", second, StringComparison.Ordinal);
        Assert.Contains($"<a class=\"button\" href=\"{target.Backups}\" rel=\"prev\">Previous</a>", second, StringComparison.Ordinal);
        Assert.Contains("<h2>Nothing on this page</h2>", beyond, StringComparison.Ordinal);
        Assert.Contains($"href=\"{target.Backups}\">Back to the first page</a>", beyond, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", beyond, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("Request ID:", await nonsense.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryStatusABackupHas_IsRendered_WithTheErrorCodeOfAFailedOne_AndNothingOfItsFailure()
    {
        var target = await DatabaseAsync();
        var (failed, failing) = await _api.CreateBackupAsync(target.DatabaseId);
        _factory.DumpTools.FailAllRuns();
        await _factory.ProcessJobAsync(failing);
        _factory.DumpTools.ClearScript();
        var completed = await _factory.CreateCompletedBackupAsync(_api, target.DatabaseId);
        var (pending, _) = await _api.CreateBackupAsync(target.DatabaseId);
        var op = await SignedInAsync("operator");

        var html = Flat(await op.GetHtmlAsync(target.Backups));
        var onDatabase = Flat(await op.GetHtmlAsync($"/instances/{target.InstanceId}/databases/{target.DatabaseId}"));

        foreach (var page in new[] { html, onDatabase })
        {
            Assert.Matches("<span class=\"badge badge-progress\"><svg.*?</svg> Pending</span>", page);
            Assert.Matches("<span class=\"badge badge-success\"><svg.*?</svg> Completed</span>", page);
            Assert.Matches("<span class=\"badge badge-danger\"><svg.*?</svg> Failed</span> <code>BACKUP_PROCESS_FAILED</code>", page);
            Assert.DoesNotContain("raw-tool-detail", page, StringComparison.Ordinal);
            // Something is on its way: the page says so and keeps itself up to date.
            Assert.Contains("data-refresh=\"5\"", page, StringComparison.Ordinal);
            // Only what holds something can be restored.
            Assert.Contains($"href=\"{target.Restore(completed)}\"", page, StringComparison.Ordinal);
            Assert.DoesNotContain(target.Restore(failed), page, StringComparison.Ordinal);
            Assert.DoesNotContain(target.Restore(pending), page, StringComparison.Ordinal);
        }

        Assert.Contains("All backups (3)", onDatabase, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(html, "</svg> Verified</span>"));
    }

    [Fact]
    public async Task HistoryOfOnlyFailedBackups_SaysThereIsNothingToRestoreFrom()
    {
        var target = await DatabaseAsync();
        _factory.DumpTools.FailAllRuns();
        for (var i = 0; i < 2; i++)
        {
            var (_, jobId) = await _api.CreateBackupAsync(target.DatabaseId);
            await _factory.ProcessJobAsync(jobId);
        }

        var op = await SignedInAsync("operator");

        var html = Flat(await op.GetHtmlAsync(target.Backups));

        Assert.Equal(2, Regex.Matches(html, "</svg> Failed</span>").Count);
        Assert.Contains("None of these backups was completed, so there is nothing to restore from yet.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/restore", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-refresh", html, StringComparison.Ordinal);
    }

    // === Creating ================================================================================

    [Theory]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task OperatorAndAdmin_CreateABackup_WhichIsPending_ThenCompleted_OnceItsJobHasRun(string role)
    {
        var target = await DatabaseAsync();
        var other = await _factory.CreateReadyDatabaseAsync(_api, target.InstanceId, "another");
        var browser = await SignedInAsync(role);

        var form = Flat(await browser.GetHtmlAsync(target.Create));
        await browser.GetAsync($"{target.Create}?handler=Post&confirm=true");
        Assert.Equal(0, await BackupCountAsync());

        Assert.Contains("<h1>Create backup</h1>", form, StringComparison.Ordinal);
        Assert.Contains("<dt>Database</dt> <dd class=\"mono\">orders</dd>", form, StringComparison.Ordinal);
        Assert.Contains("<dt>Engine</dt> <dd>PostgreSQL 16</dd>", form, StringComparison.Ordinal);
        // Where it will go is the server's to say, and is said, not asked.
        Assert.Contains("<dt>Storage</dt> <dd> <strong>Local</strong> <span class=\"muted\">A directory on the server Aurora runs on. Set by Aurora&#x27;s configuration.</span>", form.Replace("Aurora's", "Aurora&#x27;s", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("<select", form, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"radio\"", form, StringComparison.Ordinal);
        Assert.Contains("data-busy-label=\"Starting…\">Create backup</button>", form, StringComparison.Ordinal);
        Assert.Contains($"<a class=\"button\" href=\"{target.Backups}\">Cancel</a>", form, StringComparison.Ordinal);

        var response = await browser.PostFormAsync(target.Create, []);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var backup = await _factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync());
        // Of the database in the address, and of no other.
        Assert.Equal(target.DatabaseId, backup.DatabaseId);
        Assert.NotEqual(other, backup.DatabaseId);
        Assert.Equal(BackupStatus.Pending, backup.Status);
        Assert.Equal(target.Backup(backup.Id), response.Headers.Location!.OriginalString);

        var started = Flat(await browser.GetHtmlAsync(target.Backup(backup.Id)));
        // Started, and said to be exactly that.
        Assert.Matches("<div class=\"alert alert-progress\" role=\"status\">.*?Backup creation started\\.", started);
        Assert.DoesNotContain("Backup created", started, StringComparison.Ordinal);
        Assert.DoesNotContain("created successfully", started, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("</h1> <span class=\"badge badge-progress\"><svg.*?</svg> Pending</span>", started);
        Assert.Contains("The backup has been asked for and is waiting to be made.", started, StringComparison.Ordinal);
        Assert.Contains("data-refresh=\"5\"", started, StringComparison.Ordinal);
        Assert.Contains("<dt>Size</dt> <dd> <span class=\"muted\">Not available</span> </dd>", started, StringComparison.Ordinal);
        Assert.Contains("<dt>Integrity</dt> <dd> <span class=\"muted\">Not available</span> </dd>", started, StringComparison.Ordinal);
        Assert.DoesNotContain("/restore", started, StringComparison.Ordinal);
        Assert.Matches("Back up database</td> <td> <span class=\"badge badge-progress\"><svg.*?</svg> Pending</span>", started);
        Assert.Empty(_factory.BackupFiles());

        await _factory.ProcessJobAsync(await UnfinishedJobAsync(target.DatabaseId));
        var completed = Flat(await browser.GetHtmlAsync(target.Backup(backup.Id)));

        Assert.Matches("</h1> <span class=\"badge badge-success\"><svg.*?</svg> Completed</span>", completed);
        Assert.DoesNotContain("data-refresh", completed, StringComparison.Ordinal);
        Assert.DoesNotContain("Backup creation started", completed, StringComparison.Ordinal);
        Assert.Contains($"href=\"{target.Restore(backup.Id)}\"", completed, StringComparison.Ordinal);
        Assert.Single(_factory.BackupFiles());
    }

    [Fact]
    public async Task Viewer_CannotCreateABackup_OrRestoreOne_WhateverTheyAskFor()
    {
        var target = await DatabaseAsync();
        var backupId = await _factory.CreateCompletedBackupAsync(_api, target.DatabaseId);
        var viewer = await SignedInAsync("viewer");

        var responses = new[]
        {
            await viewer.GetAsync(target.Create),
            await viewer.GetAsync(target.Restore(backupId)),
            // With a genuine antiforgery token of their own session: it is the policy that refuses.
            await viewer.PostFormAsync(target.Create, [], tokenFrom: "/instances"),
            await viewer.PostFormAsync(target.Restore(backupId), [], tokenFrom: "/instances")
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("You don&#x27;t have permission", html, StringComparison.Ordinal);
            Assert.Contains("Request ID:", html, StringComparison.Ordinal);
        }

        Assert.Equal(1, await BackupCountAsync());
        Assert.Equal(0, await RestoreJobCountAsync());
        // They may look, at the list and at the backup.
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(target.Backups)).StatusCode);
        var details = await viewer.GetHtmlAsync(target.Backup(backupId));
        Assert.DoesNotContain("/restore", details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatingABackup_ThatTheServiceRefuses_ShowsWhy_WithTheStableCode_AndCreatesNothing()
    {
        var busy = await DatabaseAsync(name: "busy");
        await _api.CreateBackupAsync(busy.DatabaseId);
        var restoring = new Target(busy.InstanceId, await _factory.CreateReadyDatabaseAsync(_api, busy.InstanceId, "restoring"));
        await ApiFactory.RequestRestoreAsync(_api, await _factory.CreateCompletedBackupAsync(_api, restoring.DatabaseId));
        var (creatingId, _) = await _api.CreateDatabaseAsync(busy.InstanceId, "being_created");
        var creating = new Target(busy.InstanceId, creatingId);
        var op = await SignedInAsync("operator");
        var before = await BackupCountAsync();

        foreach (var (target, code, message) in new[]
                 {
                     (busy, "BACKUP_OPERATION_IN_PROGRESS", "The database already has a backup in progress."),
                     (restoring, "RESTORE_OPERATION_IN_PROGRESS", "The database cannot be backed up while it is being restored."),
                     (creating, "DATABASE_NOT_READY", "Only a ready database can be backed up.")
                 })
        {
            var response = await op.PostFormAsync(target.Create, [], tokenFrom: "/instances");

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var html = await HtmlOf(response);
            Assert.Matches($"<div class=\"alert alert-warning\" role=\"alert\">.*?<div class=\"alert-title\">{code}</div> <div>{Regex.Escape(message)}</div>", html);
            Assert.Contains("<h1>Create backup</h1>", html, StringComparison.Ordinal);
        }

        Assert.Equal(before, await BackupCountAsync());
        // What cannot be backed up now is said before anything is sent, and not offered on the list.
        Assert.Contains("A backup can be made only of a ready database in a running instance.", await op.GetHtmlAsync(creating.Create), StringComparison.Ordinal);
        Assert.DoesNotContain("/backups/create", await op.GetHtmlAsync(creating.Backups), StringComparison.Ordinal);
        // A restore under way is said on the list of the database it is replacing.
        var list = Flat(await op.GetHtmlAsync(restoring.Backups));
        Assert.Contains("<div class=\"alert-title\">Restore in progress</div> <div>The database may be temporarily unavailable until the restore has finished.</div>", list, StringComparison.Ordinal);
        Assert.Contains("data-refresh=\"5\"", list, StringComparison.Ordinal);
    }

    // === Details =================================================================================

    [Fact]
    public async Task BackupPage_ShowsWhatTheRecordSays_TheChecksumThatWasVerified_AndNothingOfWhereTheFileIs()
    {
        var target = await DatabaseAsync();
        var backupId = await _factory.CreateCompletedBackupAsync(_api, target.DatabaseId);
        var record = await _factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync());
        var viewer = await SignedInAsync("viewer");

        var html = Flat(await viewer.GetHtmlAsync(target.Backup(backupId)));

        Assert.Matches("<h1>Backup of \\d{4}-\\d\\d-\\d\\d \\d\\d:\\d\\d UTC</h1> <span class=\"badge badge-success\"><svg.*?</svg> Completed</span>", html);
        Assert.Contains("A backup of the database orders, in production-db.", html, StringComparison.Ordinal);
        Assert.Contains($"<a href=\"{target.Backups}\">Backups</a>", html, StringComparison.Ordinal);
        Assert.Contains("The backup is in the storage and can be restored.", html, StringComparison.Ordinal);
        Assert.Matches("<dt>Created</dt> <dd> <time datetime=\"[^\"]+\">[^<]+ UTC</time> </dd> <dt>Completed</dt> <dd> <time datetime=\"[^\"]+\">", html);
        Assert.Contains($"<span>{record.SizeBytes} B</span> <span class=\"muted\">{record.SizeBytes} bytes</span>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Storage</dt> <dd> <strong>Local</strong> <span class=\"muted\">A directory on the server Aurora runs on.</span> </dd>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Not configured in Aurora", html, StringComparison.Ordinal);
        // Integrity is the record's: the checksum that was verified when the backup was stored.
        Assert.Matches("<dt>Integrity</dt> <dd> <span class=\"badge badge-success\"><svg.*?</svg> Verified</span>", html);
        Assert.Contains("The stored backup was read back and matched its checksum before it was marked completed.", html, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", record.Checksum!);
        Assert.Contains($"<details class=\"disclosure\"> <summary>SHA-256</summary> <code class=\"checksum\">{record.Checksum}</code> </details>", html, StringComparison.Ordinal);
        Assert.Contains($"<dt>ID</dt> <dd class=\"mono\">{backupId}</dd>", html, StringComparison.Ordinal);
        Assert.Matches("Back up database</td> <td> <span class=\"badge badge-success\"><svg.*?</svg> Completed</span>", html);

        // Where exactly the artifact is is the server's business: not the path, not the directory, not the file's name.
        Assert.NotNull(record.Path);
        Assert.DoesNotContain(record.Path!, html, StringComparison.Ordinal);
        Assert.DoesNotContain(_factory.BackupRoot, html, StringComparison.Ordinal);
        Assert.DoesNotContain(".dump", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupFromBeforeChecksums_IsShownAsHavingNone_NotAsVerified_AndNotAsFailed()
    {
        var target = await DatabaseAsync();
        var backupId = await _factory.CreateCompletedBackupAsync(_api, target.DatabaseId);
        await _factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE backups SET checksum = NULL, checksum_algorithm = NULL WHERE id = {backupId}"));
        var op = await SignedInAsync("operator");

        var details = Flat(await op.GetHtmlAsync(target.Backup(backupId)));
        var list = Flat(await op.GetHtmlAsync(target.Backups));
        var restore = Flat(await op.GetHtmlAsync(target.Restore(backupId)));

        Assert.Matches("<dt>Integrity</dt> <dd> <span class=\"badge badge-neutral\"><svg.*?</svg> No checksum</span>", details);
        Assert.Contains("This backup was completed before checksums were recorded. Before a restore it is checked by size and format only.", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Verified", details, StringComparison.Ordinal);
        Assert.DoesNotContain("<dt>Checksum</dt>", details, StringComparison.Ordinal);
        Assert.Matches("</svg> No checksum</span>", list);
        Assert.DoesNotContain("Verified", list, StringComparison.Ordinal);
        // Still a backup, still restorable; the confirmation says what it will be checked by.
        Assert.Contains("checked against its recorded size and format; it has no recorded checksum before anything", restore, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedBackup_ShowsItsCodeAndMessage_HasNoSizeOrIntegrity_AndCannotBeRestored()
    {
        var target = await DatabaseAsync();
        var (backupId, jobId) = await _api.CreateBackupAsync(target.DatabaseId);
        _factory.DumpTools.FailAllRuns("raw-tool-detail: FATAL password authentication failed for user postgres");
        await _factory.ProcessJobAsync(jobId);
        var message = (await _api.GetBackupAsync(backupId)).GetProperty("error").GetProperty("message").GetString()!;
        var op = await SignedInAsync("operator");

        var html = Flat(await op.GetHtmlAsync(target.Backup(backupId)));
        var restore = await op.PostFormAsync(target.Restore(backupId), []);

        Assert.Matches("</h1> <span class=\"badge badge-danger\"><svg.*?</svg> Failed</span>", html);
        Assert.Contains($"<div class=\"alert-title\">BACKUP_PROCESS_FAILED</div> <div>{Encoded(message)}</div>", html, StringComparison.Ordinal);
        Assert.Contains("The backup could not be made. There is nothing to restore from it.", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Failed</dt>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Size</dt> <dd> <span class=\"muted\">Not available</span> </dd>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Integrity</dt> <dd> <span class=\"muted\">Not available</span> </dd>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-tool-detail", html, StringComparison.Ordinal);
        Assert.DoesNotContain("authentication failed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/restore", html, StringComparison.Ordinal);
        // Asked for anyway, the service says no.
        Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
        Assert.Contains("<div class=\"alert-title\">BACKUP_NOT_COMPLETED</div> <div>Only a completed backup can be restored.</div>", await HtmlOf(restore), StringComparison.Ordinal);
        Assert.Equal(0, await RestoreJobCountAsync());
    }

    [Fact]
    public async Task WhatIsNotThere_IsNotFound_AndABackupIsOnlyFoundUnderItsOwnDatabase()
    {
        var target = await DatabaseAsync(name: "one");
        var other = new Target(target.InstanceId, await _factory.CreateReadyDatabaseAsync(_api, target.InstanceId, "other"));
        var backupOfOther = await _factory.CreateCompletedBackupAsync(_api, other.DatabaseId);
        var elsewhere = await DatabaseAsync(name: "elsewhere");
        var admin = await SignedInAsync("admin");
        var nobody = Guid.CreateVersion7();

        foreach (var (path, missing) in new[]
                 {
                     (target.Backup(nobody), "Backup"),
                     (target.Restore(nobody), "Backup"),
                     // A backup of another database is not found under this one, nor under another instance.
                     (target.Backup(backupOfOther), "Backup"),
                     (target.Restore(backupOfOther), "Backup"),
                     (elsewhere.Backup(backupOfOther), "Backup"),
                     (new Target(target.InstanceId, nobody).Backups, "Database"),
                     (new Target(target.InstanceId, nobody).Create, "Database"),
                     (new Target(nobody, target.DatabaseId).Backups, "Instance"),
                     (new Target(elsewhere.InstanceId, target.DatabaseId).Backups, "Database")
                 })
        {
            var response = await admin.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains($"<h1>{missing} not found</h1>", html, StringComparison.Ordinal);
            Assert.Contains("Request ID:", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", html, StringComparison.Ordinal);
        }

        // Restoring another database's backup through this database's address restores nothing.
        var posted = await admin.PostFormAsync(target.Restore(backupOfOther), [], tokenFrom: "/instances");
        Assert.Equal(HttpStatusCode.NotFound, posted.StatusCode);
        Assert.Equal(0, await RestoreJobCountAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{target.Backups}/not-an-id")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/backups")).StatusCode);
    }

    // === Storage =================================================================================

    [Fact]
    public async Task BackupInS3_SaysSo_OnEveryPage_AndShowsNothingOfTheBucketOrTheCredentials()
    {
        using var database = new TempDatabase();
        var root = Path.Combine(Path.GetTempPath(), $"aurora-ui-backups-{Guid.NewGuid():N}");
        var factory = Track(S3Application(database, root, new FakeS3ObjectStore(), BackupStorageType.S3));
        var api = Track(factory.CreateClientAs(UserRole.Admin));
        var target = await DatabaseAsync(factory, api);
        var op = await SignedInAsync(factory, api, "operator");

        var form = Flat(await op.GetHtmlAsync(target.Create));
        var created = await op.PostFormAsync(target.Create, []);
        var backup = await factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync());
        await factory.ProcessJobAsync(await factory.WithDbAsync(db => db.Jobs.Where(job => job.BackupId == backup.Id).Select(job => job.Id).SingleAsync()));
        backup = await factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync());

        var pages = new[]
        {
            form, await HtmlOf(created), Flat(await op.GetHtmlAsync(target.Backups)), Flat(await op.GetHtmlAsync(target.Backup(backup.Id))),
            Flat(await op.GetHtmlAsync(target.Restore(backup.Id)))
        };

        Assert.Contains("<dt>Storage</dt> <dd> <strong>S3</strong> <span class=\"muted\">A bucket of an S3-compatible object store.", form, StringComparison.Ordinal);
        Assert.Equal(BackupStorageType.S3, backup.StorageType);
        Assert.Equal(BackupStatus.Completed, backup.Status);
        Assert.Contains("<td>S3</td>", pages[2], StringComparison.Ordinal);
        Assert.Contains("<dt>Storage</dt> <dd> <strong>S3</strong>", pages[3], StringComparison.Ordinal);
        Assert.Matches("</svg> Verified</span>", pages[3]);
        Assert.Contains("<dt>Storage</dt> <dd>S3</dd>", pages[4], StringComparison.Ordinal);

        foreach (var html in pages)
        {
            foreach (var secret in new[] { S3AccessKey, S3SecretKey, FakeS3ObjectStore.Bucket, backup.Path!, "us-east-1", "amazonaws" })
            {
                Assert.DoesNotContain(secret, html, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(S3SecretKey, StringComparison.Ordinal));
        DeleteDirectory(root);
    }

    [Fact]
    public async Task HistoricalBackup_KeepsTheStorageItWasMadeIn_WhateverNewBackupsGoToNow_AndIsRestoredFromThere()
    {
        using var database = new TempDatabase();
        var root = Path.Combine(Path.GetTempPath(), $"aurora-ui-backups-{Guid.NewGuid():N}");
        var objects = new FakeS3ObjectStore();
        Target target;
        Guid inS3;
        using (var before = S3Application(database, root, objects, BackupStorageType.S3))
        {
            using var api = before.CreateClientAs(UserRole.Admin);
            target = await DatabaseAsync(before, api);
            inS3 = await before.CreateCompletedBackupAsync(api, target.DatabaseId);
        }

        // The server's default is local now; S3 is still configured.
        var factory = Track(S3Application(database, root, objects, BackupStorageType.Local));
        var apiNow = Track(factory.CreateClientAs(UserRole.Admin));
        var op = await SignedInAsync(factory, apiNow, "operator");
        var local = await factory.CreateCompletedBackupAsync(apiNow, target.DatabaseId);

        var form = Flat(await op.GetHtmlAsync(target.Create));
        var list = Flat(await op.GetHtmlAsync(target.Backups));
        var old = Flat(await op.GetHtmlAsync(target.Backup(inS3)));
        var restored = await op.PostFormAsync(target.Restore(inS3), []);

        // New backups: local. The old one: where it is, not where new ones go.
        Assert.Contains("<dt>Storage</dt> <dd> <strong>Local</strong>", form, StringComparison.Ordinal);
        Assert.Contains("<td>S3</td>", list, StringComparison.Ordinal);
        Assert.Contains("<td>Local</td>", list, StringComparison.Ordinal);
        Assert.Contains("<dt>Storage</dt> <dd> <strong>S3</strong> <span class=\"muted\">A bucket of an S3-compatible object store.</span> </dd>", old, StringComparison.Ordinal);
        Assert.Contains("<dt>Storage</dt> <dd> <strong>Local</strong>", Flat(await op.GetHtmlAsync(target.Backup(local))), StringComparison.Ordinal);
        Assert.Contains($"href=\"{target.Restore(inS3)}\"", old, StringComparison.Ordinal);

        // And it is restored from the storage it is in: the job goes through, reading the object store.
        Assert.Equal(HttpStatusCode.Redirect, restored.StatusCode);
        var job = await factory.WithDbAsync(db => db.Jobs.Where(j => j.Type == JobType.RestoreDatabase).Select(j => j.Id).SingleAsync());
        await factory.ProcessJobAsync(job);
        Assert.Equal("completed", (await apiNow.GetJobAsync(job)).Status());
        DeleteDirectory(root);
    }

    [Fact]
    public async Task BackupInAStorageTheServerNoLongerHasSettingsFor_SaysSo_IsNotOfferedForRestore_AndARestoreIsRefused()
    {
        using var database = new TempDatabase();
        var root = Path.Combine(Path.GetTempPath(), $"aurora-ui-backups-{Guid.NewGuid():N}");
        var objects = new FakeS3ObjectStore();
        Target target;
        Guid inS3;
        using (var before = S3Application(database, root, objects, BackupStorageType.S3))
        {
            using var api = before.CreateClientAs(UserRole.Admin);
            target = await DatabaseAsync(before, api);
            inS3 = await before.CreateCompletedBackupAsync(api, target.DatabaseId);
        }

        var factory = Track(S3Application(database, root, objects, BackupStorageType.Local, s3Configured: false));
        var apiNow = Track(factory.CreateClientAs(UserRole.Admin));
        var op = await SignedInAsync(factory, apiNow, "operator");

        var details = Flat(await op.GetHtmlAsync(target.Backup(inS3)));
        var response = await op.PostFormAsync(target.Restore(inS3), []);

        Assert.Contains("<strong>S3</strong>", details, StringComparison.Ordinal);
        Assert.Matches("<span class=\"badge badge-warning\"><svg.*?</svg> Not configured in Aurora</span>", details);
        Assert.Contains("The backup cannot be read, and so not restored, until Aurora has settings for that storage again.", details, StringComparison.Ordinal);
        Assert.DoesNotContain($"href=\"{target.Restore(inS3)}\"", details, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var html = await HtmlOf(response);
        Assert.Contains(
            "<div class=\"alert-title\">BACKUP_STORAGE_NOT_CONFIGURED</div> <div>The backup storage the backup belongs to is not configured on the server.</div>",
            html,
            StringComparison.Ordinal);
        // Which setting is missing is for the log, not for the page.
        Assert.DoesNotContain("Backups:S3", html, StringComparison.Ordinal);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Jobs.CountAsync(job => job.Type == JobType.RestoreDatabase)));
        DeleteDirectory(root);
    }

    // === Restoring ===============================================================================

    [Theory]
    [InlineData("operator")]
    [InlineData("admin")]
    public async Task OperatorAndAdmin_RestoreABackup_OnlyByPostingTheConfirmation_AndItIsRunningUntilItsJobHasRun(string role)
    {
        var target = await DatabaseAsync(name: "production");
        var backupId = await _factory.CreateCompletedBackupAsync(_api, target.DatabaseId);
        var record = await _factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync());
        var browser = await SignedInAsync(role);

        // Asking is a GET, and restores nothing, with whatever it carries.
        var question = Flat(await browser.GetHtmlAsync(target.Restore(backupId)));
        await browser.GetAsync($"{target.Restore(backupId)}?confirm=true");
        await browser.GetAsync($"{target.Restore(backupId)}?handler=Post&confirm=true&restore=1");
        Assert.Equal(0, await RestoreJobCountAsync());

        Assert.Contains("<h1>Restore database?</h1>", question, StringComparison.Ordinal);
        // Which database and which backup, unmistakably.
        Assert.Contains("<dt>Database</dt> <dd><strong class=\"mono\">production</strong> <span class=\"muted\">in production-db, PostgreSQL 16</span></dd>", question, StringComparison.Ordinal);
        Assert.Matches("<dt>Backup</dt> <dd> <time datetime=\"[^\"]+\">[^<]+ UTC</time> <span class=\"badge badge-success\">", question);
        Assert.Contains($"<dt>Backup ID</dt> <dd class=\"mono\">{backupId}</dd>", question, StringComparison.Ordinal);
        Assert.Contains($"<dt>Size</dt> <dd> {record.SizeBytes} B </dd>", question, StringComparison.Ordinal);
        Assert.Contains("<dt>Storage</dt> <dd>Local</dd>", question, StringComparison.Ordinal);
        Assert.Matches("<dt>Integrity</dt> <dd> <span class=\"badge badge-success\"><svg.*?</svg> Verified</span>", question);
        // What it will do, in so many words.
        Assert.Contains("<strong>Restoring this backup replaces the current contents of the database <span class=\"mono\">production</span>.</strong>", question, StringComparison.Ordinal);
        Assert.Contains("Everything in the database now is removed.", question, StringComparison.Ordinal);
        Assert.Contains("This cannot be undone through Aurora, unless another backup exists that holds the current data.", question, StringComparison.Ordinal);
        Assert.Contains("the database may be unavailable while the restore is performed.", question, StringComparison.Ordinal);
        Assert.Contains($"<a class=\"button\" href=\"{target.Backup(backupId)}\">Cancel</a>", question, StringComparison.Ordinal);
        Assert.Contains("<button type=\"submit\" class=\"button button-danger-solid\" data-busy-label=\"Starting the restore…\">Restore database</button>", question, StringComparison.Ordinal);
        Assert.Contains($"<form method=\"post\" class=\"form\">", question, StringComparison.Ordinal);
        // Nothing ticked or chosen for the user, and no script confirmation standing in for the page.
        Assert.DoesNotContain("checked", question.Replace("is fetched and checked", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("data-confirm=", question, StringComparison.Ordinal);

        var response = await browser.PostFormAsync(target.Restore(backupId), []);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(target.Backup(backupId), response.Headers.Location!.OriginalString);
        var job = await _factory.WithDbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Type == JobType.RestoreDatabase));
        Assert.Equal((target.DatabaseId, (Guid?)backupId, JobStatus.Pending), (job.DatabaseId!.Value, job.BackupId, job.Status));
        Assert.Empty(_factory.RestoreSql.Statements);

        var started = Flat(await browser.GetHtmlAsync(target.Backup(backupId)));
        // Started, not finished: nothing says restored or completed about the restore.
        Assert.Matches("<div class=\"alert alert-progress\" role=\"status\">.*?Restore started\\. The database may be temporarily unavailable until it has finished\\.", started);
        Assert.Contains("<div class=\"alert-title\">Restore in progress</div>", started, StringComparison.Ordinal);
        Assert.DoesNotContain("Restore completed", started, StringComparison.Ordinal);
        Assert.DoesNotContain("was last restored", started, StringComparison.Ordinal);
        Assert.Matches("Restore database</td> <td> <span class=\"badge badge-progress\"><svg.*?</svg> Pending</span>", started);
        Assert.Contains("data-refresh=\"5\"", started, StringComparison.Ordinal);
        Assert.DoesNotMatch("\\d+ ?%", started);

        // A second restore while the first is under way is the service's to refuse.
        var again = await browser.PostFormAsync(target.Restore(backupId), []);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("<div class=\"alert-title\">RESTORE_OPERATION_IN_PROGRESS</div> <div>The database is already being restored.</div>", await HtmlOf(again), StringComparison.Ordinal);
        Assert.Equal(1, await RestoreJobCountAsync());

        await _factory.ProcessJobAsync(job.Id);
        var finished = Flat(await browser.GetHtmlAsync(target.Backup(backupId)));

        Assert.Equal("completed", (await _api.GetJobAsync(job.Id)).Status());
        Assert.Matches("<div class=\"alert alert-success\" role=\"status\">.*?<div class=\"alert-title\">Restore completed</div> <div>The database was last restored from this backup at \\d{4}-\\d\\d-\\d\\d \\d\\d:\\d\\d UTC\\.</div>", finished);
        Assert.Matches("Restore database</td> <td> <span class=\"badge badge-success\"><svg.*?</svg> Completed</span>", finished);
        Assert.DoesNotContain("data-refresh", finished, StringComparison.Ordinal);
        Assert.DoesNotContain("Restore in progress", finished, StringComparison.Ordinal);
        Assert.NotEmpty(_factory.RestoreSql.Statements);
    }

    [Fact]
    public async Task RestoreOfABackupThatNoLongerMatchesItsChecksum_Fails_IsShownAsFailed_AndTheDatabaseIsNotTouched()
    {
        var target = await DatabaseAsync();
        var backupId = await _factory.CreateCompletedBackupAsync(_api, target.DatabaseId);
        var op = await SignedInAsync("operator");
        // The stored file changes after the backup was completed: same size, other bytes.
        var path = _factory.BackupFilePath(target.InstanceId, target.DatabaseId, backupId, "dump");
        var bytes = await File.ReadAllBytesAsync(path);
        for (var i = 8; i < 12; i++)
        {
            bytes[i] = (byte)(bytes[i] == (byte)'A' ? 'B' : 'A');
        }

        await File.WriteAllBytesAsync(path, bytes);

        var response = await op.PostFormAsync(target.Restore(backupId), []);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var jobId = await UnfinishedJobAsync(target.DatabaseId);
        await _factory.ProcessJobAsync(jobId);
        var job = await _api.GetJobAsync(jobId);
        var html = Flat(await op.GetHtmlAsync(target.Backup(backupId)));

        // The backend found it out, before anything was run against the database; the page reports that.
        Assert.Equal("failed", job.Status());
        Assert.Equal("RESTORE_ARTIFACT_CHECKSUM_MISMATCH", job.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_factory.RestoreSql.Statements);
        Assert.Matches("<div class=\"alert alert-danger\" role=\"alert\">.*?<div class=\"alert-title\">The last restore failed: RESTORE_ARTIFACT_CHECKSUM_MISMATCH</div>", html);
        Assert.Contains(Encoded(job.GetProperty("error").GetProperty("message").GetString()!), html, StringComparison.Ordinal);
        Assert.Matches("Restore database</td> <td> <span class=\"badge badge-danger\"><svg.*?</svg> Failed</span> <code>RESTORE_ARTIFACT_CHECKSUM_MISMATCH</code>", html);
        Assert.DoesNotContain("Restore completed", html, StringComparison.Ordinal);
        Assert.DoesNotContain(path, html, StringComparison.Ordinal);
        Assert.DoesNotContain(_factory.BackupRoot, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoringIntoADatabaseThatIsBusy_OrNotReady_IsRefusedWithTheStableCode()
    {
        var backingUp = await DatabaseAsync(name: "backing_up");
        var backupOfFirst = await _factory.CreateCompletedBackupAsync(_api, backingUp.DatabaseId);
        await _api.CreateBackupAsync(backingUp.DatabaseId);
        var deleting = new Target(backingUp.InstanceId, await _factory.CreateReadyDatabaseAsync(_api, backingUp.InstanceId, "deleting"));
        var backupOfSecond = await _factory.CreateCompletedBackupAsync(_api, deleting.DatabaseId);
        await _api.DeleteDatabaseAsync(deleting.DatabaseId);
        var op = await SignedInAsync("operator");

        var busy = await op.PostFormAsync(backingUp.Restore(backupOfFirst), []);
        var notReady = await op.PostFormAsync(deleting.Restore(backupOfSecond), [], tokenFrom: "/instances");

        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Contains(
            "<div class=\"alert-title\">BACKUP_OPERATION_IN_PROGRESS</div> <div>The database cannot be restored while it is being backed up.</div>",
            await HtmlOf(busy),
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
        var html = await HtmlOf(notReady);
        Assert.Contains("<div class=\"alert-title\">DATABASE_NOT_READY</div> <div>A backup can only be restored into a ready database.</div>", html, StringComparison.Ordinal);
        Assert.Contains("<h1>Restore database?</h1>", html, StringComparison.Ordinal);
        Assert.Equal(0, await RestoreJobCountAsync());
        // And the list of a database that is not ready offers neither a backup nor a restore.
        var list = await op.GetHtmlAsync(deleting.Backups);
        Assert.DoesNotContain("/restore", list, StringComparison.Ordinal);
        Assert.DoesNotContain("/backups/create", list, StringComparison.Ordinal);
    }

    // === Security ================================================================================

    [Fact]
    public async Task CreatingAndRestoring_NeedTheAntiforgeryToken_AndASignIn()
    {
        var target = await DatabaseAsync();
        var backupId = await _factory.CreateCompletedBackupAsync(_api, target.DatabaseId);
        var admin = await SignedInAsync("admin");
        var anonymous = Track(_factory.CreateBrowser());

        var responses = new[]
        {
            await admin.PostFormAsync(target.Create, [], withToken: false),
            await admin.PostFormAsync(target.Restore(backupId), [], withToken: false),
            await admin.PostAsync(target.Restore(backupId), new FormUrlEncodedContent([new("__RequestVerificationToken", "CfDJ8forged")]))
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Request ID:", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        foreach (var path in new[] { target.Backups, target.Create, target.Backup(backupId), target.Restore(backupId) })
        {
            var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/login?returnUrl=", response.Headers.Location!.PathAndQuery, StringComparison.Ordinal);
        }

        Assert.Equal(1, await BackupCountAsync());
        Assert.Equal(0, await RestoreJobCountAsync());
    }

    [Fact]
    public void PagesThatChangeThings_HaveThePoliciesOfTheApiEndpointsThatDoTheSame_AndThereIsNoPageThatDeletesABackup()
    {
        var pages = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<PageActionDescriptor>() is not null)
            .GroupBy(endpoint => "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Select(data => data.Policy).Distinct().SingleOrDefault());
        const string backups = "/instances/{id:guid}/databases/{databaseId:guid}/backups";

        Assert.Equal("operator", pages[$"{backups}/create"]);
        Assert.Equal("operator", pages[$"{backups}/{{backupId:guid}}/restore"]);
        Assert.Null(pages[backups]);
        Assert.Null(pages[$"{backups}/{{backupId:guid}}"]);
        Assert.Equal(4, pages.Keys.Count(route => route.StartsWith(backups, StringComparison.Ordinal)));
        Assert.DoesNotContain("/backups", pages.Keys);
    }

    [Fact]
    public async Task NoPassword_NoStorageDetail_AndNothingTheContentSecurityPolicyWouldBlock_OnAnyBackupPage()
    {
        var target = await DatabaseAsync();
        var backupId = await _factory.CreateCompletedBackupAsync(_api, target.DatabaseId);
        var password = await _factory.AdminPasswordAsync(target.InstanceId);
        var record = await _factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync());
        var admin = await SignedInAsync("admin");
        var responses = new List<HttpResponseMessage>();
        foreach (var path in new[] { target.Backups, target.Create, target.Backup(backupId), target.Restore(backupId), target.Backup(Guid.CreateVersion7()), $"{target.Backups}?page=0" })
        {
            responses.Add(await admin.GetAsync(path));
        }

        responses.Add(await admin.PostFormAsync(target.Restore(backupId), []));
        responses.Add(await admin.PostFormAsync(target.Restore(backupId), []));
        responses.Add(await admin.GetAsync(target.Backup(backupId)));
        responses.Add(await admin.GetAsync(target.Backups));

        foreach (var response in responses.Where(response => response.StatusCode != HttpStatusCode.Redirect))
        {
            var html = await response.Content.ReadAsStringAsync();

            foreach (var secret in new[] { password, record.Path!, _factory.BackupRoot, "POSTGRES_PASSWORD", "PGPASSWORD", "Exception", "   at " })
            {
                Assert.DoesNotContain(secret, html, StringComparison.Ordinal);
            }

            Assert.All(HiddenInput().Matches(html), field => Assert.Contains("__RequestVerificationToken", field.Value, StringComparison.Ordinal));
            Assert.Equal(WebProgram.PageContentSecurityPolicy, string.Join("", response.Headers.GetValues("Content-Security-Policy")));
            Assert.Contains("no-store", string.Join("", response.Headers.GetValues("Cache-Control")), StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{32}$", string.Join("", response.Headers.GetValues("X-Request-Id")));
            Assert.Empty(InlineScript().Matches(html));
            Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(StyleAttribute().Matches(html));
            Assert.Empty(EventHandlerAttribute().Matches(html));
            Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
            // Every form is a POST with a token; nothing is changed through a link.
            Assert.All(Form().Matches(html), form => Assert.Contains("method=\"post\"", form.Value, StringComparison.Ordinal));
            Assert.Equal(Form().Matches(html).Count, Regex.Matches(html, "name=\"__RequestVerificationToken\"").Count);
        }

        Assert.DoesNotContain(_factory.Logs.Entries, entry => entry.Contains(password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostileNames_AreTextOnTheBackupPages()
    {
        const string hostile = "<script>alert(1)</script>\"'";
        var (instanceId, jobId) = await _api.CreateInstanceAsync(name: hostile);
        await _factory.ProcessJobAsync(jobId);
        var target = new Target(instanceId, await _factory.CreateReadyDatabaseAsync(_api, instanceId, "orders"));
        var backupId = await _factory.CreateCompletedBackupAsync(_api, target.DatabaseId);
        var admin = await SignedInAsync("admin");

        foreach (var path in new[] { target.Backups, target.Create, target.Backup(backupId), target.Restore(backupId) })
        {
            var html = await admin.GetHtmlAsync(path);

            Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
            Assert.Contains(Encoded(hostile), html, StringComparison.Ordinal);
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

    [GeneratedRegex("<input[^>]+type=\"hidden\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInput();
}
