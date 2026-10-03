namespace AuroraDbManager.Api.Application.Backups;

/// <summary>
/// Calculates the checksum of a backup artifact: SHA-256 over exactly the bytes it is given,
/// written as 64 lowercase hexadecimal characters. The bytes are read as a stream; an artifact is
/// never held in memory as a whole, so its size does not matter.
/// </summary>
public interface IArtifactHasher
{
    /// <summary>The checksum of the file's content.</summary>
    Task<string> ComputeAsync(string filePath, CancellationToken cancellationToken);

    /// <summary>The checksum of everything from the stream's current position to its end.</summary>
    Task<string> ComputeAsync(Stream content, CancellationToken cancellationToken);
}
