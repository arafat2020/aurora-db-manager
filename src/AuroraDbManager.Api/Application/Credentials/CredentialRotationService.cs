using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Jobs;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Credentials;

/// <summary>
/// The credential Aurora manages for an instance, the password of its database administrator, and
/// the requests to rotate it. Nothing here reaches a database server: a request has the secret
/// store generate the replacement, and records a <c>rotate_credential</c> job whose handler does
/// the rest. A password is seen in one place only, <see cref="RetrieveResultAsync"/>, which hands
/// the result of a completed rotation out once.
/// </summary>
/// <remarks>
/// <para>
/// <b>The replacement exists before anything is changed.</b> It is stored, encrypted, in the same
/// transaction as the job. From then on the job only ever moves between two known passwords, the
/// stored one and its stored replacement, however often it is retried or the process restarted.
/// A request that finds a replacement left by a rotation that failed does not make another: the
/// database server may already have been changed to that one.
/// </para>
/// <para>
/// <b>One rotation at a time.</b> The jobs table's unique index allows an instance one unfinished
/// job of a type; a second request is refused, not queued.
/// </para>
/// <para>
/// <b>Not while other work is going on.</b> A rotation is refused while a database of the instance
/// is being created, deleted, backed up or restored, and those are refused while a rotation is
/// unfinished, so that no job connects with a password that stops working under it. These are
/// checks made before a job is recorded and can be overtaken by a request made at the same
/// moment; a job that does lose that race fails an attempt and connects with the new password on
/// its next one.
/// </para>
/// </remarks>
public sealed class CredentialRotationService(
    AppDbContext db,
    IInstanceSecretStore secrets,
    ICurrentUser currentUser,
    JobQueue jobQueue,
    IOptions<JobOptions> jobOptions,
    TimeProvider timeProvider,
    ILogger<CredentialRotationService> logger)
{
    /// <summary>Where the one-time result of that rotation stands; never the password.</summary>
    /// <returns>Null if there is no such rotation of that instance.</returns>
    public async Task<CredentialResultState?> GetResultStateAsync(Guid instanceId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await RotationAsync(instanceId, jobId, cancellationToken);
        return job is null ? null : State(job, await secrets.GetAdminPasswordDeliveryAsync(instanceId, cancellationToken));
    }

    /// <summary>
    /// Hands out the new password of a completed rotation, once. This is the only place a password
    /// leaves the application, and the caller is answerable for who asked: it is for operators and
    /// administrators, any of them, not only the one who asked for the rotation.
    /// </summary>
    /// <remarks>
    /// Recording that it was handed out, reading it and preparing the answer happen inside one
    /// transaction, which is committed last. If reading or preparing fails, the record is rolled
    /// back with it and the password is still there to be retrieved: a failure in here cannot use
    /// the one time up. Of two requests at once, the database lets one record it and tells the other no.
    /// </remarks>
    public async Task<RetrieveCredentialResult> RetrieveResultAsync(
        Guid instanceId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await RotationAsync(instanceId, jobId, cancellationToken);
        if (job is null)
        {
            return new RetrieveCredentialResult(RetrieveCredentialStatus.NotFound);
        }

        var instance = await db.Instances.AsNoTracking().FirstAsync(i => i.Id == instanceId, cancellationToken);
        CredentialRotationResultResponse result;

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var password = job.Status == JobStatus.Completed
                ? await secrets.ClaimAdminPasswordAsync(instanceId, jobId, timeProvider.GetUtcNow().UtcDateTime, cancellationToken)
                : null;
            if (password is null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new RetrieveCredentialResult(
                    State(job, await secrets.GetAdminPasswordDeliveryAsync(instanceId, cancellationToken)) switch
                    {
                        CredentialResultState.Retrieved => RetrieveCredentialStatus.AlreadyRetrieved,
                        CredentialResultState.Expired => RetrieveCredentialStatus.Expired,
                        _ => RetrieveCredentialStatus.NotAvailable
                    });
            }

            result = new CredentialRotationResultResponse(
                instance.Id, job.Id, instance.Engine, EngineDefaults.AdminUser(instance.Engine), password);

            // Last: until here, anything that went wrong has left the password retrievable.
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "The new administrator password of instance {InstanceId} from rotation job {JobId} was retrieved by user {UserId}; it cannot be retrieved again",
            instanceId, jobId, currentUser.UserId);
        return new RetrieveCredentialResult(RetrieveCredentialStatus.Retrieved, result);
    }

    private Task<Job?> RotationAsync(Guid instanceId, Guid jobId, CancellationToken cancellationToken) =>
        db.Jobs.AsNoTracking().FirstOrDefaultAsync(
            j => j.Id == jobId && j.InstanceId == instanceId && j.Type == JobType.RotateCredential, cancellationToken);

    private CredentialResultState State(Job job, AdminPasswordDelivery? delivery)
    {
        if (job.Status != JobStatus.Completed || delivery is null || delivery.JobId != job.Id)
        {
            return CredentialResultState.Unavailable;
        }

        if (delivery.ConsumedAt is not null)
        {
            return CredentialResultState.Retrieved;
        }

        return delivery.ExpiresAt > timeProvider.GetUtcNow().UtcDateTime ? CredentialResultState.Available : CredentialResultState.Expired;
    }

    /// <returns>The instance's managed credential, or null if there is no such instance.</returns>
    public async Task<InstanceCredentialResponse?> GetAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        return instance is null ? null : await DescribeAsync(instance, cancellationToken);
    }

    /// <summary>
    /// Accepts a rotation of the instance's administrator password, or says why not. Accepted is
    /// not rotated: the job does that, and its status says how it went.
    /// </summary>
    public async Task<RotateCredentialResult> RequestAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
        if (instance is null)
        {
            return new RotateCredentialResult(RotateCredentialStatus.NotFound);
        }

        // Only a running instance has a server to change the password in. It is not started for this.
        if (instance.Status != InstanceStatus.Running)
        {
            return new RotateCredentialResult(RotateCredentialStatus.InstanceNotReady);
        }

        if (Busy(await db.UnfinishedJobTypesOfInstanceAsync(instanceId, cancellationToken)) is { } busy)
        {
            return new RotateCredentialResult(busy);
        }

        var job = Job.Create(JobType.RotateCredential, instanceId, jobOptions.Value.MaxAttempts, timeProvider.GetUtcNow().UtcDateTime);

        // The replacement and the job that puts it in place: stored together or not at all.
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            if (!await secrets.StageAdminPasswordReplacementAsync(instanceId, cancellationToken))
            {
                return new RotateCredentialResult(RotateCredentialStatus.NotManaged);
            }

            db.Jobs.Add(job);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // The unique index: a concurrent request's rotation got in first. Or the instance was deleted.
                await transaction.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();

                return new RotateCredentialResult(
                    await db.Instances.AnyAsync(i => i.Id == instanceId, cancellationToken)
                        ? RotateCredentialStatus.RotationInProgress
                        : RotateCredentialStatus.NotFound);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        // Queued only after the commit, so the worker never sees a job that is not in the database.
        // If the process dies between the commit and this line the job stays pending until the
        // next start, when job recovery queues it. Not cancellable: the job is already committed.
        await jobQueue.EnqueueAsync(job.Id, CancellationToken.None);
        logger.LogInformation(
            "Job {JobId} ({JobType}) for instance {InstanceId} queued",
            job.Id, job.Type, job.InstanceId);

        return new RotateCredentialResult(
            RotateCredentialStatus.Accepted,
            new CredentialRotationResponse(await DescribeAsync(instance, cancellationToken), JobResponse.From(job)));
    }

    private async Task<InstanceCredentialResponse> DescribeAsync(Instance instance, CancellationToken cancellationToken)
    {
        var stored = await secrets.GetAdminCredentialStateAsync(instance.Id, cancellationToken);

        var rotations = db.Jobs.AsNoTracking().Where(j => j.InstanceId == instance.Id && j.Type == JobType.RotateCredential);
        var latest = await rotations.OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id).FirstOrDefaultAsync(cancellationToken);
        var lastRotatedAt = await rotations
            .Where(j => j.Status == JobStatus.Completed)
            .OrderByDescending(j => j.CompletedAt)
            .Select(j => j.CompletedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var rotation = latest switch
        {
            { Status: JobStatus.Pending or JobStatus.Running } => CredentialRotationState.InProgress,
            // A replacement nobody is working on: the rotation that made it failed part-way.
            _ when stored == AdminCredentialState.ReplacementStaged => CredentialRotationState.Incomplete,
            _ => CredentialRotationState.Idle
        };

        return new InstanceCredentialResponse(
            instance.Id,
            instance.Engine,
            EngineDefaults.AdminUser(instance.Engine),
            // The password belongs to a server that exists; before that there is nothing to connect to with it.
            stored != AdminCredentialState.None && instance.Status != InstanceStatus.Provisioning,
            rotation,
            lastRotatedAt,
            latest is null ? null : JobResponse.From(latest),
            await ResultOfAsync(instance.Id, cancellationToken));
    }

    private async Task<CredentialResultStatusResponse?> ResultOfAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        if (await secrets.GetAdminPasswordDeliveryAsync(instanceId, cancellationToken) is not { } delivery)
        {
            return null;
        }

        var state = delivery.ConsumedAt is not null ? CredentialResultState.Retrieved
            : delivery.ExpiresAt > timeProvider.GetUtcNow().UtcDateTime ? CredentialResultState.Available
            : CredentialResultState.Expired;
        return new CredentialResultStatusResponse(delivery.JobId, state, delivery.ExpiresAt);
    }

    /// <summary>The unfinished work that keeps the password from being rotated; null if there is none.</summary>
    private static RotateCredentialStatus? Busy(IReadOnlyCollection<JobType> unfinished)
    {
        if (unfinished.Contains(JobType.RotateCredential))
        {
            return RotateCredentialStatus.RotationInProgress;
        }

        if (unfinished.Contains(JobType.BackupDatabase))
        {
            return RotateCredentialStatus.BackupInProgress;
        }

        if (unfinished.Contains(JobType.RestoreDatabase))
        {
            return RotateCredentialStatus.RestoreInProgress;
        }

        return unfinished.Count > 0 ? RotateCredentialStatus.DatabaseOperationInProgress : null;
    }
}

/// <param name="Status">Whether the rotation was accepted, and if not, why.</param>
/// <param name="Operation">The credential and the job; set only when <paramref name="Status"/> is <see cref="RotateCredentialStatus.Accepted"/>.</param>
public sealed record RotateCredentialResult(RotateCredentialStatus Status, CredentialRotationResponse? Operation = null);

/// <param name="Status">Whether the password was handed out, and if not, why.</param>
/// <param name="Result">The credential; set only when <paramref name="Status"/> is <see cref="RetrieveCredentialStatus.Retrieved"/>.</param>
public sealed record RetrieveCredentialResult(RetrieveCredentialStatus Status, CredentialRotationResultResponse? Result = null);

public enum RetrieveCredentialStatus
{
    Retrieved,

    /// <summary>There is no such rotation of that instance.</summary>
    NotFound,

    /// <summary>The rotation did not complete, or a newer rotation has replaced its password.</summary>
    NotAvailable,

    /// <summary>It was handed out before.</summary>
    AlreadyRetrieved,

    /// <summary>Nobody retrieved it in time.</summary>
    Expired
}

public enum RotateCredentialStatus
{
    Accepted,
    NotFound,

    /// <summary>Not accepted because the instance is not running.</summary>
    InstanceNotReady,

    /// <summary>Not accepted because Aurora holds no password for the instance.</summary>
    NotManaged,

    /// <summary>Not accepted because a rotation of this instance's password is already pending or running.</summary>
    RotationInProgress,

    /// <summary>Not accepted because one of the instance's databases is being created or deleted.</summary>
    DatabaseOperationInProgress,

    /// <summary>Not accepted because one of the instance's databases is being backed up.</summary>
    BackupInProgress,

    /// <summary>Not accepted because one of the instance's databases is being restored.</summary>
    RestoreInProgress
}
