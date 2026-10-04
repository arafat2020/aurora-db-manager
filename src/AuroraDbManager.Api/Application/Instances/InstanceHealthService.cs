using System.Text.Json.Serialization;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AuroraDbManager.Api.Application.Instances;

/// <summary>
/// Reports how an instance's database server is doing right now, next to what its metadata says.
/// The two are different things and may disagree: an instance that is <c>running</c> on record
/// can have a server that is down. This only observes. The instance's status is never changed
/// here, whatever is found; that is for provisioning and reconciliation.
/// </summary>
public sealed class InstanceHealthService(AppDbContext db, IInstanceRuntimeProbe probe, TimeProvider timeProvider)
{
    /// <returns>The instance's health, or null if there is no such instance.</returns>
    public async Task<InstanceHealthResponse?> GetAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        var state = await probe.InspectAsync(instance, cancellationToken);
        var checkedAt = timeProvider.GetUtcNow().UtcDateTime;

        return state switch
        {
            InstanceRuntimeState.Ready => Response(HealthStatus.Healthy, null, exists: true, running: true, reachable: true),

            // The server is up but not answering: starting, recovering, or overloaded.
            InstanceRuntimeState.DatabaseNotReady =>
                Response(HealthStatus.Degraded, "database_not_ready", exists: true, running: true, reachable: false),

            InstanceRuntimeState.ContainerNotRunning =>
                Response(HealthStatus.Unhealthy, "container_not_running", exists: true, running: false, reachable: false),

            InstanceRuntimeState.ContainerMissing =>
                Response(HealthStatus.Unhealthy, "container_missing", exists: false, running: false, reachable: false),

            // Nothing is known about the instance; it may well be serving. Not reported as down.
            _ => Response(HealthStatus.Degraded, "runtime_unavailable", exists: null, running: null, reachable: null)
        };

        InstanceHealthResponse Response(HealthStatus status, string? reason, bool? exists, bool? running, bool? reachable) => new(
            instance.Id,
            status,
            instance.Status,
            reason,
            new InstanceContainerHealth(exists, running),
            new InstanceDatabaseHealth(reachable),
            checkedAt);
    }
}

/// <param name="InstanceId">The instance.</param>
/// <param name="Status">
/// What was observed just now: <c>healthy</c> (container running, database accepting connections),
/// <c>degraded</c> (container running but the database not answering, or the runtime could not be
/// asked) or <c>unhealthy</c> (container missing or not running).
/// </param>
/// <param name="InstanceStatus">The instance's status on record, which this request neither derives from nor changes.</param>
/// <param name="Reason">
/// Why the status is not <c>healthy</c>: <c>database_not_ready</c>, <c>container_not_running</c>,
/// <c>container_missing</c> or <c>runtime_unavailable</c>. Left out when it is.
/// </param>
/// <param name="Container">The instance's container.</param>
/// <param name="Database">The database server in it.</param>
/// <param name="CheckedAt">UTC time of the observation.</param>
public sealed record InstanceHealthResponse(
    Guid InstanceId,
    HealthStatus Status,
    InstanceStatus InstanceStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason,
    InstanceContainerHealth Container,
    InstanceDatabaseHealth Database,
    DateTime CheckedAt);

/// <param name="Exists">Whether the instance has a container of its own; null if that could not be found out.</param>
/// <param name="Running">Whether it is running; null if that could not be found out.</param>
public sealed record InstanceContainerHealth(bool? Exists, bool? Running);

/// <param name="Reachable">Whether the database accepts connections; null if that could not be found out.</param>
public sealed record InstanceDatabaseHealth(bool? Reachable);
