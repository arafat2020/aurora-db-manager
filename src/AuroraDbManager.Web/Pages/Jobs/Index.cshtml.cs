using AuroraDbManager.Api.Application.Jobs;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuroraDbManager.Web.Pages.Jobs;

/// <summary>The most recent jobs, read through the same service the API lists them with.</summary>
public sealed class IndexModel(JobService jobs) : PageModel
{
    public JobListResponse Jobs { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Jobs = await jobs.ListAsync(new ListJobsQuery(), cancellationToken);
    }
}
