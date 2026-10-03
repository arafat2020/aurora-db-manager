using AuroraDbManager.Api.Infrastructure.Backups;

namespace AuroraDbManager.Api.Tests.Backups;

public sealed class TemporaryCredentialFileTests
{
    [Fact]
    public async Task Create_WritesAPrivateFile_ThatIsGoneAfterDisposal()
    {
        string path;
        await using (var file = await TemporaryCredentialFile.CreateAsync("secret content\n", default))
        {
            path = file.Path;

            Assert.Equal("secret content\n", await File.ReadAllTextAsync(path));
            Assert.StartsWith(Path.GetTempPath(), path);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Create_UsesADifferentUnguessableNameEveryTime()
    {
        await using var first = await TemporaryCredentialFile.CreateAsync("a", default);
        await using var second = await TemporaryCredentialFile.CreateAsync("a", default);

        Assert.NotEqual(first.Path, second.Path);
        Assert.DoesNotContain("secret", Path.GetFileName(first.Path));
    }

    [Fact]
    public async Task Dispose_Twice_IsHarmless()
    {
        var file = await TemporaryCredentialFile.CreateAsync("a", default);

        await file.DisposeAsync();
        await file.DisposeAsync();

        Assert.False(File.Exists(file.Path));
    }
}
