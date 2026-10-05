using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.BackupSchedules;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances.Databases.Schedule;

/// <summary>
/// Giving a database a backup schedule, changing the one it has, and turning it on and off.
/// Operators and administrators, by the policy the API's endpoints have. The form's fields are
/// those of <see cref="BackupScheduleRequest"/>, and <see cref="BackupScheduleService"/> does
/// everything else: it is its calculator that reads the cron expression and the time zone, and
/// that says when the schedule next runs. This page parses neither.
/// </summary>
/// <remarks>
/// The service has no operation that only enables or only disables: a request states the whole
/// schedule. Enabling and disabling therefore send the schedule back as it is, with that one
/// value changed.
/// </remarks>
[Authorize(Policy = AuroraPolicies.Operator)]
public sealed class EditModel(
    InstanceService instances,
    DatabaseService databases,
    BackupScheduleService schedules,
    IScheduleCalculator calculator)
    : DatabasePageModel(instances, databases)
{
    [BindProperty]
    public ScheduleInput Input { get; set; } = new();

    /// <summary>The schedule being changed; null when the database has none and the form creates one.</summary>
    public BackupScheduleResponse? Existing { get; private set; }

    /// <summary>
    /// Time zone names to suggest: the ones this machine knows that the schedule calculator
    /// accepts. Suggestions only; whatever is typed is the calculator's to accept or refuse.
    /// </summary>
    public IReadOnlyList<string> TimeZones { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, Guid databaseId, CancellationToken cancellationToken)
    {
        if (await LoadWithScheduleAsync(id, databaseId, cancellationToken) is { } missing)
        {
            return Missing(missing);
        }

        // What it says now, to be changed; or, for a new one, enabled and otherwise for the user to say.
        Input = Existing is null
            ? new ScheduleInput { Enabled = true }
            : new ScheduleInput { CronExpression = Existing.CronExpression, TimeZoneId = Existing.TimeZoneId, Enabled = Existing.Enabled };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, Guid databaseId, CancellationToken cancellationToken)
    {
        if (await LoadWithScheduleAsync(id, databaseId, cancellationToken) is { } missing)
        {
            return Missing(missing);
        }

        var request = new BackupScheduleRequest { CronExpression = Input.CronExpression, TimeZoneId = Input.TimeZoneId, Enabled = Input.Enabled };
        var creating = Existing is null;
        var result = creating
            ? await schedules.CreateAsync(databaseId, request, cancellationToken)
            : await schedules.UpdateAsync(databaseId, request, cancellationToken);

        return Outcome(result.Status, id, databaseId, creating ? "Backup schedule created." : "Backup schedule updated.");
    }

    public Task<IActionResult> OnPostEnableAsync(Guid id, Guid databaseId, CancellationToken cancellationToken) =>
        SetEnabledAsync(id, databaseId, enabled: true, cancellationToken);

    public Task<IActionResult> OnPostDisableAsync(Guid id, Guid databaseId, CancellationToken cancellationToken) =>
        SetEnabledAsync(id, databaseId, enabled: false, cancellationToken);

    private async Task<IActionResult> SetEnabledAsync(Guid id, Guid databaseId, bool enabled, CancellationToken cancellationToken)
    {
        if (await LoadWithScheduleAsync(id, databaseId, cancellationToken) is { } missing)
        {
            return Missing(missing);
        }

        if (Existing is null)
        {
            return Missing("Backup schedule");
        }

        // The schedule as it is, with only this changed. The next run that follows is the service's to work out.
        var result = await schedules.UpdateAsync(
            databaseId,
            new BackupScheduleRequest { CronExpression = Existing.CronExpression, TimeZoneId = Existing.TimeZoneId, Enabled = enabled },
            cancellationToken);

        Input = new ScheduleInput { CronExpression = Existing.CronExpression, TimeZoneId = Existing.TimeZoneId, Enabled = enabled };
        return Outcome(result.Status, id, databaseId, enabled ? "Backup schedule enabled." : "Backup schedule disabled.");
    }

    private IActionResult Outcome(BackupScheduleStatus status, Guid id, Guid databaseId, string done)
    {
        switch (status)
        {
            case BackupScheduleStatus.Ok:
                // The schedule is saved. No backup was made by this, and none is claimed.
                Announce(StatusTone.Success, done);
                return RedirectToPage("/Instances/Databases/Schedule/Index", new { id, databaseId });

            case BackupScheduleStatus.DatabaseNotFound:
                return Missing("Database");

            case BackupScheduleStatus.ScheduleNotFound:
                return Missing("Backup schedule");
        }

        if (Rejections.ScheduleFieldError(status) is { } invalid)
        {
            ModelState.AddModelError($"{nameof(Input)}.{invalid.Field}", invalid.Message);
            return Invalid();
        }

        return Rejected(Rejections.For(status)!);
    }

    private async Task<string?> LoadWithScheduleAsync(Guid id, Guid databaseId, CancellationToken cancellationToken)
    {
        if (await LoadAsync(id, databaseId, cancellationToken) is { } missing)
        {
            return missing;
        }

        var result = await schedules.GetAsync(databaseId, cancellationToken);
        if (result.Status == BackupScheduleStatus.DatabaseNotFound)
        {
            return "Database";
        }

        Existing = result.Schedule;
        TimeZones = TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => zone.Id)
            .Prepend("UTC")
            .Where(calculator.IsTimeZone)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return null;
    }

    /// <summary>What the form holds: the fields of <see cref="BackupScheduleRequest"/>, which has no others.</summary>
    public sealed class ScheduleInput
    {
        public string? CronExpression { get; set; }

        public string? TimeZoneId { get; set; }

        public bool Enabled { get; set; }
    }
}
