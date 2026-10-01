using AuroraDbManager.Api.Domain.Jobs;

namespace AuroraDbManager.Api.Application.Jobs;

/// <summary>
/// Performs the work of one <see cref="JobType"/>. Handlers share the <see cref="JobProcessor"/>'s
/// unit of work: they change entities but do not save, so each change is committed together with
/// the job's new status.
/// </summary>
public interface IJobHandler
{
    JobType Type { get; }

    /// <summary>
    /// Runs one attempt. Must be safe to run again after a failed or interrupted attempt, and
    /// should change entities only once the work has succeeded. Throw
    /// <see cref="JobExecutionException"/> to report a failure with a client-safe code and message.
    /// </summary>
    Task ExecuteAsync(Job job, CancellationToken cancellationToken);

    /// <summary>Called once, after the last attempt failed, to record the failure on the affected entities.</summary>
    Task OnFailedAsync(Job job, CancellationToken cancellationToken);
}
