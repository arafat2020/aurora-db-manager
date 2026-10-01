namespace AuroraDbManager.Api.Domain.Jobs;

/// <summary>
/// A unit of background work for an instance. A job moves
/// <c>pending → running → completed | failed</c> and only through the methods below.
/// It stays <see cref="JobStatus.Running"/> across retries; <see cref="JobStatus.Failed"/>
/// is terminal and means every attempt was used up.
/// </summary>
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

    /// <summary>Number of attempts started so far; 0 until the job is first picked up.</summary>
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

    public bool HasAttemptsRemaining => Attempt < MaxAttempts;

    public static Job Create(JobType type, Guid instanceId, int maxAttempts, DateTime utcNow)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);

        return new Job
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            Status = JobStatus.Pending,
            InstanceId = instanceId,
            Attempt = 0,
            MaxAttempts = maxAttempts,
            CreatedAt = utcNow,
            UpdatedAt = utcNow
        };
    }

    /// <summary><c>pending → running</c>; begins the first attempt.</summary>
    public void Start(DateTime utcNow)
    {
        EnsureStatus(JobStatus.Pending, nameof(Start));

        Status = JobStatus.Running;
        Attempt = 1;
        StartedAt = utcNow;
        UpdatedAt = utcNow;
    }

    /// <summary>Begins another attempt of a running job after a failed one.</summary>
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
