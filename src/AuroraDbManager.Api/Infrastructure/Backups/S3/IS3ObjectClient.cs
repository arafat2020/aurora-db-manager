namespace AuroraDbManager.Api.Infrastructure.Backups.S3;

/// <summary>
/// The S3 operations backup storage needs, and nothing else: put a file as an object, ask
/// whether an object is there, and get an object, into a file or as a stream. All AWS SDK calls live behind this interface, so
/// <see cref="S3BackupStorage"/> can be tested without an object store. Failures are reported as
/// <see cref="Application.Backups.BackupOperationException"/> with a client-safe code and message.
/// </summary>
public interface IS3ObjectClient
{
    /// <summary>
    /// Asks the store whether the bucket is there and may be used, without reading or writing
    /// any object. Returns if it is; throws otherwise.
    /// </summary>
    Task CheckBucketAsync(string bucket, CancellationToken cancellationToken);

    /// <summary>Returns the object's properties, or null if there is no such object.</summary>
    Task<S3ObjectInfo?> FindObjectAsync(string bucket, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the file as the object, replacing an object of the same key. The file is read from
    /// disk as it is sent; it is never held in memory as a whole.
    /// </summary>
    Task UploadFileAsync(S3Upload upload, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the object into the local file, replacing it, and returns false if there is no such
    /// object. The object is written to disk as it arrives; it is never held in memory as a whole.
    /// </summary>
    Task<bool> DownloadFileAsync(string bucket, string key, string filePath, CancellationToken cancellationToken);

    /// <summary>
    /// Hands the object's content to <paramref name="read"/> as a stream, as it arrives, and
    /// returns false if there is no such object. Nothing is kept: neither in memory nor on disk.
    /// </summary>
    Task<bool> ReadObjectAsync(string bucket, string key, Func<Stream, CancellationToken, Task> read, CancellationToken cancellationToken);
}

/// <param name="SizeBytes">The object's size as the store reports it.</param>
/// <param name="ContentType">The object's content type.</param>
/// <param name="Metadata">The metadata stored with the object, by name without the <c>x-amz-meta-</c> prefix.</param>
public sealed record S3ObjectInfo(long SizeBytes, string? ContentType, IReadOnlyDictionary<string, string> Metadata);

/// <param name="Bucket">The bucket to store into.</param>
/// <param name="Key">The object's key.</param>
/// <param name="FilePath">The local file to upload.</param>
/// <param name="ContentType">The object's content type.</param>
/// <param name="Metadata">Informational metadata stored with the object. Never secrets.</param>
public sealed record S3Upload(
    string Bucket,
    string Key,
    string FilePath,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata);
