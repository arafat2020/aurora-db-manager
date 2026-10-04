using System.ComponentModel.DataAnnotations;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Api.Application.Jobs;

public sealed class ListJobsQuery
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>Only jobs in this status: <c>pending</c>, <c>running</c>, <c>completed</c> or <c>failed</c>.</summary>
    // Bound as a string, like the type below, so an unknown value yields a normal validation
    // error. Keep in sync with JobStatus and JobType.
    [FromQuery(Name = "status")]
    [AllowedValues("pending", "running", "completed", "failed", null, ErrorMessage = "status must be one of: pending, running, completed, failed.")]
    public string? Status { get; init; }

    /// <summary>
    /// Only jobs of this type: <c>provision_instance</c>, <c>create_database</c>, <c>delete_database</c>,
    /// <c>backup_database</c> or <c>restore_database</c>.
    /// </summary>
    [FromQuery(Name = "type")]
    [AllowedValues(
        "provision_instance", "create_database", "delete_database", "backup_database", "restore_database", null,
        ErrorMessage = "type must be one of: provision_instance, create_database, delete_database, backup_database, restore_database.")]
    public string? Type { get; init; }

    /// <summary>Only jobs of this instance.</summary>
    [FromQuery(Name = "instanceId")]
    public Guid? InstanceId { get; init; }

    /// <summary>Only jobs that work on this database.</summary>
    [FromQuery(Name = "databaseId")]
    public Guid? DatabaseId { get; init; }

    /// <summary>1-based page number, at most 1000000. Defaults to 1.</summary>
    [FromQuery(Name = "page")]
    [Range(1, Paging.MaxPage, ErrorMessage = "page must be between {1} and {2}.")]
    public int Page { get; init; } = 1;

    /// <summary>Number of items per page, between 1 and 100. Defaults to 20.</summary>
    [FromQuery(Name = "pageSize")]
    [Range(1, MaxPageSize, ErrorMessage = "pageSize must be between {1} and {2}.")]
    public int PageSize { get; init; } = DefaultPageSize;

    internal JobStatus? StatusFilter => Status is null ? null : EnumStorage.FromDbValue<JobStatus>(Status);

    internal JobType? TypeFilter => Type is null ? null : EnumStorage.FromDbValue<JobType>(Type);
}
