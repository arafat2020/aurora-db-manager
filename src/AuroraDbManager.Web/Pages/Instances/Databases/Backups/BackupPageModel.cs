using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;

namespace AuroraDbManager.Web.Pages.Instances.Databases.Backups;

/// <summary>
/// A page about one backup of one database of one instance: all three are in its address, and
/// each has to be the other's.
/// </summary>
public abstract class BackupPageModel(InstanceService instances, DatabaseService databases, BackupService backups)
    : DatabasePageModel(instances, databases)
{
    public BackupResponse Backup { get; private set; } = null!;

    /// <returns>What was not found, "Instance", "Database" or "Backup"; null if all were.</returns>
    protected async Task<string?> LoadAsync(Guid id, Guid databaseId, Guid backupId, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, databaseId, cancellationToken) is { } missing)
        {
            return missing;
        }

        // A backup of another database is not this database's backup, whatever its id.
        if (await backups.GetAsync(backupId, cancellationToken) is not { } backup || backup.DatabaseId != databaseId)
        {
            return "Backup";
        }

        Backup = backup;
        return null;
    }
}
