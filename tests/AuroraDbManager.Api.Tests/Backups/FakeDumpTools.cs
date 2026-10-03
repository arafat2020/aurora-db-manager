using System.Text;
using AuroraDbManager.Api.Infrastructure.Backups;

namespace AuroraDbManager.Api.Tests.Backups;

/// <summary>
/// Stands in for <c>pg_dump</c>, <c>mysqldump</c>, <c>pg_restore</c> and <c>mysql</c>, without
/// any process being started. A dump "run" writes a plausible dump into the file the real program
/// would write to; a restore "run" reads the file the real program would load. It records exactly
/// what the program would have been given, including the content and permissions of the files
/// involved at the moment it ran, and its outcome is scripted by the test.
/// </summary>
public sealed class FakeDumpTools : IProcessRunner
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    /// <summary><c>pg_dump</c> writing an archive.</summary>
    public const string PgDump = "pg_dump";

    /// <summary><c>mysqldump</c> writing a script.</summary>
    public const string MySqlDump = "mysqldump";

    /// <summary><c>pg_restore --list</c>: reading an archive without a server.</summary>
    public const string PgRestoreList = "pg_restore --list";

    /// <summary><c>pg_restore</c> loading an archive into a database.</summary>
    public const string PgRestore = "pg_restore";

    /// <summary><c>mysql --version</c>.</summary>
    public const string MySqlVersion = "mysql --version";

    /// <summary><c>mysql</c> loading a script into a database.</summary>
    public const string MySql = "mysql";

    public static readonly byte[] PostgresDump = Encoding.ASCII.GetBytes("PGDMP\u0001\u000e\u0000 fake custom-format archive");

    public static readonly byte[] MysqlDump = Encoding.ASCII.GetBytes(
        "-- MySQL dump 10.13\n--\n\nCREATE TABLE `t` (`id` int);\n\n-- Dump completed on 2026-01-01  0:00:00\n");

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _started = new(0);
    private readonly SemaphoreSlim _gate = new(0);
    private readonly Queue<Func<Run, ProcessResult>> _scripted = new();
    private readonly Dictionary<string, Queue<Func<Run, ProcessResult>>> _scriptedByKind = [];
    private volatile bool _blocking;
    private volatile string? _blockingKind;

    /// <param name="Kind">Which program ran, and in which mode: one of the constants of this class.</param>
    /// <param name="Executable">The program that would have been started.</param>
    /// <param name="Arguments">Its arguments.</param>
    /// <param name="Environment">The variables added to its environment.</param>
    /// <param name="OutputPath">The file a dump program was told to write to; empty for a restore program.</param>
    /// <param name="OutputFileMode">That file's permissions when the program started; null if none or on Windows.</param>
    /// <param name="CredentialPath">The file it was told to read the password from; empty if it was given none.</param>
    /// <param name="CredentialContent">That file's content when the program started.</param>
    /// <param name="CredentialFileMode">That file's permissions when the program started; null if none or on Windows.</param>
    /// <param name="Timeout">How long it was allowed to run.</param>
    /// <param name="InputPath">The file a restore program was told to read, as an argument or as standard input; empty for a dump program.</param>
    /// <param name="InputContent">That file's content when the program started; null if there was none.</param>
    /// <param name="InputIsStandardInput">Whether the input file was to be streamed to standard input rather than named as an argument.</param>
    /// <param name="StandardInputText">Text that was to be written to standard input ahead of any file; null for none.</param>
    public sealed record Run(
        string Kind,
        string Executable,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> Environment,
        string OutputPath,
        UnixFileMode? OutputFileMode,
        string CredentialPath,
        string CredentialContent,
        UnixFileMode? CredentialFileMode,
        TimeSpan Timeout,
        string InputPath = "",
        byte[]? InputContent = null,
        bool InputIsStandardInput = false,
        string? StandardInputText = null);

    public List<Run> Runs { get; } = [];

    public int RunCount
    {
        get
        {
            lock (_lock)
            {
                return Runs.Count;
            }
        }
    }

    public IReadOnlyList<Run> RunsOf(string kind)
    {
        lock (_lock)
        {
            return Runs.Where(run => run.Kind == kind).ToList();
        }
    }

    /// <summary>When true the programs are not installed.</summary>
    public bool Missing { get; set; }

    /// <summary>What a successful dump writes instead of a plausible dump; null for the default.</summary>
    public byte[]? Content { get; set; }

    /// <summary>Called for every run, when it starts.</summary>
    public Action<Run>? OnRun { get; set; }

    /// <summary>Makes the next <paramref name="count"/> runs, of whatever program, exit with a failure.</summary>
    public void FailNextRuns(int count, string standardError = "raw-tool-detail: something went wrong", int exitCode = 1)
    {
        for (var i = 0; i < count; i++)
        {
            Script(_ => new ProcessResult(exitCode, string.Empty, standardError, TimedOut: false));
        }
    }

    public void FailAllRuns(string standardError = "raw-tool-detail: something went wrong") => FailNextRuns(1000, standardError);

    /// <summary>Makes the next <paramref name="count"/> runs of one kind exit with a failure; other kinds are unaffected.</summary>
    public void FailNext(string kind, int count, string standardError = "raw-tool-detail: something went wrong", int exitCode = 1)
    {
        for (var i = 0; i < count; i++)
        {
            Script(kind, _ => new ProcessResult(exitCode, string.Empty, standardError, TimedOut: false));
        }
    }

    /// <summary>Makes the next run write half a dump and then fail, like a program killed mid-way.</summary>
    public void WritePartOfADumpThenFail() =>
        Script(run =>
        {
            File.WriteAllBytes(run.OutputPath, "PGDMP half a du"u8.ToArray());
            return new ProcessResult(1, string.Empty, "raw-tool-detail: connection lost", TimedOut: false);
        });

    /// <summary>Makes the next run exceed its timeout.</summary>
    public void TimeOutNextRun() => Script(_ => new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));

    public void Script(Func<Run, ProcessResult> outcome)
    {
        lock (_lock)
        {
            _scripted.Enqueue(outcome);
        }
    }

    public void Script(string kind, Func<Run, ProcessResult> outcome)
    {
        lock (_lock)
        {
            if (!_scriptedByKind.TryGetValue(kind, out var queue))
            {
                _scriptedByKind[kind] = queue = new Queue<Func<Run, ProcessResult>>();
            }

            queue.Enqueue(outcome);
        }
    }

    public void ClearScript()
    {
        lock (_lock)
        {
            _scripted.Clear();
            _scriptedByKind.Clear();
        }
    }

    /// <summary>Holds every run at the gate until <see cref="Release"/> lets it through.</summary>
    public void Block()
    {
        ForgetStartedRuns();
        _blocking = true;
    }

    /// <summary>Holds only runs of one kind at the gate.</summary>
    public void Block(string kind)
    {
        ForgetStartedRuns();
        _blockingKind = kind;
        _blocking = true;
    }

    // Runs that started before the block was set are not what a test is about to wait for.
    private void ForgetStartedRuns()
    {
        while (_started.Wait(0))
        {
        }
    }

    public void Release(int runs = 1) => _gate.Release(runs);

    /// <summary>Waits until one more run that is held at the gate has started.</summary>
    public async Task WaitForRunAsync()
    {
        if (!await _started.WaitAsync(SignalTimeout))
        {
            throw new TimeoutException("No program was run in time.");
        }
    }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Missing)
        {
            throw new ProcessStartException(request.Executable, new System.ComponentModel.Win32Exception(2));
        }

        var run = await DescribeAsync(request, cancellationToken);

        Func<Run, ProcessResult>? scripted;
        lock (_lock)
        {
            Runs.Add(run);
            if (!(_scriptedByKind.TryGetValue(run.Kind, out var ofKind) && ofKind.TryDequeue(out scripted)))
            {
                // The general script is for whatever runs next; preflight runs do not use it up.
                scripted = run.Kind is PgRestoreList or MySqlVersion ? null : _scripted.TryDequeue(out var next) ? next : null;
            }
        }

        OnRun?.Invoke(run);

        var held = _blocking && (_blockingKind is null || _blockingKind == run.Kind);
        if (_blockingKind is null || held)
        {
            _started.Release();
        }

        if (held)
        {
            // A real run that is cancelled has its process killed and throws the same way.
            await _gate.WaitAsync(cancellationToken);
        }

        if (scripted is not null)
        {
            return scripted(run);
        }

        switch (run.Kind)
        {
            case PgDump or MySqlDump:
                await File.WriteAllBytesAsync(
                    run.OutputPath, Content ?? (run.Kind == PgDump ? PostgresDump : MysqlDump), cancellationToken);
                return Success();

            // Like the real one: an archive it cannot read is an error.
            case PgRestoreList when !(run.InputContent ?? []).AsSpan().StartsWith("PGDMP"u8):
                return new ProcessResult(1, string.Empty, "pg_restore: error: input file does not appear to be a valid archive", TimedOut: false);

            // Like the real one: its listing says how many entries the archive has.
            case PgRestoreList:
                return new ProcessResult(0, ";\n; Archive created at 2026-01-01 00:00:00 UTC\n;     TOC Entries: 6\n;     Format: CUSTOM\n", string.Empty, TimedOut: false);

            default:
                return Success();
        }

        static ProcessResult Success() => new(0, string.Empty, string.Empty, TimedOut: false);
    }

    private static async Task<Run> DescribeAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(request.Executable);
        string kind, outputPath = string.Empty, credentialPath = string.Empty, inputPath = string.Empty;
        var standardInput = false;

        if (name.Contains("pg_dump", StringComparison.Ordinal))
        {
            kind = PgDump;
            outputPath = ArgumentValue(request, "--file=");
            credentialPath = request.Environment["PGPASSFILE"];
        }
        else if (name.Contains("mysqldump", StringComparison.Ordinal))
        {
            kind = MySqlDump;
            outputPath = ArgumentValue(request, "--result-file=");
            credentialPath = ArgumentValue(request, "--defaults-extra-file=");
        }
        else if (name.Contains("pg_restore", StringComparison.Ordinal))
        {
            var listing = request.Arguments.Contains("--list");
            kind = listing ? PgRestoreList : PgRestore;
            inputPath = request.Arguments[^1];
            credentialPath = listing ? string.Empty : request.Environment["PGPASSFILE"];
        }
        else if (request.Arguments.SequenceEqual(["--version"]))
        {
            kind = MySqlVersion;
        }
        else
        {
            kind = MySql;
            credentialPath = ArgumentValue(request, "--defaults-extra-file=");
            inputPath = request.StandardInputFilePath ?? string.Empty;
            standardInput = true;
        }

        return new Run(
            kind,
            request.Executable,
            request.Arguments.ToList(),
            new Dictionary<string, string>(request.Environment),
            outputPath,
            outputPath.Length == 0 ? null : ModeOf(outputPath),
            credentialPath,
            credentialPath.Length == 0 ? string.Empty : await File.ReadAllTextAsync(credentialPath, cancellationToken),
            credentialPath.Length == 0 ? null : ModeOf(credentialPath),
            request.Timeout,
            inputPath,
            inputPath.Length == 0 ? null : await File.ReadAllBytesAsync(inputPath, cancellationToken),
            standardInput,
            request.StandardInput);
    }

    private static string ArgumentValue(ProcessRequest request, string prefix) =>
        request.Arguments.Single(argument => argument.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];

    private static UnixFileMode? ModeOf(string path) =>
        OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);
}
