using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances.Databases;

/// <summary>The databases of one instance, a page at a time, read through the service the API lists them with.</summary>
public sealed class IndexModel(InstanceService instances, DatabaseService databases) : ResourcePageModel
{
    public InstanceResponse Instance { get; private set; } = null!;

    public DatabaseListResponse Databases { get; private set; } = null!;

    public bool InProgress =>
        Databases.Items.Any(database => database.Status is DatabaseStatus.Creating or DatabaseStatus.Deleting);

    public async Task<IActionResult> OnGetAsync(Guid id, [FromQuery] ListDatabasesQuery query, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var instance = await instances.GetAsync(id, cancellationToken);
        var list = instance is null ? null : await databases.ListAsync(id, query, cancellationToken);
        if (instance is null || list is null)
        {
            return Missing("Instance");
        }

        Instance = instance;
        Databases = list;
        return Page();
    }
}
