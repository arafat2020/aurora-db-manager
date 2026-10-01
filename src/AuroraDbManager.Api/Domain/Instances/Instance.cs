namespace AuroraDbManager.Api.Domain.Instances;

/// <summary>
/// Metadata for a managed database instance. A new instance starts in
/// <see cref="InstanceStatus.Provisioning"/> and leaves it only through
/// <see cref="MarkRunning"/> or <see cref="MarkFailed"/>.
/// </summary>
public sealed class Instance
{
    public const int NameMaxLength = 100;
    public const int VersionMaxLength = 32;

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
    public void MarkRunning(DateTime utcNow) => CompleteProvisioning(InstanceStatus.Running, utcNow);

    /// <summary><c>provisioning → failed</c>; provisioning failed for good.</summary>
    public void MarkFailed(DateTime utcNow) => CompleteProvisioning(InstanceStatus.Failed, utcNow);

    private void CompleteProvisioning(InstanceStatus outcome, DateTime utcNow)
    {
        if (Status != InstanceStatus.Provisioning)
        {
            throw new InvalidOperationException(
                $"Cannot move instance {Id} to {outcome}: status is {Status}, expected {InstanceStatus.Provisioning}.");
        }

        Status = outcome;
        UpdatedAt = utcNow;
    }
}
