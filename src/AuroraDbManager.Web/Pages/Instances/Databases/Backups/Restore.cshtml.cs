using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Restores;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances.Databases.Backups;

/// <summary>
/// Restoring a backup into the database it was made of, which replaces everything in that
/// database. Operators and administrators, by the policy the API's endpoint has. Opening the
/// page asks the question and changes nothing; only its form, a POST with an antiforgery token,
/// asks for the restore, and <see cref="RestoreService"/> says whether it is accepted: whether
/// the backup is complete and its storage usable, the database ready, the instance running, and
/// nothing else at work in the database. This page checks none of that itself. Accepted is not
/// restored: a job restores, and its status is the restore's.
/// </summary>
[Authorize(Policy = AuroraPolicies.Operator)]
public sealed class RestoreModel(InstanceService instances, DatabaseService databases, BackupService backups, RestoreService restores)
    : BackupPageModel(instances, databases, backups)
{
    public async Task<IActionResult> OnGetAsync(Guid id, Guid databaseId, Guid backupId, CancellationToken cancellationToken) =>
        await LoadAsync(id, databaseId, backupId, cancellationToken) is { } missing ? Missing(missing) : Page();

    public async Task<IActionResult> OnPostAsync(Guid id, Guid databaseId, Guid backupId, CancellationToken cancellationToken)
    {
        // The backup named in the address has to be this database's before anything is asked of the service.
        if (await LoadAsync(id, databaseId, backupId, cancellationToken) is { } missing)
        {
            return Missing(missing);
        }

        var result = await restores.CreateAsync(backupId, cancellationToken);
        if (result.Status == CreateRestoreStatus.BackupNotFound)
        {
            return Missing("Backup");
        }

        if (Rejections.For(result.Status) is { } rejection)
        {
            return Rejected(rejection);
        }

        Announce(StatusTone.Progress, "Restore started. The database may be temporarily unavailable until it has finished.");
        return RedirectToPage("/Instances/Databases/Backups/Details", new { id, databaseId, backupId });
    }
}
