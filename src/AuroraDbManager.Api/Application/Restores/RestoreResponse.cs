using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Jobs;

namespace AuroraDbManager.Api.Application.Restores;

/// <param name="Database">The database whose contents the restore replaces: the one the backup was made of.</param>
/// <param name="Job">
/// The job carrying out the restore, and the restore's status: <c>pending</c>, <c>running</c>,
/// <c>completed</c> or <c>failed</c>. Poll <c>GET /api/v1/jobs/{id}</c> to follow it.
/// </param>
public sealed record RestoreResponse(DatabaseResponse Database, JobResponse Job);
