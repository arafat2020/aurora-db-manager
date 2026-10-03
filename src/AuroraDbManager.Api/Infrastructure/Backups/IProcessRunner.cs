namespace AuroraDbManager.Api.Infrastructure.Backups;

/// <summary>
/// Runs one of the backup programs. This exists so the backup managers can be tested without the
/// programs and so there is exactly one place that starts a process. It is not a way to run
/// commands: only the backup managers use it, with a program named in the server's configuration
/// and arguments they build themselves. There is no shell involved at any point; the program is
/// started directly and each argument reaches it exactly as given.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Starts the program and waits for it to exit. If <paramref name="cancellationToken"/> is
    /// cancelled, the program and its children are terminated and the call throws
    /// <see cref="OperationCanceledException"/>. If the timeout passes first they are terminated
    /// too, and the result says so.
    /// </summary>
    /// <exception cref="ProcessStartException">The program does not exist or could not be started.</exception>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}

/// <param name="Executable">The program: a name looked up on <c>PATH</c>, or a full path.</param>
/// <param name="Arguments">The arguments, one element each. Never contains secrets: command lines are visible to other users.</param>
/// <param name="Environment">Variables added to the environment the program inherits.</param>
/// <param name="Timeout">How long the program may run.</param>
/// <param name="StandardInput">Text written to the program's standard input, which is then closed; null for none.</param>
public sealed record ProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout,
    string? StandardInput = null)
{
    // Keeps arguments, environment and input out of anything that prints a request.
    public override string ToString() => $"{nameof(ProcessRequest)} {{ Executable = {Executable} }}";
}

/// <param name="ExitCode">The program's exit code; meaningless if <paramref name="TimedOut"/>.</param>
/// <param name="StandardOutput">What the program wrote to standard output, up to a fixed limit.</param>
/// <param name="StandardError">What the program wrote to standard error, up to a fixed limit.</param>
/// <param name="TimedOut">Whether the program was terminated because it ran longer than allowed.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>The program does not exist or could not be started.</summary>
public sealed class ProcessStartException(string executable, Exception innerException)
    : Exception($"The program '{executable}' could not be started.", innerException)
{
    public string Executable { get; } = executable;
}
