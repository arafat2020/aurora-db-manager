using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.BackupSchedules;
using AuroraDbManager.Api.Application.Connectivity;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Databases;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances.Databases;

/// <summary>One database: what is on record about it, how it is reached, and the last few things done to it.</summary>
public sealed class DetailsModel(
    InstanceService instances,
    DatabaseService databases,
    InstanceConnectivityService connectivity,
    BackupService backups,
    BackupScheduleService schedules,
    InstanceHealthService health,
    JobService jobs)
    : DatabasePageModel(instances, databases)
{
    /// <summary>How the database is reached, with connection strings that have a placeholder where the password goes.</summary>
    public DatabaseConnectionResponse Connection { get; private set; } = null!;

    public JobListResponse Jobs { get; private set; } = null!;

    /// <summary>The database's backup schedule; null if it has none.</summary>
    public BackupScheduleResponse? Schedule { get; private set; }

    /// <summary>
    /// How the instance's database server is doing right now, which is as much as is known about
    /// the health of a database in it: the application has no check of a single database. Null
    /// while the instance is being provisioned and has no server to look at.
    /// </summary>
    public InstanceHealthResponse? InstanceHealth { get; private set; }

    /// <summary>The database's newest backups; the rest are a link away.</summary>
    public BackupListResponse Backups { get; private set; } = null!;

    public bool InProgress =>
        Database.Status is DatabaseStatus.Creating or DatabaseStatus.Deleting
        || Backups.Items.Any(backup => backup.Status is Api.Domain.Backups.BackupStatus.Pending or Api.Domain.Backups.BackupStatus.Running);

    public async Task<IActionResult> OnGetAsync(Guid id, Guid databaseId, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, databaseId, cancellationToken) is { } missing)
        {
            return Missing(missing);
        }

        if (await connectivity.GetForDatabaseAsync(databaseId, cancellationToken) is not { } connection)
        {
            return Missing("Database");
        }

        if (await backups.ListAsync(databaseId, new ListBackupsQuery { PageSize = Instances.DetailsModel.Shown }, cancellationToken) is not { } newest)
        {
            return Missing("Database");
        }

        Connection = connection;
        Backups = newest;
        Schedule = (await schedules.GetAsync(databaseId, cancellationToken)).Schedule;
        InstanceHealth = Instance.Status == Api.Domain.Instances.InstanceStatus.Provisioning ? null : await health.GetAsync(id, cancellationToken);
        Jobs = await jobs.ListAsync(new ListJobsQuery { DatabaseId = databaseId, PageSize = Instances.DetailsModel.Shown }, cancellationToken);
        return Page();
    }
}
