namespace AuroraDbManager.Api.Infrastructure.Backups;

/// <summary>
/// A file through which a backup program is given a password, so the password appears neither on
/// its command line nor in its environment. The file is created readable by the API's user only
/// (0600 on Unix), under a random name in the system's temporary directory, and is deleted when
/// disposed, which is as soon as the program has exited.
/// </summary>
public sealed class TemporaryCredentialFile : IAsyncDisposable
{
    private TemporaryCredentialFile(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static async Task<TemporaryCredentialFile> CreateAsync(string content, CancellationToken cancellationToken)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aurora-backup-{Guid.NewGuid():N}.credentials");

        // CreateNew: never writes into a file that someone else put there first.
        var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
        {
            create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var file = new TemporaryCredentialFile(path);
        try
        {
            await using var stream = new FileStream(path, create);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(content.AsMemory(), cancellationToken);
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }

        return file;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done here; the file stays private to the API's user.
        }

        return ValueTask.CompletedTask;
    }
}
