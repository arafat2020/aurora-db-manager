namespace AuroraDbManager.Api.Domain.Jobs;

/// <summary>
/// A unit of background work for an instance, or for one database of an instance. A job moves
/// <c>pending → running → completed | failed</c> and only through the methods below.
/// It stays <see cref="JobStatus.Running"/> across retries; <see cref="JobStatus.Failed"/>
/// is terminal and means every attempt was used up.
/// </summary>
/// <remarks>
/// A running job is owned by one execution through a lease: <see cref="LeaseId"/> identifies the
/// owner and <see cref="LeaseExpiresAt"/> is extended while the owner is alive. Once the lease has
/// expired the owner is presumed dead, and the job may be returned to <c>pending</c> with
/// <see cref="ReturnToPending"/> so it can run again.
/// </remarks>
public sealed class Job
{
    public const int ErrorCodeMaxLength = 64;
    public const int ErrorMessageMaxLength = 1024;

    private Job()
    {
    }

    public Guid Id { get; private set; }
    public JobType Type { get; private set; }
    public JobStatus Status { get; private set; }
    public Guid InstanceId { get; private set; }

    /// <summary>
    /// The database a <c>create_database</c> or <c>delete_database</c> job works on; null for every
    /// other type. Kept after the database is gone, so a finished job still says what it was for.
    /// </summary>
    public Guid? DatabaseId { get; private set; }

    /// <summary>
    /// Number of the current attempt; 0 until the job is first picked up. An attempt that was
    /// interrupted, by a shutdown or a crash, is not counted and runs again under the same number.
    /// </summary>
    public int Attempt { get; private set; }
    public int MaxAttempts { get; private set; }

    /// <summary>Error of the most recent failed attempt. Cleared when the job completes.</summary>
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime? StartedAt { get; private set; }

    /// <summary>When the job reached <c>completed</c> or <c>failed</c>.</summary>
    public DateTime? CompletedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Identifies the execution that owns this running job; null unless running.</summary>
    public Guid? LeaseId { get; private set; }

    /// <summary>Until when the owner's claim holds without being renewed; null unless running.</summary>
    public DateTime? LeaseExpiresAt { get; private set; }

    public bool HasAttemptsRemaining => Attempt < MaxAttempts;

    public static Job Create(JobType type, Guid instanceId, int maxAttempts, DateTime utcNow, Guid? databaseId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);

        var worksOnDatabase = type is JobType.CreateDatabase or JobType.DeleteDatabase;
        if (worksOnDatabase != databaseId.HasValue)
        {
            throw new ArgumentException(
                worksOnDatabase ? $"A {type} job needs a database." : $"A {type} job does not work on a database.",
                nameof(databaseId));
        }

        return new Job
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            Status = JobStatus.Pending,
            InstanceId = instanceId,
            DatabaseId = databaseId,
            Attempt = 0,
            MaxAttempts = maxAttempts,
            CreatedAt = utcNow,
            UpdatedAt = utcNow
        };
    }

    /// <summary><c>pending → running</c>; begins an attempt under the given lease.</summary>
    public void Start(Guid leaseId, DateTime leaseExpiresAt, DateTime utcNow)
    {
        EnsureStatus(JobStatus.Pending, nameof(Start));

        Status = JobStatus.Running;
        Attempt++;
        LeaseId = leaseId;
        LeaseExpiresAt = leaseExpiresAt;
        StartedAt ??= utcNow;
        UpdatedAt = utcNow;
    }

    /// <summary>
    /// <c>running → pending</c>; gives up an attempt that was interrupted before it finished, so the
    /// job can be picked up again. The interrupted attempt is not counted.
    /// </summary>
    public void ReturnToPending(DateTime utcNow)
    {
        EnsureStatus(JobStatus.Running, nameof(ReturnToPending));

        Status = JobStatus.Pending;
        Attempt--;
        LeaseId = null;
        LeaseExpiresAt = null;
        UpdatedAt = utcNow;
    }

    /// <summary>Begins another attempt of a running job after a failed one. The lease is kept.</summary>
    public void StartNextAttempt(DateTime utcNow)
    {
        EnsureStatus(JobStatus.Running, nameof(StartNextAttempt));
        if (!HasAttemptsRemaining)
        {
            throw new InvalidOperationException($"Job {Id} has used all {MaxAttempts} attempts.");
        }

        Attempt++;
        UpdatedAt = utcNow;
    }

    /// <summary><c>running → completed</c>.</summary>
    public void Complete(DateTime utcNow)
    {
        EnsureStatus(JobStatus.Running, nameof(Complete));

        Status = JobStatus.Completed;
        ErrorCode = null;
        ErrorMessage = null;
        LeaseId = null;
        LeaseExpiresAt = null;
        CompletedAt = utcNow;
        UpdatedAt = utcNow;
    }

    /// <summary>
    /// Records that the current attempt failed. The job stays <c>running</c> while attempts
    /// remain and becomes <c>failed</c> once they are used up.
    /// </summary>
    public void FailAttempt(string errorCode, string errorMessage, DateTime utcNow)
    {
        EnsureStatus(JobStatus.Running, nameof(FailAttempt));
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        ErrorCode = Truncate(errorCode, ErrorCodeMaxLength);
        ErrorMessage = Truncate(errorMessage, ErrorMessageMaxLength);
        UpdatedAt = utcNow;

        if (!HasAttemptsRemaining)
        {
            Status = JobStatus.Failed;
            LeaseId = null;
            LeaseExpiresAt = null;
            CompletedAt = utcNow;
        }
    }

    private void EnsureStatus(JobStatus expected, string operation)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(
                $"Cannot {operation} job {Id}: status is {Status}, expected {expected}.");
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
