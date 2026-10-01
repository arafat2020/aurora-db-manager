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
}
