using AuroraDbManager.Api.Application.BackupSchedules;
using AuroraDbManager.Api.Errors;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Api.Controllers;

[ApiController]
[Route("api/v1/databases/{databaseId:guid}/backup-schedule")]
[Produces("application/json")]
public sealed class BackupSchedulesController(BackupScheduleService schedules) : ControllerBase
{
    /// <summary>Gives a database a backup schedule.</summary>
    /// <remarks>
    /// From then on the database is backed up by itself at every occurrence of the cron
    /// expression, read in the given time zone. Each of those backups is an ordinary backup with
    /// an ordinary <c>backup_database</c> job, stored where the server stores new backups; it
    /// appears under <c>GET /api/v1/databases/{id}/backups</c> like any other. Nothing is backed
    /// up by this request: the first run is the next occurrence after now. A database has one
    /// schedule at most (<c>409 BACKUP_SCHEDULE_ALREADY_EXISTS</c>), and gets one only while it
    /// could be backed up: <c>ready</c> (<c>409 DATABASE_NOT_READY</c>) on a <c>running</c>
    /// instance (<c>409 INSTANCE_NOT_READY</c>). An occurrence that passed while the server was
    /// down is made up for by one backup, however many were missed; one that finds the database
    /// busy or unavailable is skipped.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<BackupScheduleResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(Guid databaseId, BackupScheduleRequest request, CancellationToken cancellationToken)
    {
        var result = await schedules.CreateAsync(databaseId, request, cancellationToken);
        return result.Status == BackupScheduleStatus.Ok
            ? CreatedAtAction(nameof(Get), new { databaseId }, result.Schedule)
            : Failure(result.Status);
    }

    /// <summary>Returns the backup schedule of a database.</summary>
    [HttpGet]
    [ProducesResponseType<BackupScheduleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid databaseId, CancellationToken cancellationToken)
    {
        var result = await schedules.GetAsync(databaseId, cancellationToken);
        return result.Status == BackupScheduleStatus.Ok ? Ok(result.Schedule) : Failure(result.Status);
    }

    /// <summary>Changes the backup schedule of a database: its cron expression, its time zone, and whether it is enabled.</summary>
    /// <remarks>
    /// The request states the whole schedule. If anything changes, the next run is worked out
    /// anew from now. Disabling keeps the schedule and leaves it without a next run; enabling it
    /// again starts it from the next occurrence after that moment.
    /// </remarks>
    [HttpPut]
    [ProducesResponseType<BackupScheduleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid databaseId, BackupScheduleRequest request, CancellationToken cancellationToken)
    {
        var result = await schedules.UpdateAsync(databaseId, request, cancellationToken);
        return result.Status == BackupScheduleStatus.Ok ? Ok(result.Schedule) : Failure(result.Status);
    }

    /// <summary>Removes the backup schedule of a database.</summary>
    /// <remarks>
    /// Only the schedule is removed. Backups that exist are kept, a backup that is running is not
    /// stopped, and the database is not affected.
    /// </remarks>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid databaseId, CancellationToken cancellationToken)
    {
        var status = await schedules.DeleteAsync(databaseId, cancellationToken);
        return status == BackupScheduleStatus.Ok ? NoContent() : Failure(status);
    }

    private ObjectResult Failure(BackupScheduleStatus status) => status switch
    {
        BackupScheduleStatus.DatabaseNotFound => NotFound(ApiErrorResponse.Create(
            ErrorCodes.DatabaseNotFound, "Database was not found.")),
        BackupScheduleStatus.ScheduleNotFound => NotFound(ApiErrorResponse.Create(
            ErrorCodes.BackupScheduleNotFound, "The database has no backup schedule.")),
        BackupScheduleStatus.AlreadyExists => Conflict(ApiErrorResponse.Create(
            ErrorCodes.BackupScheduleAlreadyExists, "The database already has a backup schedule.")),
        BackupScheduleStatus.DatabaseNotReady => Conflict(ApiErrorResponse.Create(
            ErrorCodes.DatabaseNotReady, "Only a ready database can be given a backup schedule.")),
        BackupScheduleStatus.InstanceNotReady => Conflict(ApiErrorResponse.Create(
            ErrorCodes.InstanceNotReady, "Backup schedules need a running instance.")),
        BackupScheduleStatus.InvalidCronExpression => BadRequest(ApiErrorResponse.Create(
            ErrorCodes.InvalidCronExpression,
            "cronExpression must be a cron expression of five fields: minute, hour, day of month, month, day of week.")),
        _ => BadRequest(ApiErrorResponse.Create(
            ErrorCodes.InvalidTimeZone,
            "timeZoneId must be the IANA name of a time zone, such as 'Asia/Dhaka' or 'UTC'."))
    };
}
