using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Infrastructure.Docker;

/// <summary>
/// Everything needed to create an instance's container. <see cref="Environment"/> holds the
/// administrator password, so a spec must never be logged.
/// </summary>
public sealed record DockerContainerSpec(
    string Name,
    string Image,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> Labels,
    string VolumeName,
    string VolumeTarget,
    string NetworkName,
    long NanoCpus,
    long MemoryBytes,
    DockerPortBinding? PortBinding = null)
{
    private const long NanoCpusPerCpu = 1_000_000_000;
    private const long BytesPerMegabyte = 1024 * 1024;

    /// <summary>
    /// Builds the container configuration for an instance. Resource mapping:
    /// <list type="bullet">
    /// <item><c>Cpu</c> is a number of whole CPUs. Docker takes CPU limits in billionths of a CPU,
    /// so the limit is <c>Cpu × 1,000,000,000</c> NanoCPUs.</item>
    /// <item><c>MemoryMb</c> is a hard memory limit of <c>MemoryMb × 1024 × 1024</c> bytes.</item>
    /// <item><c>StorageGb</c> is not enforced: Docker's local volume driver cannot limit a volume's size.</item>
    /// </list>
    /// The container publishes what <see cref="PortBindingFor"/> says, which for an instance
    /// without external access is nothing.
    /// </summary>
    public static DockerContainerSpec For(
        Instance instance, DatabaseImage image, string adminPassword, string networkName, string bindAddress) => new(
        Name: DockerResourceNaming.ContainerName(instance.Id),
        Image: image.Image,
        Environment: new Dictionary<string, string> { [image.AdminPasswordVariable] = adminPassword },
        Labels: DockerResourceNaming.InstanceLabels(instance.Id),
        VolumeName: DockerResourceNaming.VolumeName(instance.Id),
        VolumeTarget: image.DataPath,
        NetworkName: networkName,
        NanoCpus: instance.Cpu * NanoCpusPerCpu,
        MemoryBytes: instance.MemoryMb * BytesPerMegabyte,
        PortBinding: PortBindingFor(instance, image.Port, bindAddress));

    /// <summary>
    /// The one port an instance's container publishes, or null if its record says none: the
    /// engine's own port, as the image catalog and <see cref="EngineDefaults"/> have it, on the host port on the instance's record, bound
    /// to the server's configured address. No part of it is anything a request said.
    /// </summary>
    public static DockerPortBinding? PortBindingFor(Instance instance, int enginePort, string bindAddress) =>
        instance is { ExternalAccessEnabled: true, ExternalPort: { } hostPort }
            ? new DockerPortBinding(enginePort, bindAddress, hostPort)
            : null;

    // Keeps the environment, and with it the password, out of anything that prints a spec.
    public override string ToString() => $"{nameof(DockerContainerSpec)} {{ Name = {Name}, Image = {Image} }}";
}
