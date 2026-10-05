using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Jobs;

/// <summary>
/// One job, as <see cref="JobService"/> has it: what it was, how far it got, and, if it failed,
/// the stable code and the message the application recorded for clients. What it worked on is
/// named by asking the services that have it; a database that has since been deleted is simply
/// not named.
/// </summary>
public sealed class DetailsModel(JobService jobs, InstanceService instances, DatabaseService databases) : ResourcePageModel
{
    public JobResponse Job { get; private set; } = null!;

    public InstanceResponse? Instance { get; private set; }

    public DatabaseResponse? Database { get; private set; }

    public bool InProgress => Job.Status is JobStatus.Pending or JobStatus.Running;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await jobs.GetAsync(id, cancellationToken) is not { } job)
        {
            return Missing("Job");
        }

        Job = job;
        Instance = await instances.GetAsync(job.InstanceId, cancellationToken);
        Database = job.DatabaseId is { } databaseId ? await databases.GetAsync(databaseId, cancellationToken) : null;
        return Page();
    }
}
