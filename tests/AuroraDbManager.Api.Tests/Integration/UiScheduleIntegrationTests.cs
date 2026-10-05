using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Infrastructure.Docker;
using AuroraDbManager.Api.Tests.Ui;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Tests.Integration;

/// <summary>
/// A backup schedule made through the pages and acted on for real: the UI host with the real
/// provisioner, a real PostgreSQL container, the real scheduler and calculator on the real clock,
/// the real <c>pg_dump</c> and the real local backup storage. Opt-in: see
/// <see cref="DockerFactAttribute"/>. The scheduler makes its passes when the test says so; when
/// a schedule is due is the clock's to say, so the test waits for the minute to come.
/// </summary>
[Trait("Category", "DockerIntegration")]
public sealed class UiScheduleIntegrationTests : IAsyncLifetime
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable(DockerFactAttribute.EnableVariable) == "1";
    private static readonly bool InContainer = File.Exists("/.dockerenv");

    private const string Psql = "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -v ON_ERROR_STOP=1";

    private readonly string _network = $"aurora-db-test-{Guid.NewGuid():N}";
    private readonly List<Guid> _instanceIds = [];
    private DockerEngine _engine = null!;
    private DockerClient _docker = null!;
    private WebFactory _factory = null!;
    private HttpClient _api = null!;
    private HttpClient _browser = null!;

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        if (!InContainer && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new InvalidOperationException(
                "On Docker Desktop the instance network is not reachable from the host. "
                + "Run these tests with tests/run-docker-integration-tests.sh, which runs them in a container.");
        }

        var options = new DockerOptions { NetworkName = _network };
        _engine = new DockerEngine(Options.Create(options));
        _docker = DockerClientFactory.Create(options);

        await _engine.CreateNetworkAsync(_network, DockerResourceNaming.NetworkLabels(), default);
        if (InContainer)
        {
            await _docker.Networks.ConnectNetworkAsync(_network, new NetworkConnectParameters { Container = Environment.MachineName });
        }

        // No worker: the test runs each job itself, and looks at the pages before and after.
        _factory = new WebFactory { RealDockerNetwork = _network, BootstrapAdmin = (Browser.Admin, Browser.Password) };
        _api = _factory.CreateClientAs(UserRole.Admin);
        _browser = _factory.CreateBrowser();
    }

    public async Task DisposeAsync()
    {
        if (!Enabled || _engine is null)
        {
            return;
        }

        _browser?.Dispose();
        _api?.Dispose();
        _factory?.Dispose();

        foreach (var instanceId in _instanceIds)
        {
            await _engine.RemoveContainerAsync(DockerResourceNaming.ContainerName(instanceId), default);
            await _engine.RemoveVolumeAsync(DockerResourceNaming.VolumeName(instanceId), default);
        }

        if (InContainer)
        {
            await _docker.Networks.DisconnectNetworkAsync(
                _network, new NetworkDisconnectParameters { Container = Environment.MachineName, Force = true });
        }

        await _docker.Networks.DeleteNetworkAsync(_network);
        _docker.Dispose();
        _engine.Dispose();
    }

    [DockerFact]
    public async Task ScheduleMadeThroughThePages_IsActedOnByTheScheduler_AndTheBackupItCausesIsARealOne()
    {
        // An operator, signed in through the form: the least role that may do this.
        await _api.CreateUserAsync("the-operator", "a-perfectly-fine-password", "operator");
        await Browser.SignInAsync(_browser, "the-operator", "a-perfectly-fine-password");

        var created = await (await _api.PostAsync(
                ApiClientExtensions.InstancesUrl,
                System.Net.Http.Json.JsonContent.Create(ApiClientExtensions.ValidInstanceRequest(version: "16", memoryMb: 512, storageGb: 1))))
            .ReadJsonAsync(HttpStatusCode.Accepted);
        var instanceId = created.GetProperty("instance").GetProperty("id").GetGuid();
        _instanceIds.Add(instanceId);
        await _factory.ProcessJobAsync(created.GetProperty("job").GetProperty("id").GetGuid());
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "shop");
        var database = $"/instances/{instanceId}/databases/{databaseId}";
        Assert.Equal(0, await ExecAsync(instanceId, $"{Psql} -d shop -c \"create table orders (id int primary key); insert into orders values (1)\""));

        // --- A schedule, through the page: every minute, which is as short as a schedule gets ---
        var saved = await _browser.PostFormAsync(
            $"{database}/schedule/edit",
            [new("Input.CronExpression", "* * * * *"), new("Input.TimeZoneId", "UTC"), new("Input.Enabled", "true")]);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var schedule = await _factory.WithDbAsync(db => db.BackupSchedules.AsNoTracking().SingleAsync());
        var page = Flat(await _browser.GetHtmlAsync($"{database}/schedule"));
        Assert.Contains("Backup schedule created.", page, StringComparison.Ordinal);
        Assert.Contains("<code>* * * * *</code>", page, StringComparison.Ordinal);
        // The next run on the page is the one on the schedule's record, to the second.
        Assert.Contains($"<time datetime=\"{schedule.NextRunAt!.Value:yyyy-MM-ddTHH:mm:ss}Z\">", page, StringComparison.Ordinal);
        // Saving it backed nothing up, and a pass before it is due does nothing either.
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Backups.CountAsync()));

        // --- The scheduler, when the minute has come ---
        IReadOnlyList<Guid> jobs = [];
        for (var attempt = 0; attempt < 45 && jobs.Count == 0; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            jobs = await _factory.RunSchedulerAsync();
        }

        var jobId = Assert.Single(jobs);
        var backup = await _factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync());
        Assert.Equal(BackupStatus.Pending, backup.Status);
        // The schedule moved on to its next occurrence, and the page shows that one.
        var moved = await _factory.WithDbAsync(db => db.BackupSchedules.AsNoTracking().SingleAsync());
        Assert.True(moved.NextRunAt > schedule.NextRunAt);
        Assert.Contains($"<time datetime=\"{moved.NextRunAt!.Value:yyyy-MM-ddTHH:mm:ss}Z\">", await _browser.GetHtmlAsync($"{database}/schedule"), StringComparison.Ordinal);

        // --- The backup it caused: an ordinary job, an ordinary backup, a real file ---
        await _factory.ProcessJobAsync(jobId);

        var job = Flat(await _browser.GetHtmlAsync($"/jobs/{jobId}"));
        var backups = Flat(await _browser.GetHtmlAsync($"{database}/backups"));
        var details = Flat(await _browser.GetHtmlAsync($"{database}/backups/{backup.Id}"));
        Assert.Matches("<h1>Back up database</h1> <span class=\"badge badge-success\"><svg.*?</svg> Completed</span>", job);
        Assert.Matches("<span class=\"badge badge-success\"><svg.*?</svg> Completed</span>", backups);
        Assert.Matches("</svg> Verified</span>", details);
        var file = Assert.Single(_factory.BackupFiles());
        var checksum = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(file)));
        Assert.Contains($"<code class=\"checksum\">{checksum}</code>", details, StringComparison.Ordinal);

        // Monitoring has seen the scheduler pass, and the instance is healthy on the database's page.
        var monitoring = Flat(await _browser.GetHtmlAsync("/monitoring"));
        Assert.Contains("<dt>Enabled schedules</dt> <dd>1</dd>", monitoring, StringComparison.Ordinal);
        Assert.Matches("<dt>Last pass</dt> <dd> <time datetime=", monitoring);
        Assert.Matches("<dt>Docker</dt> <dd> <span class=\"badge badge-success\"><svg.*?</svg> Healthy</span>", monitoring);
        Assert.Matches("<dt>Instance health</dt> <dd> <span class=\"badge badge-success\"><svg.*?</svg> Healthy</span>", Flat(await _browser.GetHtmlAsync(database)));

        // --- Disabled through the page, it owes nothing more ---
        var disabled = await _browser.PostFormAsync($"{database}/schedule/edit?handler=Disable", [], tokenFrom: $"{database}/schedule");
        Assert.Equal(HttpStatusCode.Redirect, disabled.StatusCode);
        Assert.Null((await _factory.WithDbAsync(db => db.BackupSchedules.AsNoTracking().SingleAsync())).NextRunAt);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Empty(await _factory.RunSchedulerAsync());
        Assert.Equal(1, await _factory.WithDbAsync(db => db.Backups.CountAsync()));
    }

    private Task<Guid> JobOfAsync(Guid backupId, JobType type) => _factory.WithDbAsync(db => db.Jobs
        .Where(job => job.BackupId == backupId && job.Type == type)
        .Select(job => job.Id)
        .SingleAsync());

    private Task<int> ExecAsync(Guid instanceId, string shellCommand) =>
        _engine.ExecAsync(DockerResourceNaming.ContainerName(instanceId), ["sh", "-c", shellCommand], default);

    private static string Flat(string html) => Regex.Replace(html, "\\s+", " ");
}
