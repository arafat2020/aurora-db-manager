using System.ComponentModel.DataAnnotations;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Api.Application.Backups;

public sealed class ListBackupsQuery
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>Only backups in this status: <c>pending</c>, <c>running</c>, <c>completed</c> or <c>failed</c>.</summary>
    // Bound as a string so an unknown value yields a normal validation error. Keep in sync with BackupStatus.
    [FromQuery(Name = "status")]
    [AllowedValues("pending", "running", "completed", "failed", null, ErrorMessage = "status must be one of: pending, running, completed, failed.")]
    public string? Status { get; init; }

    internal BackupStatus? StatusFilter => Status is null ? null : EnumStorage.FromDbValue<BackupStatus>(Status);

    /// <summary>1-based page number. Defaults to 1.</summary>
    [FromQuery(Name = "page")]
    [Range(1, int.MaxValue, ErrorMessage = "page must be greater than zero.")]
    public int Page { get; init; } = 1;

    /// <summary>Number of items per page, between 1 and 100. Defaults to 20.</summary>
    [FromQuery(Name = "pageSize")]
    [Range(1, MaxPageSize, ErrorMessage = "pageSize must be between {1} and {2}.")]
    public int PageSize { get; init; } = DefaultPageSize;
}
