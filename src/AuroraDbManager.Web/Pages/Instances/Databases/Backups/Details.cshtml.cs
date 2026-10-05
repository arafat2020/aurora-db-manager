using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances.Databases.Backups;

/// <summary>
/// One backup: what its record says, whether the server can still use the storage it is in, and
/// what was done with it. The storage is the backup's own, the one its record names, which need
/// not be where new backups go now; whether it is usable is asked of
/// <see cref="IBackupStorageResolver"/>, the resolver every restore goes through.
/// </summary>
public sealed class DetailsModel(
    InstanceService instances,
    DatabaseService databases,
    BackupService backups,
    IBackupStorageResolver storages,
    BackupActivity activity)
    : BackupPageModel(instances, databases, backups)
{
    public DatabaseActivity Activity { get; private set; } = null!;

    /// <summary>The job that made the backup and the restores of it, newest first.</summary>
    public IReadOnlyList<JobResponse> Operations { get; private set; } = [];

    /// <summary>Whether this server has settings for the storage the backup is in. If not, the backup cannot be read, and so not restored, here.</summary>
    public bool StorageConfigured { get; private set; }

    /// <summary>The most recent restore of this backup, if it is still under way.</summary>
    public JobResponse? RestoreInProgress =>
        Activity.LatestRestoreOf(Backup.Id) is { Status: JobStatus.Pending or JobStatus.Running } restore ? restore : null;

    /// <summary>The most recent restore of this backup, if it failed for good.</summary>
    public JobResponse? RestoreFailed =>
        Activity.LatestRestoreOf(Backup.Id) is { Status: JobStatus.Failed } restore ? restore : null;

    /// <summary>The most recent restore of this backup, if it went through.</summary>
    public JobResponse? RestoreCompleted =>
        Activity.LatestRestoreOf(Backup.Id) is { Status: JobStatus.Completed } restore ? restore : null;

    public bool InProgress => Backup.Status is BackupStatus.Pending or BackupStatus.Running || RestoreInProgress is not null;

    public async Task<IActionResult> OnGetAsync(Guid id, Guid databaseId, Guid backupId, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, databaseId, backupId, cancellationToken) is { } missing)
        {
            return Missing(missing);
        }

        Activity = await activity.ReadAsync(databaseId, cancellationToken);
        Operations = Activity.Of(backupId);

        try
        {
            storages.Resolve(Backup.StorageType);
            StorageConfigured = true;
        }
        catch (BackupOperationException exception) when (exception.Code == BackupErrorCodes.BackupStorageNotConfigured)
        {
            StorageConfigured = false;
        }

        return Page();
    }
}
