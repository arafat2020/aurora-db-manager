using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Databases;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances.Databases;

/// <summary>One database: what is on record about it, and the last few things done to it.</summary>
public sealed class DetailsModel(InstanceService instances, DatabaseService databases, JobService jobs)
    : DatabasePageModel(instances, databases)
{
    public JobListResponse Jobs { get; private set; } = null!;

    public bool InProgress => Database.Status is DatabaseStatus.Creating or DatabaseStatus.Deleting;

    public async Task<IActionResult> OnGetAsync(Guid id, Guid databaseId, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, databaseId, cancellationToken) is { } missing)
        {
            return Missing(missing);
        }

        Jobs = await jobs.ListAsync(new ListJobsQuery { DatabaseId = databaseId, PageSize = Instances.DetailsModel.Shown }, cancellationToken);
        return Page();
    }
}
