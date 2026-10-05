using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances.Databases.Backups;

/// <summary>
/// Asking for a backup of a database. Operators and administrators, by the policy the API's
/// endpoint has. The request is the database and nothing else, as it is for the API: where a
/// backup goes is the server's setting, which this page shows and cannot change, and whether the
/// database can be backed up now is <see cref="BackupService"/>'s to say. Accepted is not made:
/// a job makes the backup, which says "pending" and then "running" until it has.
/// </summary>
[Authorize(Policy = AuroraPolicies.Operator)]
public sealed class CreateModel(InstanceService instances, DatabaseService databases, BackupService backups, IBackupStorage defaultStorage)
    : DatabasePageModel(instances, databases)
{
    /// <summary>
    /// Where the backup will be kept: the server's default storage, the very thing
    /// <see cref="BackupService"/> creates a backup for. Its type, and nothing of its settings.
    /// </summary>
    public BackupStorageType Storage => defaultStorage.Type;

    public async Task<IActionResult> OnGetAsync(Guid id, Guid databaseId, CancellationToken cancellationToken) =>
        await LoadAsync(id, databaseId, cancellationToken) is { } missing ? Missing(missing) : Page();

    public async Task<IActionResult> OnPostAsync(Guid id, Guid databaseId, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, databaseId, cancellationToken) is { } missing)
        {
            return Missing(missing);
        }

        var result = await backups.CreateAsync(databaseId, cancellationToken);
        if (result.Status == CreateBackupStatus.DatabaseNotFound)
        {
            return Missing("Database");
        }

        if (Rejections.For(result.Status) is { } rejection)
        {
            return Rejected(rejection);
        }

        Announce(StatusTone.Progress, "Backup creation started.");
        return RedirectToPage("/Instances/Databases/Backups/Details", new { id, databaseId, backupId = result.Operation!.Backup.Id });
    }
}
