using AuroraDbManager.Api.Application.Connectivity;
using AuroraDbManager.Api.Application.Credentials;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances;

/// <summary>
/// One instance: what is on record about it, how its database server is doing right now, its
/// databases, how it is reached, the credential Aurora manages for it, and the last few things done to it. Everything is read through the services the
/// API reads it through; the health is <see cref="InstanceHealthService"/>'s and nobody else's.
/// </summary>
public sealed class DetailsModel(
    InstanceService instances,
    InstanceHealthService health,
    DatabaseService databases,
    InstanceConnectivityService connectivity,
    CredentialRotationService credentials,
    JobService jobs) : ResourcePageModel
{
    /// <summary>How many of the instance's databases and operations this page shows; the rest are a link away.</summary>
    public const int Shown = 5;

    public InstanceResponse Instance { get; private set; } = null!;

    /// <summary>
    /// What was found of the database server just now. Null while the instance is being
    /// provisioned: there is no server to look at yet, and its absence is not ill health.
    /// </summary>
    public InstanceHealthResponse? Health { get; private set; }

    /// <summary>How the instance is reached, as <see cref="InstanceConnectivityService"/> has it.</summary>
    public InstanceConnectionResponse Connection { get; private set; } = null!;

    /// <summary>The credential Aurora manages for the instance, as <see cref="CredentialRotationService"/> has it. Never a password.</summary>
    public InstanceCredentialResponse Credential { get; private set; } = null!;

    public DatabaseListResponse Databases { get; private set; } = null!;

    public JobListResponse Jobs { get; private set; } = null!;

    /// <summary>Something here is on its way to another status.</summary>
    public bool InProgress =>
        Instance.Status == InstanceStatus.Provisioning
        || Credential.Rotation == CredentialRotationState.InProgress
        || Databases.Items.Any(database => database.Status is DatabaseStatus.Creating or DatabaseStatus.Deleting);

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var instance = await instances.GetAsync(id, cancellationToken);
        var list = instance is null ? null : await databases.ListAsync(id, new ListDatabasesQuery { PageSize = Shown }, cancellationToken);
        var connection = list is null ? null : await connectivity.GetAsync(id, cancellationToken);
        var credential = connection is null ? null : await credentials.GetAsync(id, cancellationToken);
        if (instance is null || list is null || connection is null || credential is null)
        {
            return Missing("Instance");
        }

        Instance = instance;
        Connection = connection;
        Credential = credential;
        Databases = list;
        Health = instance.Status == InstanceStatus.Provisioning ? null : await health.GetAsync(id, cancellationToken);
        Jobs = await jobs.ListAsync(new ListJobsQuery { InstanceId = id, PageSize = Shown }, cancellationToken);
        return Page();
    }
}
