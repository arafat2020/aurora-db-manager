using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Infrastructure.Backups.S3;

namespace AuroraDbManager.Api.Tests.Backups;

/// <summary>
/// An object store in memory, behind the same interface as the AWS SDK client. Like a real one it
/// shows an object only once its upload has finished. Its outcome and timing are scripted by the test.
/// </summary>
public sealed class FakeS3ObjectStore : IS3ObjectClient
{
    public const string Bucket = "aurora-test-backups";

    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private readonly Dictionary<(string Bucket, string Key), StoredObject> _objects = [];
    private readonly SemaphoreSlim _started = new(0);
    private readonly SemaphoreSlim _gate = new(0);
    private readonly Queue<BackupOperationException> _uploadFailures = new();
    private volatile bool _blocking;

    /// <param name="Content">The object's bytes.</param>
    /// <param name="ContentType">Its content type.</param>
    /// <param name="Metadata">The metadata stored with it.</param>
    /// <param name="Version">How many times an object was stored under this key.</param>
    public sealed record StoredObject(byte[] Content, string ContentType, IReadOnlyDictionary<string, string> Metadata, int Version);

    /// <summary>The buckets that exist. Uploading to any other fails the way a missing bucket does.</summary>
    public HashSet<string> Buckets { get; } = [Bucket];

    /// <summary>Every upload that was attempted, in order.</summary>
    public List<S3Upload> Uploads { get; } = [];

    /// <summary>When true every request fails as if the store could not be reached.</summary>
    public bool Unavailable { get; set; }

    /// <summary>When set, an upload stores only this many bytes of the file, as a store that lost data would.</summary>
    public int? TruncateUploadsTo { get; set; }

    /// <summary>When true an upload reports success but stores nothing.</summary>
    public bool DropUploads { get; set; }

    public int UploadCount
    {
        get
        {
            lock (_lock)
            {
                return Uploads.Count;
            }
        }
    }

    public IReadOnlyDictionary<string, StoredObject> ObjectsIn(string bucket = Bucket)
    {
        lock (_lock)
        {
            return _objects.Where(entry => entry.Key.Bucket == bucket).ToDictionary(entry => entry.Key.Key, entry => entry.Value);
        }
    }

    /// <summary>Puts an object into the store, as an upload that completed earlier would have.</summary>
    public void Put(string key, byte[] content, string contentType = "application/octet-stream", string bucket = Bucket)
    {
        lock (_lock)
        {
            var version = _objects.TryGetValue((bucket, key), out var existing) ? existing.Version + 1 : 1;
            _objects[(bucket, key)] = new StoredObject(content, contentType, new Dictionary<string, string>(), version);
        }
    }

    /// <summary>Makes the next <paramref name="count"/> uploads fail.</summary>
    public void FailNextUploads(int count, BackupOperationException? failure = null)
    {
        lock (_lock)
        {
            for (var i = 0; i < count; i++)
            {
                _uploadFailures.Enqueue(failure ?? new BackupOperationException(
                    BackupErrorCodes.BackupStorageUploadFailed,
                    "The backup could not be uploaded to the backup storage.",
                    new IOException("raw-sdk-detail: request id 4442587FB7D0A2F9 at https://s3.internal.example")));
            }
        }
    }

    /// <summary>Holds every upload at the gate until <see cref="Release"/> lets it through.</summary>
    public void Block() => _blocking = true;

    public void Release(int uploads = 1) => _gate.Release(uploads);

    /// <summary>Waits until one more upload has started.</summary>
    public async Task WaitForUploadAsync()
    {
        if (!await _started.WaitAsync(SignalTimeout))
        {
            throw new TimeoutException("No upload was started in time.");
        }
    }

    public Task<S3ObjectInfo?> FindObjectAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();

        lock (_lock)
        {
            return Task.FromResult(_objects.TryGetValue((bucket, key), out var stored)
                ? new S3ObjectInfo(stored.Content.Length, stored.ContentType)
                : null);
        }
    }

    public async Task UploadFileAsync(S3Upload upload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        BackupOperationException? failure;
        lock (_lock)
        {
            Uploads.Add(upload);
            _uploadFailures.TryDequeue(out failure);
        }

        _started.Release();
        if (_blocking)
        {
            // A real upload that is cancelled is aborted by the SDK and throws the same way.
            await _gate.WaitAsync(cancellationToken);
        }

        ThrowIfUnavailable();
        if (failure is not null)
        {
            throw failure;
        }

        if (!Buckets.Contains(upload.Bucket))
        {
            throw new BackupOperationException(
                BackupErrorCodes.BackupStorageBucketNotFound, "The backup storage bucket does not exist.");
        }

        if (DropUploads)
        {
            return;
        }

        var content = await File.ReadAllBytesAsync(upload.FilePath, cancellationToken);
        if (TruncateUploadsTo is { } length)
        {
            content = content[..Math.Min(length, content.Length)];
        }

        lock (_lock)
        {
            var version = _objects.TryGetValue((upload.Bucket, upload.Key), out var existing) ? existing.Version + 1 : 1;
            _objects[(upload.Bucket, upload.Key)] = new StoredObject(
                content, upload.ContentType, new Dictionary<string, string>(upload.Metadata), version);
        }
    }

    /// <summary>Every download that was attempted, as bucket and key, in order.</summary>
    public List<(string Bucket, string Key)> Downloads { get; } = [];

    public async Task<bool> DownloadFileAsync(string bucket, string key, string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();

        StoredObject? stored;
        lock (_lock)
        {
            Downloads.Add((bucket, key));
            _objects.TryGetValue((bucket, key), out stored);
        }

        if (stored is null)
        {
            return false;
        }

        await File.WriteAllBytesAsync(filePath, stored.Content, cancellationToken);
        return true;
    }

    private void ThrowIfUnavailable()
    {
        if (Unavailable)
        {
            throw new BackupOperationException(
                BackupErrorCodes.BackupStorageUnavailable,
                "The backup storage is not available.",
                new HttpRequestException("raw-sdk-detail: connection refused (s3.internal.example:443)"));
        }
    }
}
