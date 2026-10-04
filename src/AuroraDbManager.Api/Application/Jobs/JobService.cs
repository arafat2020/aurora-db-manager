using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Application.Jobs;

public sealed class JobService(AppDbContext db)
{
    public async Task<JobResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        return job is null ? null : JobResponse.From(job);
    }

    /// <summary>One page of the job history, newest first, narrowed by whichever filters are given.</summary>
    public async Task<JobListResponse> ListAsync(ListJobsQuery query, CancellationToken cancellationToken)
    {
        var jobs = db.Jobs.AsNoTracking();

        if (query.StatusFilter is { } status)
        {
            jobs = jobs.Where(j => j.Status == status);
        }

        if (query.TypeFilter is { } type)
        {
            jobs = jobs.Where(j => j.Type == type);
        }

        if (query.InstanceId is { } instanceId)
        {
            jobs = jobs.Where(j => j.InstanceId == instanceId);
        }

        if (query.DatabaseId is { } databaseId)
        {
            jobs = jobs.Where(j => j.DatabaseId == databaseId);
        }

        var totalCount = await jobs.CountAsync(cancellationToken);

        // Never the whole history: one page of it.
        var page = await jobs
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new JobListResponse(page.Select(JobResponse.From).ToList(), query.Page, query.PageSize, totalCount);
    }
}
