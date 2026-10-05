using AuroraDbManager.Api.Application.Instances;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuroraDbManager.Web.Pages.Instances;

/// <summary>The instances, a page at a time, read through the same service the API lists them with.</summary>
public sealed class IndexModel(InstanceService instances) : PageModel
{
    public InstanceListResponse Instances { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync([FromQuery] ListInstancesQuery query, CancellationToken cancellationToken)
    {
        // The query's own limits: a page number that is not one is not a page.
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        Instances = await instances.ListAsync(query, cancellationToken);
        return Page();
    }
}
