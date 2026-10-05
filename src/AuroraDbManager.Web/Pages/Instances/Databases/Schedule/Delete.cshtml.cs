using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.BackupSchedules;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances.Databases.Schedule;

/// <summary>
/// Removing a database's backup schedule. Operators and administrators. Opening the page asks
/// the question and changes nothing; only its form, a POST with an antiforgery token, removes
/// the schedule, through <see cref="BackupScheduleService.DeleteAsync"/>.
/// </summary>
[Authorize(Policy = AuroraPolicies.Operator)]
public sealed class DeleteModel(InstanceService instances, DatabaseService databases, BackupScheduleService schedules)
    : DatabasePageModel(instances, databases)
{
    public BackupScheduleResponse Schedule { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, Guid databaseId, CancellationToken cancellationToken) =>
        await LoadWithScheduleAsync(id, databaseId, cancellationToken) is { } missing ? Missing(missing) : Page();

    public async Task<IActionResult> OnPostAsync(Guid id, Guid databaseId, CancellationToken cancellationToken)
    {
        if (await LoadWithScheduleAsync(id, databaseId, cancellationToken) is { } missing)
        {
            return Missing(missing);
        }

        return await schedules.DeleteAsync(databaseId, cancellationToken) switch
        {
            BackupScheduleStatus.Ok => Deleted(id, databaseId),
            BackupScheduleStatus.DatabaseNotFound => Missing("Database"),
            _ => Missing("Backup schedule")
        };
    }

    private IActionResult Deleted(Guid id, Guid databaseId)
    {
        Announce(StatusTone.Success, "Backup schedule deleted. The database is no longer backed up automatically.");
        return RedirectToPage("/Instances/Databases/Schedule/Index", new { id, databaseId });
    }

    private async Task<string?> LoadWithScheduleAsync(Guid id, Guid databaseId, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, databaseId, cancellationToken) is { } missing)
        {
            return missing;
        }

        var result = await schedules.GetAsync(databaseId, cancellationToken);
        if (result.Schedule is null)
        {
            return result.Status == BackupScheduleStatus.DatabaseNotFound ? "Database" : "Backup schedule";
        }

        Schedule = result.Schedule;
        return null;
    }
}
