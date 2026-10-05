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

    /// <summary>Host ports below this need privileges and belong to the system; none is ever published on.</summary>
    public const int MinExternalPort = 1024;
    public const int MaxExternalPort = 65535;

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

    /// <summary>
    /// Whether the instance's database port is published on the host. Off for a new instance, and
    /// only ever turned on by <see cref="EnableExternalAccess"/>.
    /// </summary>
    public bool ExternalAccessEnabled { get; private set; }

    /// <summary>The host port the database port is published on; set exactly while <see cref="ExternalAccessEnabled"/> is.</summary>
    public int? ExternalPort { get; private set; }

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

    /// <summary>
    /// Records that the database port is published on <paramref name="hostPort"/>. Only a running
    /// instance has a server whose port can be published.
    /// </summary>
    public void EnableExternalAccess(int hostPort, DateTime utcNow)
    {
        EnsureStatus(nameof(EnableExternalAccess), InstanceStatus.Running);
        if (ExternalAccessEnabled)
        {
            throw new InvalidOperationException($"Cannot {nameof(EnableExternalAccess)} instance {Id}: external access is already enabled.");
        }

        if (hostPort is < MinExternalPort or > MaxExternalPort)
        {
            throw new ArgumentOutOfRangeException(nameof(hostPort), hostPort, $"A host port is between {MinExternalPort} and {MaxExternalPort}.");
        }

        ExternalAccessEnabled = true;
        ExternalPort = hostPort;
        UpdatedAt = utcNow;
    }

    /// <summary>Records that the database port is no longer published, and gives its host port up.</summary>
    public void DisableExternalAccess(DateTime utcNow)
    {
        if (!ExternalAccessEnabled)
        {
            throw new InvalidOperationException($"Cannot {nameof(DisableExternalAccess)} instance {Id}: external access is not enabled.");
        }

        ExternalAccessEnabled = false;
        ExternalPort = null;
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
