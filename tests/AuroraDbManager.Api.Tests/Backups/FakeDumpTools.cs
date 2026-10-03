using System.Text;
using AuroraDbManager.Api.Infrastructure.Backups;

namespace AuroraDbManager.Api.Tests.Backups;

/// <summary>
/// Stands in for <c>pg_dump</c> and <c>mysqldump</c>: a "run" writes a plausible dump into the
/// file the real program would write to, without any process being started. It records exactly
/// what the program would have been given, including the content and permissions of the
/// credential and output files at the moment it ran, and its outcome is scripted by the test.
/// </summary>
public sealed class FakeDumpTools : IProcessRunner
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    public static readonly byte[] PostgresDump = Encoding.ASCII.GetBytes("PGDMP\u0001\u000e\u0000 fake custom-format archive");

    public static readonly byte[] MysqlDump = Encoding.ASCII.GetBytes(
        "-- MySQL dump 10.13\n--\n\nCREATE TABLE `t` (`id` int);\n\n-- Dump completed on 2026-01-01  0:00:00\n");

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _started = new(0);
    private readonly SemaphoreSlim _gate = new(0);
    private readonly Queue<Func<Run, ProcessResult>> _scripted = new();
    private volatile bool _blocking;

    /// <param name="Executable">The program that would have been started.</param>
    /// <param name="Arguments">Its arguments.</param>
    /// <param name="Environment">The variables added to its environment.</param>
    /// <param name="OutputPath">The file it was told to write the dump to.</param>
    /// <param name="OutputFileMode">That file's permissions when the program started; null on Windows.</param>
    /// <param name="CredentialPath">The file it was told to read the password from.</param>
    /// <param name="CredentialContent">That file's content when the program started.</param>
    /// <param name="CredentialFileMode">That file's permissions when the program started; null on Windows.</param>
    public sealed record Run(
        string Executable,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> Environment,
        string OutputPath,
        UnixFileMode? OutputFileMode,
        string CredentialPath,
        string CredentialContent,
        UnixFileMode? CredentialFileMode,
        TimeSpan Timeout);

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

    /// <summary>When true the programs are not installed.</summary>
    public bool Missing { get; set; }

    /// <summary>What a successful run writes instead of a plausible dump; null for the default.</summary>
    public byte[]? Content { get; set; }

    /// <summary>Makes the next <paramref name="count"/> runs exit with a failure.</summary>
    public void FailNextRuns(int count, string standardError = "raw-tool-detail: something went wrong", int exitCode = 1)
    {
        for (var i = 0; i < count; i++)
        {
            Script(_ => new ProcessResult(exitCode, string.Empty, standardError, TimedOut: false));
        }
    }

    public void FailAllRuns(string standardError = "raw-tool-detail: something went wrong") => FailNextRuns(1000, standardError);

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

    public void ClearScript()
    {
        lock (_lock)
        {
            _scripted.Clear();
        }
    }

    /// <summary>Holds every run at the gate until <see cref="Release"/> lets it through.</summary>
    public void Block() => _blocking = true;

    public void Release(int runs = 1) => _gate.Release(runs);

    /// <summary>Waits until one more run has started.</summary>
    public async Task WaitForRunAsync()
    {
        if (!await _started.WaitAsync(SignalTimeout))
        {
            throw new TimeoutException("No backup program was run in time.");
        }
    }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Missing)
        {
            throw new ProcessStartException(request.Executable, new System.ComponentModel.Win32Exception(2));
        }

        var isPostgres = request.Executable.Contains("pg_dump", StringComparison.Ordinal);
        var outputPath = ArgumentValue(request, isPostgres ? "--file=" : "--result-file=");
        var credentialPath = isPostgres ? request.Environment["PGPASSFILE"] : ArgumentValue(request, "--defaults-extra-file=");

        var run = new Run(
            request.Executable,
            request.Arguments.ToList(),
            new Dictionary<string, string>(request.Environment),
            outputPath,
            ModeOf(outputPath),
            credentialPath,
            await File.ReadAllTextAsync(credentialPath, cancellationToken),
            ModeOf(credentialPath),
            request.Timeout);

        Func<Run, ProcessResult>? scripted;
        lock (_lock)
        {
            Runs.Add(run);
            _scripted.TryDequeue(out scripted);
        }

        _started.Release();
        if (_blocking)
        {
            // A real run that is cancelled has its process killed and throws the same way.
            await _gate.WaitAsync(cancellationToken);
        }

        if (scripted is not null)
        {
            return scripted(run);
        }

        await File.WriteAllBytesAsync(outputPath, Content ?? (isPostgres ? PostgresDump : MysqlDump), cancellationToken);
        return new ProcessResult(0, string.Empty, string.Empty, TimedOut: false);
    }

    private static string ArgumentValue(ProcessRequest request, string prefix) =>
        request.Arguments.Single(argument => argument.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];

    private static UnixFileMode? ModeOf(string path) =>
        OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);
}
