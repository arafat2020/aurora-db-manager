using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Api.Application.Databases;

public sealed class ListDatabasesQuery
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>1-based page number, at most 1000000. Defaults to 1.</summary>
    [FromQuery(Name = "page")]
    [Range(1, Paging.MaxPage, ErrorMessage = "page must be between {1} and {2}.")]
    public int Page { get; init; } = 1;

    /// <summary>Number of items per page, between 1 and 100. Defaults to 20.</summary>
    [FromQuery(Name = "pageSize")]
    [Range(1, MaxPageSize, ErrorMessage = "pageSize must be between {1} and {2}.")]
    public int PageSize { get; init; } = DefaultPageSize;
}
