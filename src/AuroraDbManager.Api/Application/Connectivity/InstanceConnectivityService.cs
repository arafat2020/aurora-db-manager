using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Errors;
using AuroraDbManager.Api.Infrastructure.Docker;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Connectivity;

/// <summary>
/// How an instance's database server is reached, and whether from outside the Docker network.
/// Reaching it from outside is off for every instance until an administrator turns it on for
/// that instance; it is then one host port, picked by <see cref="ExternalPortAllocator"/>, on the
/// address the server is configured to bind (<see cref="ExternalAccessOptions"/>). Exposure is of
/// the instance: its databases share the server, and so its port.
/// </summary>
/// <remarks>
/// <para>
/// <b>Changing it restarts the server.</b> Docker cannot change what a container publishes, so
/// the provisioner replaces the container on the same data volume
/// (<see cref="IInstanceProvisioner.ApplyExternalAccessAsync"/>). That interrupts the database for
/// as long as it takes to stop and start, which is why it is refused while a job is working in
/// the instance.
/// </para>
/// <para>
/// <b>The record never claims what Docker does not do.</b> The new state is written inside a
/// transaction that is committed only after the provisioner has reported the new container
/// running, ready and checked. If Docker fails, or the process dies, the transaction is rolled
/// back and the record says what it said before; a container left differently is brought back in
/// line with the record when the instance is next reconciled.
/// </para>
/// <para>
/// <b>Two instances never get one port.</b> Writing the port is what reserves it: the unique
/// index on the column refuses the second writer, who then tries the next port. The same goes
/// for a port that Docker finds taken by something that is not an instance.
/// </para>
/// </remarks>
public sealed class InstanceConnectivityService(
    AppDbContext db,
    IInstanceProvisioner provisioner,
    ExternalPortAllocator allocator,
    IInstanceNetwork network,
    IOptions<ExternalAccessOptions> options,
    TimeProvider timeProvider,
    ILogger<InstanceConnectivityService> logger)
{
    /// <summary>How many ports are tried before giving up, when each one turns out to be taken after all.</summary>
    public const int MaxAllocationAttempts = 5;

    /// <returns>How the instance is reached, or null if there is no such instance.</returns>
    public async Task<InstanceConnectionResponse?> GetAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        return instance is null ? null : Describe(instance);
    }

    /// <returns>How the database is reached, or null if there is no such database.</returns>
    public async Task<DatabaseConnectionResponse?> GetForDatabaseAsync(Guid databaseId, CancellationToken cancellationToken)
    {
        var database = await db.Databases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == databaseId, cancellationToken);
        if (database is null)
        {
            return null;
        }

        var instance = await db.Instances.AsNoTracking().FirstAsync(i => i.Id == database.InstanceId, cancellationToken);
        var connection = Describe(instance);

        return new DatabaseConnectionResponse(
            database.Id,
            instance.Id,
            database.Name,
            instance.Engine,
            connection.Username,
            connection.Internal,
            connection.External,
            new ConnectionStringsResponse(
                ConnectionStrings.Template(instance.Engine, connection.Internal.Host, connection.Internal.Port, database.Name),
                connection.External is { Enabled: true, Port: { } port }
                    ? ConnectionStrings.Template(instance.Engine, connection.External.Host, port, database.Name)
                    : null));
    }

    /// <summary>
    /// Publishes the instance's database port on a host port of Aurora's choosing. Returns once
    /// the server is running with it, or with the reason it is not; in that case nothing has changed.
    /// </summary>
    public async Task<ExternalAccessResult> EnableAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (await RefusalAsync(instance, wanted: true, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var tried = new HashSet<int>();
        for (var attempt = 1; attempt <= MaxAllocationAttempts; attempt++)
        {
            int? port;
            try
            {
                port = await allocator.NextAsync(tried, cancellationToken);
            }
            catch (InstanceProvisioningException exception)
            {
                return ExternalAccessResult.Failed(exception);
            }

            if (port is null)
            {
                break;
            }

            tried.Add(port.Value);

            switch (await ChangeAsync(instance!, wanted: true, i => i.EnableExternalAccess(port.Value, Now), cancellationToken))
            {
                case { Status: ExternalAccessStatus.PortTaken }:
                    logger.LogInformation(
                        "Host port {HostPort} was taken before instance {InstanceId} could have it; trying another",
                        port, instanceId);
                    continue;
                case { Status: ExternalAccessStatus.Changed } changed:
                    logger.LogInformation("External access of instance {InstanceId} enabled on host port {HostPort}", instanceId, port);
                    return changed;
                case var other:
                    return other;
            }
        }

        logger.LogWarning(
            "No host port could be allocated to instance {InstanceId} in {PortRangeStart}-{PortRangeEnd}",
            instanceId, options.Value.PortRangeStart, options.Value.PortRangeEnd);
        return new ExternalAccessResult(
            ExternalAccessStatus.PortAllocationFailed,
            ErrorCode: ErrorCodes.PortAllocationFailed,
            ErrorMessage: "No free host port could be allocated in the configured port range.");
    }

    /// <summary>
    /// Stops publishing the instance's database port and gives its host port up. Returns once the
    /// server is running without it, or with the reason it is not; in that case nothing has changed.
    /// </summary>
    public async Task<ExternalAccessResult> DisableAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (await RefusalAsync(instance, wanted: false, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var result = await ChangeAsync(instance!, wanted: false, i => i.DisableExternalAccess(Now), cancellationToken);
        if (result.Status == ExternalAccessStatus.Changed)
        {
            logger.LogInformation("External access of instance {InstanceId} disabled", instanceId);
        }

        return result;
    }

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Why the instance's external access cannot be changed to <paramref name="wanted"/> right now; null if it can.</summary>
    private async Task<ExternalAccessResult?> RefusalAsync(Instance? instance, bool wanted, CancellationToken cancellationToken)
    {
        if (instance is null)
        {
            return new ExternalAccessResult(ExternalAccessStatus.NotFound);
        }

        if (instance.ExternalAccessEnabled == wanted)
        {
            return new ExternalAccessResult(wanted ? ExternalAccessStatus.AlreadyEnabled : ExternalAccessStatus.AlreadyDisabled);
        }

        // Only a running instance has a server to restart. One that is being provisioned gets its
        // container from its job, and a failed or stopped one has no working server to apply this to.
        if (instance.Status != InstanceStatus.Running)
        {
            return new ExternalAccessResult(ExternalAccessStatus.InstanceNotReady);
        }

        // The server is about to be restarted; a job that is working in it would fail.
        var unfinished = await db.Jobs
            .Where(j => j.InstanceId == instance.Id && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .Select(j => j.Type)
            .ToListAsync(cancellationToken);
        if (unfinished.Contains(JobType.RotateCredential))
        {
            return new ExternalAccessResult(ExternalAccessStatus.CredentialRotationInProgress);
        }

        if (unfinished.Contains(JobType.BackupDatabase))
        {
            return new ExternalAccessResult(ExternalAccessStatus.BackupInProgress);
        }

        if (unfinished.Contains(JobType.RestoreDatabase))
        {
            return new ExternalAccessResult(ExternalAccessStatus.RestoreInProgress);
        }

        return unfinished.Count > 0 ? new ExternalAccessResult(ExternalAccessStatus.DatabaseOperationInProgress) : null;
    }

    /// <summary>
    /// Writes the new state, has the provisioner make it true, and commits; or rolls back and
    /// leaves the record as it was.
    /// </summary>
    private async Task<ExternalAccessResult> ChangeAsync(
        Instance instance, bool wanted, Action<Instance> change, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            change(instance);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request changed the instance's external access, or its status, first.
                await UndoAsync(instance, transaction);
                return await RefusalAsync(instance, wanted, CancellationToken.None)
                    ?? new ExternalAccessResult(ExternalAccessStatus.InstanceNotReady);
            }
            catch (DbUpdateException)
            {
                // The unique index: another instance has just been given this port.
                await UndoAsync(instance, transaction);
                return new ExternalAccessResult(ExternalAccessStatus.PortTaken);
            }

            try
            {
                // Not cancellable: a database server that is half-way through being restarted is
                // not left there because a browser tab was closed.
                await provisioner.ApplyExternalAccessAsync(instance, CancellationToken.None);
            }
            catch (InstanceProvisioningException exception)
            {
                await UndoAsync(instance, transaction);
                logger.LogWarning(
                    "External access of instance {InstanceId} could not be changed: {ErrorCode}",
                    instance.Id, exception.Code);
                return exception.Code == DockerProvisioningErrors.PortAlreadyInUse
                    ? new ExternalAccessResult(ExternalAccessStatus.PortTaken)
                    : ExternalAccessResult.Failed(exception);
            }

            await transaction.CommitAsync(CancellationToken.None);
            return new ExternalAccessResult(ExternalAccessStatus.Changed, Describe(instance));
        }
        catch
        {
            // Whatever it was, the record must not keep what was not made true.
            await UndoAsync(instance, transaction);
            throw;
        }
    }

    private async Task UndoAsync(Instance instance, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Already rolled back.
        }

        // The entity as the database has it again, not as this attempt left it.
        await db.Entry(instance).ReloadAsync(CancellationToken.None);
    }

    private InstanceConnectionResponse Describe(Instance instance)
    {
        var settings = options.Value;
        return new InstanceConnectionResponse(
            instance.Id,
            instance.Engine,
            EngineDefaults.AdminUser(instance.Engine),
            new InternalEndpointResponse(network.NetworkName, network.InternalHost(instance), EngineDefaults.Port(instance.Engine)),
            new ExternalAccessResponse(
                instance.ExternalAccessEnabled,
                instance.ExternalAccessEnabled ? settings.ClientHost : null,
                instance.ExternalPort,
                settings.NormalizedBindAddress));
    }
}

/// <param name="Status">Whether external access was changed, and if not, why.</param>
/// <param name="Connection">How the instance is reached now; set only when <paramref name="Status"/> is <see cref="ExternalAccessStatus.Changed"/>.</param>
/// <param name="ErrorCode">The stable code of the failure; set when the provisioner or the allocator failed.</param>
/// <param name="ErrorMessage">Its message, written for clients.</param>
public sealed record ExternalAccessResult(
    ExternalAccessStatus Status,
    InstanceConnectionResponse? Connection = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    /// <summary>The provisioner's failure, whose code and message are written for clients.</summary>
    public static ExternalAccessResult Failed(InstanceProvisioningException exception) =>
        new(ExternalAccessStatus.Failed, ErrorCode: exception.Code, ErrorMessage: exception.Message);
}

public enum ExternalAccessStatus
{
    Changed,
    NotFound,
    AlreadyEnabled,
    AlreadyDisabled,

    /// <summary>Not changed because the instance is not running.</summary>
    InstanceNotReady,

    /// <summary>Not changed because one of the instance's databases is being created or deleted.</summary>
    DatabaseOperationInProgress,

    /// <summary>Not changed because one of the instance's databases is being backed up.</summary>
    BackupInProgress,

    /// <summary>Not changed because one of the instance's databases is being restored.</summary>
    RestoreInProgress,

    /// <summary>Not changed because the password of the instance's database administrator is being rotated.</summary>
    CredentialRotationInProgress,

    /// <summary>No host port of the configured range could be had.</summary>
    PortAllocationFailed,

    /// <summary>The port that was tried was taken in the meantime. Never returned to a caller: another port is tried.</summary>
    PortTaken,

    /// <summary>The server could not be given the new configuration; see the error code. It was left, or put back, as it was.</summary>
    Failed
}
