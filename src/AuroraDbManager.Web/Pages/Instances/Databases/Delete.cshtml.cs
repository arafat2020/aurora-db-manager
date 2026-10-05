using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances.Databases;

/// <summary>
/// Deleting a database. Operators and administrators. Opening the page asks the question and
/// changes nothing; only its form, a POST with an antiforgery token, asks for the deletion, and
/// <see cref="DatabaseService.DeleteAsync"/> says whether it is accepted. Accepted is not
/// deleted: a job drops the database, which says "deleting" until it has.
/// </summary>
[Authorize(Policy = AuroraPolicies.Operator)]
public sealed class DeleteModel(InstanceService instances, DatabaseService databases)
    : DatabasePageModel(instances, databases)
{
    private readonly DatabaseService _databases = databases;

    public async Task<IActionResult> OnGetAsync(Guid id, Guid databaseId, CancellationToken cancellationToken) =>
        await LoadAsync(id, databaseId, cancellationToken) is { } missing ? Missing(missing) : Page();

    public async Task<IActionResult> OnPostAsync(Guid id, Guid databaseId, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, databaseId, cancellationToken) is { } missing)
        {
            return Missing(missing);
        }

        var result = await _databases.DeleteAsync(databaseId, cancellationToken);
        if (result.Status == DeleteDatabaseStatus.NotFound)
        {
            return Missing("Database");
        }

        if (Rejections.For(result.Status) is { } rejection)
        {
            return Rejected(rejection);
        }

        Announce(StatusTone.Progress, $"Deletion of database “{Database.Name}” started.");
        return RedirectToPage("/Instances/Databases/Index", new { id });
    }
}
