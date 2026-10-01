namespace AuroraDbManager.Api.Domain.Instances;

/// <summary>Lifecycle status of a managed instance.</summary>
public enum InstanceStatus
{
    Provisioning,
    Running,
    Stopped,
    Failed
}
