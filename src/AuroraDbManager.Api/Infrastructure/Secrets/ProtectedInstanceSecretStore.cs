using System.Security.Cryptography;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Infrastructure.Secrets;

/// <summary>
/// Instance passwords, encrypted with ASP.NET Core Data Protection and kept in the
/// <c>instance_secrets</c> table of the system database. Passwords are generated here and
/// nowhere else: 32 characters drawn from 62 by the operating system's random number generator.
/// </summary>
/// <remarks>
/// The encryption keys are Data Protection's key ring, which by default is a directory in the
/// home of the user running the API. Whoever has both the database and the key ring can read the
/// passwords, and losing the key ring makes them unrecoverable. The password an instance's data
/// directory was initialized with is also visible in the container's configuration
/// (<c>docker inspect</c>) to anyone with access to Docker; after a rotation that one no longer works.
/// </remarks>
public sealed class ProtectedInstanceSecretStore(
    AppDbContext db,
    IDataProtectionProvider dataProtection,
    TimeProvider timeProvider) : IInstanceSecretStore
{
    // Letters and digits only, so the password needs no escaping wherever it ends up.
    private const string PasswordAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const int PasswordLength = 32;

    private readonly IDataProtector _protector = dataProtection.CreateProtector("AuroraDbManager.InstanceSecrets.v1");

    public async Task<string> GetOrCreateAdminPasswordAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var existing = await db.InstanceSecrets.AsNoTracking()
            .FirstOrDefaultAsync(secret => secret.InstanceId == instanceId, cancellationToken);
        if (existing is not null)
        {
            return _protector.Unprotect(existing.ProtectedAdminPassword);
        }

        var password = Generate();

        // Saved immediately: the password must be durable before a database is initialized with it.
        db.InstanceSecrets.Add(new InstanceSecret
        {
            InstanceId = instanceId,
            ProtectedAdminPassword = _protector.Protect(password),
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync(cancellationToken);

        return password;
    }

    public async Task<AdminCredentialState> GetAdminCredentialStateAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var staged = await db.InstanceSecrets.AsNoTracking()
            .Where(secret => secret.InstanceId == instanceId)
            .Select(secret => (bool?)(secret.ProtectedPendingAdminPassword != null))
            .FirstOrDefaultAsync(cancellationToken);

        return staged switch
        {
            null => AdminCredentialState.None,
            true => AdminCredentialState.ReplacementStaged,
            false => AdminCredentialState.Stored
        };
    }

    public async Task<bool> StageAdminPasswordReplacementAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        // Conditional on there being none: of two requests only one writes, and the other finds
        // that one's replacement in place. A replacement is never overwritten.
        var replacement = _protector.Protect(Generate());
        var staged = await db.InstanceSecrets
            .Where(secret => secret.InstanceId == instanceId && secret.ProtectedPendingAdminPassword == null)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(secret => secret.ProtectedPendingAdminPassword, replacement)
                    // The password that was on offer is the one this rotation is about to end.
                    .SetProperty(secret => secret.DeliveryJobId, (Guid?)null)
                    .SetProperty(secret => secret.DeliveryExpiresAt, (DateTime?)null)
                    .SetProperty(secret => secret.DeliveryConsumedAt, (DateTime?)null),
                cancellationToken);

        return staged > 0
            || await GetAdminCredentialStateAsync(instanceId, cancellationToken) != AdminCredentialState.None;
    }

    public async Task<string?> GetAdminPasswordReplacementAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var replacement = await db.InstanceSecrets.AsNoTracking()
            .Where(secret => secret.InstanceId == instanceId)
            .Select(secret => secret.ProtectedPendingAdminPassword)
            .FirstOrDefaultAsync(cancellationToken);

        return replacement is null ? null : _protector.Unprotect(replacement);
    }

    public Task PromoteAdminPasswordReplacementAsync(Guid instanceId, CancellationToken cancellationToken) =>
        // One statement: the row holds either the old password and its replacement, or the new
        // password alone, and never anything in between. The ciphertext is moved as it is.
        db.InstanceSecrets
            .Where(secret => secret.InstanceId == instanceId && secret.ProtectedPendingAdminPassword != null)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(secret => secret.ProtectedAdminPassword, secret => secret.ProtectedPendingAdminPassword!)
                    .SetProperty(secret => secret.ProtectedPendingAdminPassword, (string?)null),
                cancellationToken);

    public Task OfferAdminPasswordAsync(Guid instanceId, Guid jobId, DateTime expiresAt, CancellationToken cancellationToken) =>
        // Not while a replacement is waiting: the stored password is then not the rotation's result.
        db.InstanceSecrets
            .Where(secret => secret.InstanceId == instanceId
                && secret.ProtectedPendingAdminPassword == null
                && (secret.DeliveryJobId == null || secret.DeliveryJobId != jobId))
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(secret => secret.DeliveryJobId, (Guid?)jobId)
                    .SetProperty(secret => secret.DeliveryExpiresAt, (DateTime?)expiresAt)
                    .SetProperty(secret => secret.DeliveryConsumedAt, (DateTime?)null),
                cancellationToken);

    public async Task<AdminPasswordDelivery?> GetAdminPasswordDeliveryAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var delivery = await db.InstanceSecrets.AsNoTracking()
            .Where(secret => secret.InstanceId == instanceId && secret.DeliveryJobId != null)
            .Select(secret => new { secret.DeliveryJobId, secret.DeliveryExpiresAt, secret.DeliveryConsumedAt })
            .FirstOrDefaultAsync(cancellationToken);

        return delivery is null
            ? null
            : new AdminPasswordDelivery(delivery.DeliveryJobId!.Value, Utc(delivery.DeliveryExpiresAt!.Value), delivery.DeliveryConsumedAt);
    }

    public async Task<string?> ClaimAdminPasswordAsync(Guid instanceId, Guid jobId, DateTime utcNow, CancellationToken cancellationToken)
    {
        // One conditional statement: of two callers the second finds it handed out. The database
        // holds the row for the first until its transaction ends, whichever way it ends.
        var claimed = await db.InstanceSecrets
            .Where(secret => secret.InstanceId == instanceId
                && secret.DeliveryJobId == jobId
                && secret.DeliveryConsumedAt == null
                && secret.DeliveryExpiresAt > utcNow
                && secret.ProtectedPendingAdminPassword == null)
            .ExecuteUpdateAsync(
                update => update.SetProperty(secret => secret.DeliveryConsumedAt, (DateTime?)utcNow),
                cancellationToken);
        if (claimed == 0)
        {
            return null;
        }

        var stored = await db.InstanceSecrets.AsNoTracking()
            .Where(secret => secret.InstanceId == instanceId)
            .Select(secret => secret.ProtectedAdminPassword)
            .FirstAsync(cancellationToken);

        return _protector.Unprotect(stored);
    }

    // Stored as UTC; not every database provider says so when it reads a time back.
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string Generate() => RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength);
}
