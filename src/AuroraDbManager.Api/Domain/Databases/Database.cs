namespace AuroraDbManager.Api.Domain.Databases;

/// <summary>
/// A logical database inside the database server of a managed instance. Engine, version and
/// resources belong to the parent instance and are not repeated here. A new database starts in
/// <see cref="DatabaseStatus.Creating"/> and becomes <see cref="DatabaseStatus.Ready"/> once it
/// exists in the engine. Deleting one moves it to <see cref="DatabaseStatus.Deleting"/>; the row
/// itself is removed only after the engine has dropped the database. An operation that failed for
/// good leaves the database <see cref="DatabaseStatus.Failed"/>, and nothing leaves that status.
/// </summary>
public sealed class Database
{
    public const int ErrorCodeMaxLength = 64;
    public const int ErrorMessageMaxLength = 1024;

    private Database()
    {
    }

    public Guid Id { get; private set; }
    public Guid InstanceId { get; private set; }
    public string Name { get; private set; } = null!;
    public DatabaseStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Why the database is <see cref="DatabaseStatus.Failed"/>; null otherwise.</summary>
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }

    public static Database Create(Guid instanceId, string name, DateTime utcNow)
    {
        if (instanceId == Guid.Empty)
        {
            throw new ArgumentException("instanceId is required.", nameof(instanceId));
        }

        if (DatabaseName.Validate(name) is { } error)
        {
            throw new ArgumentException(error, nameof(name));
        }

        return new Database
        {
            Id = Guid.CreateVersion7(),
            InstanceId = instanceId,
            Name = name,
            Status = DatabaseStatus.Creating,
            CreatedAt = utcNow,
            UpdatedAt = utcNow
        };
    }

    /// <summary><c>creating → ready</c>; the database exists in the engine.</summary>
    public void MarkReady(DateTime utcNow)
    {
        EnsureStatus(nameof(MarkReady), DatabaseStatus.Creating);

        Status = DatabaseStatus.Ready;
        UpdatedAt = utcNow;
    }

    /// <summary><c>ready → deleting</c>; deletion was requested and is carried out by a job.</summary>
    public void MarkDeleting(DateTime utcNow)
    {
        EnsureStatus(nameof(MarkDeleting), DatabaseStatus.Ready);

        Status = DatabaseStatus.Deleting;
        UpdatedAt = utcNow;
    }

    /// <summary>
    /// <c>creating → failed</c> or <c>deleting → failed</c>, when the operation failed for good.
    /// The code and message are shown to API clients.
    /// </summary>
    public void MarkFailed(string errorCode, string errorMessage, DateTime utcNow)
    {
        EnsureStatus(nameof(MarkFailed), DatabaseStatus.Creating, DatabaseStatus.Deleting);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        Status = DatabaseStatus.Failed;
        ErrorCode = Truncate(errorCode, ErrorCodeMaxLength);
        ErrorMessage = Truncate(errorMessage, ErrorMessageMaxLength);
        UpdatedAt = utcNow;
    }

    private void EnsureStatus(string operation, params DatabaseStatus[] allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw new InvalidOperationException(
                $"Cannot {operation} database {Id}: status is {Status}, expected {string.Join(" or ", allowed)}.");
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
