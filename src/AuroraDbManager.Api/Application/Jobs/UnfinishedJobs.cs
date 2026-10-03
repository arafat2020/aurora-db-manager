using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Application.Jobs;

/// <summary>
/// What a database is busy with. A database has one unfinished job at most, which the jobs
/// table's unique index guarantees; the services ask here first so they can say which operation
/// is in the way, and ask again when the index has refused a job of theirs.
/// </summary>
public static class UnfinishedJobs
{
    /// <summary>The type of the database's pending or running job, or null if it has none.</summary>
    public static async Task<JobType?> UnfinishedJobTypeAsync(
        this AppDbContext db, Guid databaseId, CancellationToken cancellationToken)
    {
        var types = await db.Jobs
            .Where(j => j.DatabaseId == databaseId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .Select(j => j.Type)
            .ToListAsync(cancellationToken);

        return types.Count == 0 ? null : types[0];
    }
}
