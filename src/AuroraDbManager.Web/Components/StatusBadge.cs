using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AuroraDbManager.Web.Components;

/// <summary>What a status means to someone looking at it, whatever it is the status of.</summary>
public enum StatusTone
{
    /// <summary>As it should be: running, ready, completed, healthy.</summary>
    Success,

    /// <summary>On its way: provisioning, creating, deleting, pending, a job at work.</summary>
    Progress,

    /// <summary>Not in use, and not a problem: stopped, disabled.</summary>
    Neutral,

    /// <summary>Works, but wants a look: degraded.</summary>
    Warning,

    /// <summary>Did not work: failed, unhealthy.</summary>
    Danger
}

/// <summary>
/// A status as the UI shows it: a word, and a tone that picks the colour and the shape of the
/// mark next to the word. The word is always there, so the colour is never the only thing that
/// tells one status from another.
/// </summary>
/// <param name="Label">The status in words.</param>
/// <param name="Tone">What it means.</param>
public sealed record StatusBadge(string Label, StatusTone Tone)
{
    public static StatusBadge For(InstanceStatus status) => status switch
    {
        InstanceStatus.Running => new("Running", StatusTone.Success),
        InstanceStatus.Provisioning => new("Provisioning", StatusTone.Progress),
        InstanceStatus.Stopped => new("Stopped", StatusTone.Neutral),
        _ => new("Failed", StatusTone.Danger)
    };

    public static StatusBadge For(DatabaseStatus status) => status switch
    {
        DatabaseStatus.Ready => new("Ready", StatusTone.Success),
        DatabaseStatus.Creating => new("Creating", StatusTone.Progress),
        DatabaseStatus.Deleting => new("Deleting", StatusTone.Progress),
        _ => new("Failed", StatusTone.Danger)
    };

    public static StatusBadge For(JobStatus status) => status switch
    {
        JobStatus.Completed => new("Completed", StatusTone.Success),
        JobStatus.Running => new("Running", StatusTone.Progress),
        JobStatus.Pending => new("Pending", StatusTone.Progress),
        _ => new("Failed", StatusTone.Danger)
    };

    public static StatusBadge For(BackupStatus status) => status switch
    {
        BackupStatus.Completed => new("Completed", StatusTone.Success),
        BackupStatus.Running => new("Running", StatusTone.Progress),
        BackupStatus.Pending => new("Pending", StatusTone.Progress),
        _ => new("Failed", StatusTone.Danger)
    };

    public static StatusBadge For(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => new("Healthy", StatusTone.Success),
        HealthStatus.Degraded => new("Degraded", StatusTone.Warning),
        _ => new("Unhealthy", StatusTone.Danger)
    };

    /// <summary>
    /// Whether a database port is published on the host. Enabled is not "good": it is something
    /// to be aware of, and is marked that way.
    /// </summary>
    public static StatusBadge ForExternalAccess(bool enabled) =>
        enabled ? new("Enabled", StatusTone.Warning) : new("Disabled", StatusTone.Neutral);

    /// <summary>
    /// What is known of a backup's integrity, which is what the backup's record says and nothing
    /// worked out here: a completed backup has the checksum that was verified against the stored
    /// bytes before it was completed, or, if it is from before checksums were recorded, none.
    /// Null while there is nothing to say: a backup that is not finished, or failed, has no artifact.
    /// </summary>
    public static StatusBadge? ForIntegrity(BackupResponse backup) => backup switch
    {
        { Status: BackupStatus.Completed, Checksum: not null } => new("Verified", StatusTone.Success),
        { Status: BackupStatus.Completed } => new("No checksum", StatusTone.Neutral),
        _ => null
    };

    public static StatusBadge ForEnabled(bool enabled) =>
        enabled ? new("Enabled", StatusTone.Success) : new("Disabled", StatusTone.Neutral);

    /// <summary>The tone as a CSS class suffix and icon name.</summary>
    public string ToneName => Tone.ToString().ToLowerInvariant();
}
