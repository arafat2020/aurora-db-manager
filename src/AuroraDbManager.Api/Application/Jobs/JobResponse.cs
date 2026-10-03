using System.Text.Json.Serialization;
using AuroraDbManager.Api.Domain.Jobs;

namespace AuroraDbManager.Api.Application.Jobs;

/// <param name="Id">Unique identifier of the job.</param>
/// <param name="Type">Kind of work: <c>provision_instance</c>, <c>create_database</c>, <c>delete_database</c>, <c>backup_database</c> or <c>restore_database</c>.</param>
/// <param name="Status">
/// Lifecycle status: <c>pending</c>, <c>running</c>, <c>completed</c> or <c>failed</c>. A job stays
/// <c>running</c> while it retries; <c>failed</c> means all attempts were used.
/// </param>
/// <param name="InstanceId">Instance the job works on.</param>
/// <param name="DatabaseId">Database the job works on. Present on every job except <c>provision_instance</c>.</param>
/// <param name="BackupId">Backup a <c>backup_database</c> job produces or a <c>restore_database</c> job restores from. Only present on those jobs.</param>
/// <param name="Attempt">Number of the current attempt; 0 until first picked up. An attempt interrupted by a restart is not counted.</param>
/// <param name="MaxAttempts">Attempts allowed before the job is marked failed.</param>
/// <param name="CreatedAt">UTC time the job was created.</param>
/// <param name="StartedAt">UTC time the first attempt started; null until then.</param>
/// <param name="CompletedAt">UTC time the job reached <c>completed</c> or <c>failed</c>; null until then.</param>
/// <param name="UpdatedAt">UTC time the job last changed.</param>
/// <param name="Error">Error of the most recent failed attempt; null if none failed or the job completed.</param>
public sealed record JobResponse(
    Guid Id,
    JobType Type,
    JobStatus Status,
    Guid InstanceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? DatabaseId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? BackupId,
    int Attempt,
    int MaxAttempts,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime UpdatedAt,
    JobErrorResponse? Error)
{
    public static JobResponse From(Job job) => new(
        job.Id,
        job.Type,
        job.Status,
        job.InstanceId,
        job.DatabaseId,
        job.BackupId,
        job.Attempt,
        job.MaxAttempts,
        job.CreatedAt,
        job.StartedAt,
        job.CompletedAt,
        job.UpdatedAt,
        job.ErrorCode is null ? null : new JobErrorResponse(job.ErrorCode, job.ErrorMessage ?? string.Empty));
}

/// <param name="Code">Stable, machine-readable error code, e.g. <c>PROVISIONING_FAILED</c>.</param>
/// <param name="Message">Human-readable description of the failure.</param>
public sealed record JobErrorResponse(string Code, string Message);
