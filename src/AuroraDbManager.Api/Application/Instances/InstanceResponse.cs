using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Application.Instances;

/// <param name="Id">Unique identifier of the instance.</param>
/// <param name="Name">Display name of the instance.</param>
/// <param name="Engine">Database engine: <c>postgres</c> or <c>mysql</c>.</param>
/// <param name="Version">Engine version.</param>
/// <param name="Status">Lifecycle status: <c>provisioning</c>, <c>running</c>, <c>stopped</c> or <c>failed</c>.</param>
/// <param name="Cpu">Number of CPU cores.</param>
/// <param name="MemoryMb">Memory in megabytes.</param>
/// <param name="StorageGb">Storage in gigabytes.</param>
/// <param name="CreatedAt">UTC time the instance was created.</param>
/// <param name="UpdatedAt">UTC time the instance was last changed.</param>
public sealed record InstanceResponse(
    Guid Id,
    string Name,
    InstanceEngine Engine,
    string Version,
    InstanceStatus Status,
    int Cpu,
    int MemoryMb,
    int StorageGb,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static InstanceResponse From(Instance instance) => new(
        instance.Id,
        instance.Name,
        instance.Engine,
        instance.Version,
        instance.Status,
        instance.Cpu,
        instance.MemoryMb,
        instance.StorageGb,
        instance.CreatedAt,
        instance.UpdatedAt);
}

/// <param name="Items">Instances on the requested page, newest first.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Requested number of items per page.</param>
/// <param name="TotalCount">Total number of instances across all pages.</param>
public sealed record InstanceListResponse(
    IReadOnlyList<InstanceResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <param name="Instance">The created instance, in status <c>provisioning</c>.</param>
/// <param name="Job">The job provisioning the instance. Poll <c>GET /api/v1/jobs/{id}</c> to follow it.</param>
public sealed record CreateInstanceResponse(InstanceResponse Instance, JobResponse Job);
