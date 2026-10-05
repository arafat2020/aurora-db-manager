using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuroraDbManager.Web.Pages.Jobs;

/// <summary>
/// The job history, a page at a time, narrowed by the filters <see cref="JobService"/> has: status,
/// type, instance and database. The address is the query, as it is for the API; nothing is
/// filtered or counted here.
/// </summary>
public sealed class IndexModel(JobService jobs) : PageModel
{
    public JobListResponse Jobs { get; private set; } = null!;

    public ListJobsQuery Query { get; private set; } = new();

    public bool Filtered => Query.Status is not null || Query.Type is not null || Query.InstanceId is not null || Query.DatabaseId is not null;

    public bool InProgress => Jobs.Items.Any(job => job.Status is JobStatus.Pending or JobStatus.Running);

    /// <summary>This list's address with its filters, and without a page: what the pager adds a page to.</summary>
    public string FilteredPath
    {
        get
        {
            var filters = new List<string>();
            if (Query.Status is not null)
            {
                filters.Add($"status={Uri.EscapeDataString(Query.Status)}");
            }

            if (Query.Type is not null)
            {
                filters.Add($"type={Uri.EscapeDataString(Query.Type)}");
            }

            if (Query.InstanceId is { } instanceId)
            {
                filters.Add($"instanceId={instanceId:D}");
            }

            if (Query.DatabaseId is { } databaseId)
            {
                filters.Add($"databaseId={databaseId:D}");
            }

            return filters.Count == 0 ? Components.Routes.Jobs : $"{Components.Routes.Jobs}?{string.Join('&', filters)}";
        }
    }

    public async Task<IActionResult> OnGetAsync([FromQuery] ListJobsQuery query, CancellationToken cancellationToken)
    {
        // The query's own rules, the ones the API applies: an unknown status or type, an id that
        // is not one, a page that is not a page.
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        Query = query;
        Jobs = await jobs.ListAsync(query, cancellationToken);
        return Page();
    }
}
