using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Jobs;

namespace AuroraDbManager.Web.Components;

/// <summary>
/// What is being done to a database right now, read from its jobs: a restore has no record but
/// its job, and a job that is pending or running is work in progress. Nothing is tracked here;
/// this only looks at what <see cref="JobService"/> lists.
/// </summary>
public sealed class BackupActivity(JobService jobs)
{
    /// <summary>How many of a database's most recent jobs are looked at. More than a database has unfinished: it has one at most.</summary>
    public const int Considered = 50;

    public async Task<DatabaseActivity> ReadAsync(Guid databaseId, CancellationToken cancellationToken)
    {
        var recent = await jobs.ListAsync(new ListJobsQuery { DatabaseId = databaseId, PageSize = Considered }, cancellationToken);
        return new DatabaseActivity(recent.Items);
    }
}

/// <param name="RecentJobs">The database's most recent jobs, newest first.</param>
public sealed record DatabaseActivity(IReadOnlyList<JobResponse> RecentJobs)
{
    private JobResponse? Unfinished(JobType type) =>
        RecentJobs.FirstOrDefault(job => job.Type == type && job.Status is JobStatus.Pending or JobStatus.Running);

    /// <summary>The restore that is under way, if one is.</summary>
    public JobResponse? RestoreInProgress => Unfinished(JobType.RestoreDatabase);

    /// <summary>The backup that is under way, if one is.</summary>
    public JobResponse? BackupInProgress => Unfinished(JobType.BackupDatabase);

    public bool InProgress => RestoreInProgress is not null || BackupInProgress is not null;

    /// <summary>What was done with one backup: the job that made it, and every restore of it, newest first.</summary>
    public IReadOnlyList<JobResponse> Of(Guid backupId) => RecentJobs.Where(job => job.BackupId == backupId).ToList();

    /// <summary>The most recent restore of the backup, whatever became of it.</summary>
    public JobResponse? LatestRestoreOf(Guid backupId) =>
        RecentJobs.FirstOrDefault(job => job.Type == JobType.RestoreDatabase && job.BackupId == backupId);
}
