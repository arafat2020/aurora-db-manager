using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Databases;

namespace AuroraDbManager.Api.Application.Databases;

/// <param name="Id">Unique identifier of the database.</param>
/// <param name="InstanceId">The instance the database belongs to.</param>
/// <param name="Name">Name of the database, unique within its instance.</param>
/// <param name="Status">Lifecycle status: <c>creating</c>, <c>ready</c>, <c>deleting</c> or <c>failed</c>.</param>
/// <param name="CreatedAt">UTC time the database was created.</param>
/// <param name="UpdatedAt">UTC time the database was last changed.</param>
/// <param name="Error">Why the database is <c>failed</c>; null in every other status.</param>
public sealed record DatabaseResponse(
    Guid Id,
    Guid InstanceId,
    string Name,
    DatabaseStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DatabaseErrorResponse? Error)
{
    public static DatabaseResponse From(Database database) => new(
        database.Id,
        database.InstanceId,
        database.Name,
        database.Status,
        database.CreatedAt,
        database.UpdatedAt,
        database.ErrorCode is null ? null : new DatabaseErrorResponse(database.ErrorCode, database.ErrorMessage ?? string.Empty));
}

/// <param name="Code">Stable, machine-readable error code, e.g. <c>DATABASE_CONNECTION_FAILED</c>.</param>
/// <param name="Message">Human-readable description of the failure.</param>
public sealed record DatabaseErrorResponse(string Code, string Message);

/// <param name="Items">Databases on the requested page, newest first.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Requested number of items per page.</param>
/// <param name="TotalCount">Total number of databases of the instance across all pages.</param>
public sealed record DatabaseListResponse(
    IReadOnlyList<DatabaseResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <param name="Database">The database, in status <c>creating</c> or <c>deleting</c>.</param>
/// <param name="Job">The job carrying out the operation. Poll <c>GET /api/v1/jobs/{id}</c> to follow it.</param>
public sealed record DatabaseOperationResponse(DatabaseResponse Database, JobResponse Job);
