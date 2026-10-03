namespace AuroraDbManager.Api.Domain.Backups;

/// <summary>
/// A backup of one database. The status describes the backup's artifact, not the work on it: the
/// job is what runs, retries and is recovered. A backup moves <c>pending → running</c> when its
/// job first picks it up and stays <c>running</c> across the job's retries, then becomes
/// <c>completed</c>, with the artifact's location and size, or <c>failed</c>. Both are final.
/// </summary>
public sealed class Backup
{
    public const int PathMaxLength = 1024;
    public const int ChecksumLength = 64;
    public const int ErrorCodeMaxLength = 64;
    public const int ErrorMessageMaxLength = 1024;

    private Backup()
    {
    }

    public Guid Id { get; private set; }
    public Guid DatabaseId { get; private set; }
    public BackupStatus Status { get; private set; }
    public BackupStorageType StorageType { get; private set; }

    /// <summary>
    /// Where the artifact is, in terms of <see cref="StorageType"/>: a filesystem path for
    /// <see cref="BackupStorageType.Local"/>, an object key for <see cref="BackupStorageType.S3"/>.
    /// Null until the backup is <see cref="BackupStatus.Completed"/>. Internal: never returned by the API.
    /// </summary>
    public string? Path { get; private set; }

    /// <summary>Size of the finished artifact; null until the backup is <see cref="BackupStatus.Completed"/>.</summary>
    public long? SizeBytes { get; private set; }

    /// <summary>
    /// How <see cref="Checksum"/> was calculated; null exactly when there is no checksum.
    /// </summary>
    public BackupChecksumAlgorithm? ChecksumAlgorithm { get; private set; }

    /// <summary>
    /// The checksum of the artifact's bytes as they are in the storage, verified there before the
    /// backup was completed. Null until then, and null for good on a backup completed before
    /// checksums existed: such a backup is still a backup, with nothing recorded to check it against.
    /// </summary>
    public string? Checksum { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>When the backup reached <c>completed</c> or <c>failed</c>.</summary>
    public DateTime? CompletedAt { get; private set; }

    /// <summary>Why the backup is <see cref="BackupStatus.Failed"/>; null otherwise.</summary>
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }

    public static Backup Create(Guid databaseId, BackupStorageType storageType, DateTime utcNow)
    {
        if (databaseId == Guid.Empty)
        {
            throw new ArgumentException("databaseId is required.", nameof(databaseId));
        }

        return new Backup
        {
            Id = Guid.CreateVersion7(),
            DatabaseId = databaseId,
            Status = BackupStatus.Pending,
            StorageType = storageType,
            CreatedAt = utcNow
        };
    }

    /// <summary><c>pending → running</c>; the backup's job has started working on it.</summary>
    public void MarkRunning()
    {
        EnsureStatus(nameof(MarkRunning), BackupStatus.Pending);

        Status = BackupStatus.Running;
    }

    /// <summary>
    /// <c>running → completed</c>; the artifact is finished, in its final place, and its bytes
    /// there have the given checksum. A backup is never completed without one.
    /// </summary>
    public void MarkCompleted(string path, long sizeBytes, BackupChecksumAlgorithm checksumAlgorithm, string checksum, DateTime utcNow)
    {
        EnsureStatus(nameof(MarkCompleted), BackupStatus.Running);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(path.Length, PathMaxLength, nameof(path));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        if (!IsChecksum(checksum))
        {
            throw new ArgumentException($"A checksum is {ChecksumLength} lowercase hexadecimal characters.", nameof(checksum));
        }

        Status = BackupStatus.Completed;
        Path = path;
        SizeBytes = sizeBytes;
        ChecksumAlgorithm = checksumAlgorithm;
        Checksum = checksum;
        CompletedAt = utcNow;
    }

    /// <summary>Whether the text has the form of a checksum: 64 lowercase hexadecimal characters.</summary>
    public static bool IsChecksum(string? value) =>
        value is { Length: ChecksumLength } && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary><c>running → failed</c>; there is no artifact. The code and message are shown to API clients.</summary>
    public void MarkFailed(string errorCode, string errorMessage, DateTime utcNow)
    {
        EnsureStatus(nameof(MarkFailed), BackupStatus.Running);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        Status = BackupStatus.Failed;
        ErrorCode = Truncate(errorCode, ErrorCodeMaxLength);
        ErrorMessage = Truncate(errorMessage, ErrorMessageMaxLength);
        CompletedAt = utcNow;
    }

    private void EnsureStatus(string operation, BackupStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(
                $"Cannot {operation} backup {Id}: status is {Status}, expected {expected}.");
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
