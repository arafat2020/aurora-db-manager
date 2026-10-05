using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances.Databases.Backups;

/// <summary>The backups of one database, a page at a time, read through the service the API lists them with.</summary>
public sealed class IndexModel(InstanceService instances, DatabaseService databases, BackupService backups, BackupActivity activity)
    : DatabasePageModel(instances, databases)
{
    public BackupListResponse Backups { get; private set; } = null!;

    public DatabaseActivity Activity { get; private set; } = null!;

    public bool InProgress =>
        Activity.InProgress || Backups.Items.Any(backup => backup.Status is BackupStatus.Pending or BackupStatus.Running);

    public async Task<IActionResult> OnGetAsync(Guid id, Guid databaseId, [FromQuery] ListBackupsQuery query, CancellationToken cancellationToken)
    {
        // The query's own limits, the ones the API has: a page number that is not one is not a page.
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        if (await LoadAsync(id, databaseId, cancellationToken) is { } missing)
        {
            return Missing(missing);
        }

        if (await backups.ListAsync(databaseId, query, cancellationToken) is not { } list)
        {
            return Missing("Database");
        }

        Backups = list;
        Activity = await activity.ReadAsync(databaseId, cancellationToken);
        return Page();
    }
}
