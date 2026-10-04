using AuroraDbManager.Api.Application.Instances;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuroraDbManager.Web.Pages.Instances;

/// <summary>The instances, read through the same service the API lists them with. Read-only in this phase.</summary>
public sealed class IndexModel(InstanceService instances) : PageModel
{
    public InstanceListResponse Instances { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Instances = await instances.ListAsync(new ListInstancesQuery(), cancellationToken);
    }
}
