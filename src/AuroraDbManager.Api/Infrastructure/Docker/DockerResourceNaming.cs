namespace AuroraDbManager.Api.Infrastructure.Docker;

/// <summary>
/// The single place that decides what an instance's Docker resources are called and how they are
/// labelled. Names derive from the instance id only, so the same instance always maps to the same
/// resources; the user-chosen instance name is never used.
/// </summary>
public static class DockerResourceNaming
{
    public const string ManagedLabel = "aurora.managed";
    public const string InstanceIdLabel = "aurora.instance-id";

    public static string ContainerName(Guid instanceId) => $"aurora-instance-{instanceId:D}";

    public static string VolumeName(Guid instanceId) => $"{ContainerName(instanceId)}-data";

    /// <summary>
    /// The name an instance's container is set aside under while a container with another port
    /// configuration takes its place. It exists only for the duration of that replacement.
    /// </summary>
    public static string ReplacedContainerName(Guid instanceId) => $"{ContainerName(instanceId)}-replaced";

    /// <summary>Labels put on an instance's container and volume to mark them as its own.</summary>
    public static IReadOnlyDictionary<string, string> InstanceLabels(Guid instanceId) => new Dictionary<string, string>
    {
        [ManagedLabel] = "true",
        [InstanceIdLabel] = instanceId.ToString("D")
    };

    public static IReadOnlyDictionary<string, string> NetworkLabels() => new Dictionary<string, string>
    {
        [ManagedLabel] = "true"
    };

    /// <summary>The instance a resource carrying <paramref name="labels"/> was created for, if its label says so.</summary>
    public static Guid? OwnerOf(IReadOnlyDictionary<string, string> labels) =>
        labels.TryGetValue(InstanceIdLabel, out var owner) && Guid.TryParse(owner, out var instanceId) ? instanceId : null;

    /// <summary>Whether a resource carrying <paramref name="labels"/> was created for this instance.</summary>
    public static bool IsOwnedBy(IReadOnlyDictionary<string, string> labels, Guid instanceId) =>
        labels.TryGetValue(InstanceIdLabel, out var owner)
        && string.Equals(owner, instanceId.ToString("D"), StringComparison.OrdinalIgnoreCase);
}
