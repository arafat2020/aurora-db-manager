using AuroraDbManager.Api.Application.Credentials;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Jobs.RotateCredential;

/// <summary>
/// Replaces the password of an instance's database administrator: in the database server, through
/// <see cref="IAdminCredentialManager"/>, and in <see cref="IInstanceSecretStore"/>, which is where
/// everything of Aurora gets it from. The two cannot be changed in one transaction, so the job is
/// built to be stopped anywhere and run again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two passwords, both stored.</b> The replacement was generated and stored, encrypted, when
/// the rotation was accepted. An attempt generates nothing. It finds out which of the two the
/// server accepts and goes on from there:
/// </para>
/// <list type="bullet">
/// <item>the stored password: the server is changed to the replacement, and asked whether it now accepts it;</item>
/// <item>the replacement: an earlier attempt changed the server and got no further, and nothing is changed again;</item>
/// <item>neither: the server's password is not one Aurora has. Nothing is changed and the attempt fails
/// with <see cref="CredentialErrorCodes.RecoveryRequired"/>.</item>
/// </list>
/// <para>
/// Only when the server is known to accept the replacement does the replacement become the stored
/// password, in one statement, and the server is then asked once more, with what the store now
/// returns. If there is no replacement at all, an earlier attempt of this job got that far, and
/// what is left is that last check.
/// </para>
/// <para>
/// <b>A failed job leaves the replacement where it is.</b> The server may have been changed to it.
/// The next rotation that is requested finds it and finishes with it, rather than start over with
/// a password the server has never heard of.
/// </para>
/// <para>
/// <b>The result.</b> A rotation that got all the way, and only such a one, puts its new password
/// on offer: it can be retrieved once, for a limited time, through
/// <see cref="CredentialRotationService.RetrieveResultAsync"/>. The job itself never holds it.
/// </para>
/// <para>
/// <b>Nothing but the password is touched.</b> No container is stopped, replaced or given another
/// environment, and no volume is involved: the change is a statement in the running server.
/// </para>
/// </remarks>
public sealed class RotateCredentialHandler(
    AppDbContext db,
    IInstanceSecretStore secrets,
    IEnumerable<IAdminCredentialManager> managers,
    IOptions<CredentialOptions> options,
    TimeProvider timeProvider,
    ILogger<RotateCredentialHandler> logger) : IJobHandler
{
    public JobType Type => JobType.RotateCredential;

    public async Task ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == job.InstanceId, cancellationToken)
            ?? throw new JobExecutionException(DatabaseErrorCodes.InstanceNotFound, "The instance no longer exists.");

        // Not started here: an instance that stopped is retried, and fails the job if it stays down.
        if (instance.Status != InstanceStatus.Running)
        {
            throw new JobExecutionException(DatabaseErrorCodes.InstanceNotReady, "The instance is not running.");
        }

        var manager = managers.FirstOrDefault(candidate => candidate.Engine == instance.Engine)
            ?? throw new JobExecutionException(
                CredentialErrorCodes.DatabaseFailed, "Passwords cannot be rotated for the instance's engine.");

        using var scope = logger.BeginScope(
            "Job {JobId} attempt {Attempt}: rotating the administrator password of {Engine} instance {InstanceId}",
            job.Id, job.Attempt, instance.Engine, instance.Id);
        logger.LogInformation("Starting credential rotation for instance {InstanceId}", instance.Id);

        var (stored, replacement) = await StoreAsync(
            async () => (
                await secrets.GetOrCreateAdminPasswordAsync(instance.Id, cancellationToken),
                await secrets.GetAdminPasswordReplacementAsync(instance.Id, cancellationToken)),
            "The stored password could not be read.",
            cancellationToken);

        if (replacement is null)
        {
            logger.LogInformation(
                "Instance {InstanceId} has no replacement password waiting: an earlier attempt stored it as the password",
                instance.Id);
            await VerifyStoredAsync(manager, instance, stored, cancellationToken);
            await OfferAsync(job, cancellationToken);
            return;
        }

        if (await AcceptsAsync(manager, instance, stored, cancellationToken))
        {
            await DatabaseAsync(
                () => manager.ChangeAdminPasswordAsync(instance, stored, replacement, cancellationToken), cancellationToken);

            // Not taken on the statement's word: a new connection has to get in with it.
            if (!await AcceptsAsync(manager, instance, replacement, cancellationToken))
            {
                throw new JobExecutionException(
                    CredentialErrorCodes.VerificationFailed,
                    "The database server did not accept the new password after changing to it. The stored password was not replaced.");
            }
        }
        else if (await AcceptsAsync(manager, instance, replacement, cancellationToken))
        {
            logger.LogInformation(
                "The database server of instance {InstanceId} already accepts the replacement password; an earlier attempt changed it",
                instance.Id);
        }
        else
        {
            throw new JobExecutionException(
                CredentialErrorCodes.RecoveryRequired,
                "The database server accepts neither the stored password nor its replacement. Nothing was changed.");
        }

        // The server accepts the replacement, so that is what Aurora has to connect with from now on.
        await StoreAsync(
            async () =>
            {
                await secrets.PromoteAdminPasswordReplacementAsync(instance.Id, cancellationToken);
                return true;
            },
            "The database server has the new password, but it could not be stored as the password Aurora uses. It is kept, and the rotation finishes when it is run again.",
            cancellationToken);

        var current = await StoreAsync(
            () => secrets.GetOrCreateAdminPasswordAsync(instance.Id, cancellationToken),
            "The stored password could not be read.",
            cancellationToken);
        await VerifyStoredAsync(manager, instance, current, cancellationToken);
        await OfferAsync(job, cancellationToken);

        logger.LogInformation("Credential rotation for instance {InstanceId} completed", instance.Id);
    }

    // Nothing to record on the instance: it runs as before, with whichever password its server
    // accepts. The job is the record of the failure, and the replacement stays stored.
    public Task OnFailedAsync(Job job, CancellationToken cancellationToken)
    {
        logger.LogWarning("Credential rotation failed for instance {InstanceId}", job.InstanceId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Only now, with the new password stored and seen to work, is it put on offer for whoever is
    /// to use it: once, and for a while. Nothing of it is written here; what is recorded is that
    /// the stored password is this job's result. Repeating it, after an attempt that was
    /// interrupted right here, neither extends the offer nor renews one that was taken up.
    /// </summary>
    private Task OfferAsync(Job job, CancellationToken cancellationToken) =>
        StoreAsync(
            async () =>
            {
                var expiresAt = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(options.Value.ResultTtlMinutes);
                await secrets.OfferAdminPasswordAsync(job.InstanceId, job.Id, expiresAt, cancellationToken);
                return true;
            },
            "The password was rotated, but it could not be made available for retrieval. The rotation finishes when it is run again.",
            cancellationToken);

    /// <summary>The last check: what the store returns is what the server lets in.</summary>
    private static async Task VerifyStoredAsync(
        IAdminCredentialManager manager, Instance instance, string stored, CancellationToken cancellationToken)
    {
        if (!await AcceptsAsync(manager, instance, stored, cancellationToken))
        {
            throw new JobExecutionException(
                CredentialErrorCodes.VerificationFailed, "The database server does not accept the stored password.");
        }
    }

    private static Task<bool> AcceptsAsync(
        IAdminCredentialManager manager, Instance instance, string password, CancellationToken cancellationToken) =>
        DatabaseAsync(() => manager.AuthenticatesAsync(instance, password, cancellationToken), cancellationToken);

    /// <summary>Runs something against the database server; its failures become the job's, with their client-safe code.</summary>
    private static async Task<T> DatabaseAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DatabaseOperationException exception)
        {
            // Its code and message are written for clients.
            throw new JobExecutionException(exception.Code, exception.Message, exception);
        }
        catch (Exception exception)
        {
            // Only the kind of failure is kept. What an unknown exception says is not known to be safe here.
            throw new JobExecutionException(
                CredentialErrorCodes.DatabaseFailed,
                "The database server did not change the administrator password.",
                new InvalidOperationException(exception.GetType().Name));
        }
    }

    private static async Task DatabaseAsync(Func<Task> action, CancellationToken cancellationToken) =>
        await DatabaseAsync(
            async () =>
            {
                await action();
                return true;
            },
            cancellationToken);

    /// <summary>Runs something against the secret store; its failures are reported without what they say.</summary>
    private static async Task<T> StoreAsync<T>(Func<Task<T>> action, string failure, CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new JobExecutionException(
                CredentialErrorCodes.SecretStoreFailed, failure, new InvalidOperationException(exception.GetType().Name));
        }
    }
}
