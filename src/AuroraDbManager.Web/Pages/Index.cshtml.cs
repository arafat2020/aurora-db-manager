using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuroraDbManager.Web.Pages;

/// <summary>The overview: what monitoring already knows, on one page. Any signed-in user may see it.</summary>
public sealed class IndexModel(DashboardReader reader) : PageModel
{
    public Dashboard Dashboard { get; private set; } = null!;

    /// <summary>Why operations are not simply healthy, in the summary's own terms.</summary>
    public string OperationsNote
    {
        get
        {
            var summary = Dashboard.Summary;
            var reasons = new List<string>();
            if (summary.Instances.Failed > 0)
            {
                reasons.Add($"{summary.Instances.Failed} failed instance{(summary.Instances.Failed == 1 ? "" : "s")}");
            }

            if (summary.Jobs.FailedRecently > 0)
            {
                reasons.Add($"{summary.Jobs.FailedRecently} recent failure{(summary.Jobs.FailedRecently == 1 ? "" : "s")}");
            }

            if (summary.Scheduler.OverdueSchedules > 0)
            {
                reasons.Add($"{summary.Scheduler.OverdueSchedules} overdue schedule{(summary.Scheduler.OverdueSchedules == 1 ? "" : "s")}");
            }

            return string.Join(", ", reasons);
        }
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Dashboard = await reader.ReadAsync(cancellationToken);
    }
}
