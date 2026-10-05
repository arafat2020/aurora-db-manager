using AuroraDbManager.Api.Application.BackupSchedules;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances.Databases.Schedule;

/// <summary>
/// The backup schedule of one database, of which a database has one at most, as
/// <see cref="BackupScheduleService"/> has it. When it next runs is the service's to say: the
/// page shows the next run on the schedule's record, and works nothing out.
/// </summary>
public sealed class IndexModel(InstanceService instances, DatabaseService databases, BackupScheduleService schedules)
    : DatabasePageModel(instances, databases)
{
    /// <summary>The database's schedule; null if it has none.</summary>
    public BackupScheduleResponse? Schedule { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, Guid databaseId, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, databaseId, cancellationToken) is { } missing)
        {
            return Missing(missing);
        }

        var result = await schedules.GetAsync(databaseId, cancellationToken);
        if (result.Status == BackupScheduleStatus.DatabaseNotFound)
        {
            return Missing("Database");
        }

        Schedule = result.Schedule;
        return Page();
    }
}
