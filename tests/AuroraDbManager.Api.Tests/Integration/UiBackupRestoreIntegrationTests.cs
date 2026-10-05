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
/// A backup and a restore asked for through the pages, carried out for real: the UI host with
/// the real provisioner, a real PostgreSQL container, the real <c>pg_dump</c> and
/// <c>pg_restore</c>, and the real local backup storage. Opt-in: see <see cref="DockerFactAttribute"/>.
/// What the pages say is checked against what is in the database, with the engine's own client
/// inside the container.
/// </summary>
[Trait("Category", "DockerIntegration")]
public sealed class UiBackupRestoreIntegrationTests : IAsyncLifetime
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
    public async Task BackupMadeThroughThePages_IsARealBackup_AndRestoringItThroughThePages_BringsTheDataBack()
    {
        // An operator, signed in through the form: the least role that may do this.
        await _api.CreateUserAsync("the-operator", "a-perfectly-fine-password", "operator");
        // Through the form of the UI. (The API has a sign-in of the same name, which gives a token and no cookie.)
        await Browser.SignInAsync(_browser, "the-operator", "a-perfectly-fine-password");

        var created = await (await _api.PostAsync(
                ApiClientExtensions.InstancesUrl,
                System.Net.Http.Json.JsonContent.Create(ApiClientExtensions.ValidInstanceRequest(version: "16", memoryMb: 512, storageGb: 1))))
            .ReadJsonAsync(HttpStatusCode.Accepted);
        var instanceId = created.GetProperty("instance").GetProperty("id").GetGuid();
        _instanceIds.Add(instanceId);
        await _factory.ProcessJobAsync(created.GetProperty("job").GetProperty("id").GetGuid());
        var databaseId = await _factory.CreateReadyDatabaseAsync(_api, instanceId, "shop");
        var backups = $"/instances/{instanceId}/databases/{databaseId}/backups";

        Assert.Equal(0, await ExecAsync(instanceId, $"{Psql} -d shop -c \"create table orders (id int primary key, item text); insert into orders values (1, 'kept'), (2, 'also kept')\""));

        // --- Back up, through the page ---
        var response = await _browser.PostFormAsync($"{backups}/create", []);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var backupPath = response.Headers.Location!.OriginalString;
        var backupId = Guid.Parse(backupPath.Split('/').Last());
        Assert.Contains("Backup creation started.", await _browser.GetHtmlAsync(backupPath), StringComparison.Ordinal);

        await _factory.ProcessJobAsync(await JobOfAsync(backupId, JobType.BackupDatabase));

        var backup = await _factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync());
        var page = Flat(await _browser.GetHtmlAsync(backupPath));
        Assert.Equal(BackupStatus.Completed, backup.Status);
        Assert.Matches("</h1> <span class=\"badge badge-success\"><svg.*?</svg> Completed</span>", page);
        Assert.Matches("</svg> Verified</span>", page);
        // The page shows the checksum of the file that is really there.
        var file = Assert.Single(_factory.BackupFiles());
        var checksum = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(file)));
        Assert.Contains($"<code class=\"checksum\">{checksum}</code>", page, StringComparison.Ordinal);
        Assert.Equal(new FileInfo(file).Length, backup.SizeBytes);
        Assert.Contains($"{backup.SizeBytes!.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes", page, StringComparison.Ordinal);
        Assert.DoesNotContain(file, page, StringComparison.Ordinal);

        // --- The data changes after the backup ---
        Assert.Equal(0, await ExecAsync(instanceId, $"{Psql} -d shop -c \"delete from orders; insert into orders values (3, 'made after the backup'); create table scratch (x int)\""));

        // --- Restore, through the page ---
        var question = await _browser.GetHtmlAsync($"{backupPath}/restore");
        Assert.Contains("Restoring this backup replaces the current contents of the database", question, StringComparison.Ordinal);
        Assert.Equal(0, await ExecAsync(instanceId, $"{Psql} -d shop -Atc 'select item from orders' | grep -q 'made after the backup'"));

        var restore = await _browser.PostFormAsync($"{backupPath}/restore", []);
        Assert.Equal(HttpStatusCode.Redirect, restore.StatusCode);
        var started = Flat(await _browser.GetHtmlAsync(backupPath));
        Assert.Contains("Restore started.", started, StringComparison.Ordinal);
        Assert.DoesNotContain("Restore completed", started, StringComparison.Ordinal);
        // Asked for, not done: the database is still what it was a moment ago.
        Assert.Equal(0, await ExecAsync(instanceId, $"{Psql} -d shop -Atc 'select item from orders' | grep -q 'made after the backup'"));

        var restoreJob = await JobOfAsync(backupId, JobType.RestoreDatabase);
        await _factory.ProcessJobAsync(restoreJob);

        var finished = Flat(await _browser.GetHtmlAsync(backupPath));
        Assert.Equal("completed", (await _api.GetJobAsync(restoreJob)).Status());
        Assert.Contains("<div class=\"alert-title\">Restore completed</div>", finished, StringComparison.Ordinal);
        // What the backup held is back, and what came after it is gone.
        Assert.Equal(0, await ExecAsync(instanceId, $"{Psql} -d shop -Atc 'select count(*) from orders' | grep -qx 2"));
        Assert.Equal(0, await ExecAsync(instanceId, $"{Psql} -d shop -Atc 'select item from orders order by id' | head -1 | grep -qx kept"));
        Assert.NotEqual(0, await ExecAsync(instanceId, $"{Psql} -d shop -Atc 'select item from orders' | grep -q 'made after the backup'"));
        Assert.NotEqual(0, await ExecAsync(instanceId, $"{Psql} -d shop -c 'select 1 from scratch'"));
        Assert.Equal("ready", (await _api.GetDatabaseAsync(databaseId)).Status());
    }

    private Task<Guid> JobOfAsync(Guid backupId, JobType type) => _factory.WithDbAsync(db => db.Jobs
        .Where(job => job.BackupId == backupId && job.Type == type)
        .Select(job => job.Id)
        .SingleAsync());

    private Task<int> ExecAsync(Guid instanceId, string shellCommand) =>
        _engine.ExecAsync(DockerResourceNaming.ContainerName(instanceId), ["sh", "-c", shellCommand], default);

    private static string Flat(string html) => Regex.Replace(html, "\\s+", " ");
}
