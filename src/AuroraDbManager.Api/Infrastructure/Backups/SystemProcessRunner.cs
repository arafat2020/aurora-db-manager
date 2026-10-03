using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AuroraDbManager.Api.Infrastructure.Backups;

/// <summary><see cref="IProcessRunner"/> over <see cref="Process"/>.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    // A dump goes to a file, not to these streams; what arrives here is diagnostics.
    private const int MaxCapturedCharacters = 64 * 1024;

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable,
            // No shell: the program is started directly, and ArgumentList passes every element
            // as one argument whatever characters it contains.
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in request.Environment)
        {
            startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new ProcessStartException(request.Executable, exception);
        }

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        // Both streams are read while the program runs; a program blocked on a full pipe never exits.
        var standardOutput = ReadAsync(process.StandardOutput);
        var standardError = ReadAsync(process.StandardError);

        try
        {
            try
            {
                if (request.StandardInput is not null)
                {
                    await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), stop.Token);
                    await process.StandardInput.FlushAsync(stop.Token);
                }

                if (request.StandardInputFilePath is not null)
                {
                    // Bytes, as they are in the file, a buffer at a time.
                    await using var input = File.OpenRead(request.StandardInputFilePath);
                    await input.CopyToAsync(process.StandardInput.BaseStream, stop.Token);
                    await process.StandardInput.BaseStream.FlushAsync(stop.Token);
                }

                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The program exited, or closed its input, without reading it.
            }

            await process.WaitForExitAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
            Terminate(process);
            await Task.WhenAll(standardOutput, standardError);

            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessResult(-1, await standardOutput, await standardError, TimedOut: true);
        }

        return new ProcessResult(process.ExitCode, await standardOutput, await standardError, TimedOut: false);
    }

    private static void Terminate(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // It has exited in the meantime.
        }

        // Reaps the process and lets the output readers reach the end of their streams.
        process.WaitForExit();
    }

    /// <summary>Reads a stream to its end, keeping at most <see cref="MaxCapturedCharacters"/> of it.</summary>
    private static async Task<string> ReadAsync(StreamReader reader)
    {
        var captured = new StringBuilder();
        var buffer = new char[4096];

        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
            {
                captured.Append(buffer, 0, Math.Min(read, MaxCapturedCharacters - captured.Length));
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The stream went away with the process; what was read so far is what there is.
        }

        return captured.ToString();
    }
}
