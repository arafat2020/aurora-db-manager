using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuroraDbManager.Web.Pages.Monitoring;

/// <summary>
/// The state of the installation in more detail than the overview: every health check, the
/// scheduler, and the counts and failures of the monitoring summary. Any signed-in user may see
/// it, as they may the monitoring API. It is what <see cref="MonitoringReader"/> reads, as of the
/// moment the page was asked for.
/// </summary>
public sealed class IndexModel(MonitoringReader reader) : PageModel
{
    public MonitoringView Monitoring { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Monitoring = await reader.ReadAsync(cancellationToken);
    }
}
