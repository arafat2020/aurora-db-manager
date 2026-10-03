using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Infrastructure.Backups;

namespace AuroraDbManager.Api.Tests.Backups;

/// <summary>
/// The real SHA-256 hasher, with the means for a test to make it fail, to hold it, and to act
/// right after a file has been hashed, which is where an artifact could change between the
/// checksum being taken and the artifact being stored.
/// </summary>
public sealed class FaultInjectingHasher : IArtifactHasher
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private readonly Sha256ArtifactHasher _real = new();
    private readonly SemaphoreSlim _started = new(0);
    private readonly SemaphoreSlim _gate = new(0);
    private volatile bool _blocking;
    private int _failuresRemaining;
    private int _computations;

    /// <summary>How many checksums were asked for.</summary>
    public int Computations => Volatile.Read(ref _computations);

    /// <summary>Called with the path of every file that has just been hashed.</summary>
    public Action<string>? AfterFileHashed { get; set; }

    /// <summary>Makes the next <paramref name="count"/> calculations fail the way an unreadable file does.</summary>
    public void FailNextComputations(int count) => Volatile.Write(ref _failuresRemaining, count);

    /// <summary>Holds every calculation until <see cref="Release"/> lets it through.</summary>
    public void Block() => _blocking = true;

    public void Release(int computations = 1) => _gate.Release(computations);

    /// <summary>Waits until one more calculation has started.</summary>
    public async Task WaitForComputationAsync()
    {
        if (!await _started.WaitAsync(SignalTimeout))
        {
            throw new TimeoutException("No checksum was calculated in time.");
        }
    }

    public async Task<string> ComputeAsync(string filePath, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        var checksum = await _real.ComputeAsync(filePath, cancellationToken);
        AfterFileHashed?.Invoke(filePath);
        return checksum;
    }

    public async Task<string> ComputeAsync(Stream content, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        return await _real.ComputeAsync(content, cancellationToken);
    }

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _computations);
        _started.Release();
        if (_blocking)
        {
            await _gate.WaitAsync(cancellationToken);
        }

        if (Interlocked.Decrement(ref _failuresRemaining) >= 0)
        {
            throw new IOException("raw-io-detail: the file could not be read");
        }

        Volatile.Write(ref _failuresRemaining, 0);
    }
}
