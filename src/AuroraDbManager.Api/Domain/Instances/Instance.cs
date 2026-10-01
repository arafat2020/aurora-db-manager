namespace AuroraDbManager.Api.Domain.Instances;

/// <summary>
/// Metadata for a managed database instance. A new instance starts in
/// <see cref="InstanceStatus.Provisioning"/> and leaves it only through
/// <see cref="MarkRunning"/> or <see cref="MarkFailed"/>. A running instance whose database
/// cannot be brought back becomes failed as well. Nothing leaves <see cref="InstanceStatus.Failed"/>.
/// </summary>
public sealed class Instance
{
    public const int NameMaxLength = 100;
    public const int VersionMaxLength = 32;
    public const int ErrorCodeMaxLength = 64;
    public const int ErrorMessageMaxLength = 1024;

    private Instance()
    {
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public InstanceEngine Engine { get; private set; }
    public string Version { get; private set; } = null!;
    public InstanceStatus Status { get; private set; }
    public int Cpu { get; private set; }
    public int MemoryMb { get; private set; }
    public int StorageGb { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Why the instance is <see cref="InstanceStatus.Failed"/>; null otherwise.</summary>
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }

    public static Instance Create(
        string name,
        InstanceEngine engine,
        string version,
        int cpu,
        int memoryMb,
        int storageGb,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cpu);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryMb);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(storageGb);

        return new Instance
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Engine = engine,
            Version = version,
            Status = InstanceStatus.Provisioning,
            Cpu = cpu,
            MemoryMb = memoryMb,
            StorageGb = storageGb,
            CreatedAt = utcNow,
            UpdatedAt = utcNow
        };
    }

    /// <summary><c>provisioning → running</c>; provisioning succeeded.</summary>
    public void MarkRunning(DateTime utcNow)
    {
        EnsureStatus(nameof(MarkRunning), InstanceStatus.Provisioning);

        Status = InstanceStatus.Running;
        UpdatedAt = utcNow;
    }

    /// <summary>
    /// <c>provisioning → failed</c> when provisioning failed for good, or <c>running → failed</c>
    /// when the instance's database could not be brought back. The code and message are shown to
    /// API clients.
    /// </summary>
    public void MarkFailed(string errorCode, string errorMessage, DateTime utcNow)
    {
        EnsureStatus(nameof(MarkFailed), InstanceStatus.Provisioning, InstanceStatus.Running);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        Status = InstanceStatus.Failed;
        ErrorCode = Truncate(errorCode, ErrorCodeMaxLength);
        ErrorMessage = Truncate(errorMessage, ErrorMessageMaxLength);
        UpdatedAt = utcNow;
    }

    private void EnsureStatus(string operation, params InstanceStatus[] allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw new InvalidOperationException(
                $"Cannot {operation} instance {Id}: status is {Status}, expected {string.Join(" or ", allowed)}.");
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
