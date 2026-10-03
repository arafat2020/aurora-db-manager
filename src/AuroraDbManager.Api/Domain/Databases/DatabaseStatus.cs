namespace AuroraDbManager.Api.Domain.Databases;

/// <summary>Lifecycle status of a database inside a managed instance.</summary>
public enum DatabaseStatus
{
    Creating,
    Ready,
    Deleting,
    Failed
}
