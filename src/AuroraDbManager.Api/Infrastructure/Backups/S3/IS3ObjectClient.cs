namespace AuroraDbManager.Api.Infrastructure.Backups.S3;

/// <summary>
/// The two S3 operations backup storage needs, and nothing else: put a file as an object, and
/// ask whether an object is there. All AWS SDK calls live behind this interface, so
/// <see cref="S3BackupStorage"/> can be tested without an object store. Failures are reported as
/// <see cref="Application.Backups.BackupOperationException"/> with a client-safe code and message.
/// </summary>
public interface IS3ObjectClient
{
    /// <summary>Returns the object's properties, or null if there is no such object.</summary>
    Task<S3ObjectInfo?> FindObjectAsync(string bucket, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the file as the object, replacing an object of the same key. The file is read from
    /// disk as it is sent; it is never held in memory as a whole.
    /// </summary>
    Task UploadFileAsync(S3Upload upload, CancellationToken cancellationToken);
}

/// <param name="SizeBytes">The object's size as the store reports it.</param>
/// <param name="ContentType">The object's content type.</param>
public sealed record S3ObjectInfo(long SizeBytes, string? ContentType);

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
