using System.Diagnostics;
using AuroraDbManager.Api.Infrastructure.Backups;

namespace AuroraDbManager.Api.Tests.Backups;

/// <summary>
/// The real process runner, driven with <c>/bin/sh</c> as the program under test. The shell is
/// only what these tests run; the runner itself never involves one. Not for Windows.
/// </summary>
public sealed class SystemProcessRunnerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly SystemProcessRunner _runner = new();

    private static ProcessRequest Shell(string script, TimeSpan? timeout = null, params string[] arguments) =>
        new("/bin/sh", ["-c", script, "sh", .. arguments], new Dictionary<string, string>(), timeout ?? Generous);

    [Fact]
    public async Task Success_ReturnsExitCodeZeroAndBothStreams()
    {
        var result = await _runner.RunAsync(Shell("echo to-stdout; echo to-stderr >&2"), default);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal("to-stdout\n", result.StandardOutput);
        Assert.Equal("to-stderr\n", result.StandardError);
    }

    [Fact]
    public async Task NonZeroExit_IsReturnedNotThrown_WithItsDiagnostics()
    {
        var result = await _runner.RunAsync(Shell("echo broken >&2; exit 7"), default);

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal("broken\n", result.StandardError);
    }

    [Fact]
    public async Task Arguments_ReachTheProgramExactlyAsGiven_WhateverTheyContain()
    {
        string[] arguments =
        [
            "plain",
            "two words",
            "",
            "quote\"and'apostrophe",
            "$(echo injected)",
            "`echo injected`",
            "; echo injected",
            "&& echo injected",
            "| cat /etc/passwd",
            "--file=/tmp/a b/c.dump",
            "back\\slash",
            "*"
        ];

        // Prints every argument on a line of its own, in brackets.
        var result = await _runner.RunAsync(Shell("for a in \"$@\"; do printf '[%s]\\n' \"$a\"; done", null, arguments), default);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Concat(arguments.Select(argument => $"[{argument}]\n")), result.StandardOutput);
        Assert.Equal(arguments.Length, result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task Environment_IsAddedToTheInheritedOne()
    {
        var request = new ProcessRequest(
            "/bin/sh",
            ["-c", "printf '%s|%s' \"$AURORA_TEST_VALUE\" \"${PATH:+path-inherited}\""],
            new Dictionary<string, string> { ["AURORA_TEST_VALUE"] = "from the request; $(not run)" },
            Generous);

        var result = await _runner.RunAsync(request, default);

        Assert.Equal("from the request; $(not run)|path-inherited", result.StandardOutput);
    }

    [Fact]
    public async Task StandardInput_IsWrittenAndClosed()
    {
        var request = new ProcessRequest("/bin/cat", [], new Dictionary<string, string>(), Generous, StandardInput: "fed through stdin\n");

        var result = await _runner.RunAsync(request, default);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fed through stdin\n", result.StandardOutput);
    }

    [Fact]
    public async Task NoStandardInput_ProgramSeesEndOfInput_AndDoesNotHang()
    {
        var result = await _runner.RunAsync(new ProcessRequest("/bin/cat", [], new Dictionary<string, string>(), Generous), default);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    [Fact]
    public async Task LargeOutput_DoesNotBlockTheProgram_AndIsCappedInMemory()
    {
        // About 2 MB on each stream; a program whose pipes are not drained would never exit.
        var result = await _runner.RunAsync(
            Shell("i=0; while [ $i -lt 2000 ]; do printf '%01000d\\n' 0; printf '%01000d\\n' 0 >&2; i=$((i+1)); done"), default);

        Assert.Equal(0, result.ExitCode);
        Assert.InRange(result.StandardOutput.Length, 1, 64 * 1024);
        Assert.InRange(result.StandardError.Length, 1, 64 * 1024);
    }

    [Fact]
    public async Task Timeout_TerminatesTheProgram_AndSaysSo()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"aurora-runner-{Guid.NewGuid():N}");
        var started = Stopwatch.StartNew();

        // If the program survived the timeout it would create the marker.
        var result = await _runner.RunAsync(
            Shell($"echo started; sleep 3; touch '{marker}'", TimeSpan.FromMilliseconds(300)), default);

        Assert.True(result.TimedOut);
        Assert.Equal("started\n", result.StandardOutput);
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2.5));
        await Task.Delay(TimeSpan.FromSeconds(3.5) - started.Elapsed);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Cancellation_TerminatesTheProgramAndItsChildren_AndThrows()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"aurora-runner-{Guid.NewGuid():N}");
        using var cancellation = new CancellationTokenSource();
        var started = Stopwatch.StartNew();

        // The work is done by a child of the shell; killing only the shell would let it finish.
        var running = _runner.RunAsync(Shell($"(sleep 3; touch '{marker}') & wait"), cancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2.5));
        await Task.Delay(TimeSpan.FromSeconds(3.5) - started.Elapsed);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task AlreadyCancelled_StartsNothing()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"aurora-runner-{Guid.NewGuid():N}");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _runner.RunAsync(Shell($"touch '{marker}'"), new CancellationToken(canceled: true)));

        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData("aurora-no-such-program")]
    [InlineData("/nonexistent/dir/pg_dump")]
    public async Task ProgramThatDoesNotExist_ThrowsProcessStartException(string executable)
    {
        var exception = await Assert.ThrowsAsync<ProcessStartException>(
            () => _runner.RunAsync(new ProcessRequest(executable, [], new Dictionary<string, string>(), Generous), default));

        Assert.Equal(executable, exception.Executable);
    }

    [Fact]
    public void Request_PrintsOnlyItsProgram()
    {
        var request = new ProcessRequest(
            "pg_dump", ["--file=/secret/path"], new Dictionary<string, string> { ["PGPASSFILE"] = "/tmp/x" }, Generous, "stdin-secret");

        Assert.Equal("ProcessRequest { Executable = pg_dump }", request.ToString());
    }
}
