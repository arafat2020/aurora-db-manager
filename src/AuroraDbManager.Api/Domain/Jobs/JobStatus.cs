namespace AuroraDbManager.Api.Domain.Jobs;

/// <summary>Lifecycle status of a background job.</summary>
public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed
}
