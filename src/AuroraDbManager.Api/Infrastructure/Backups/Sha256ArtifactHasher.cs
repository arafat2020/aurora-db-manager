using System.Security.Cryptography;
using AuroraDbManager.Api.Application.Backups;

namespace AuroraDbManager.Api.Infrastructure.Backups;

/// <summary><see cref="IArtifactHasher"/> with the platform's SHA-256, fed a buffer at a time.</summary>
public sealed class Sha256ArtifactHasher : IArtifactHasher
{
    private const int BufferSize = 1024 * 1024;

    public async Task<string> ComputeAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeAsync(file, cancellationToken);
    }

    public async Task<string> ComputeAsync(Stream content, CancellationToken cancellationToken) =>
        Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken));
}
