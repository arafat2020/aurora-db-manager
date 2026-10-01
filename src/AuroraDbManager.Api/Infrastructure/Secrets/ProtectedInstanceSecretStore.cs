using System.Security.Cryptography;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Infrastructure.Secrets;

/// <summary>
/// Temporary Phase 3 secret storage: passwords are encrypted with ASP.NET Core Data Protection
/// and kept in the <c>instance_secrets</c> table of the system database.
/// </summary>
/// <remarks>
/// The encryption keys are Data Protection's key ring, which by default is a directory in the
/// home of the user running the API. Whoever has both the database and the key ring can read the
/// passwords, and losing the key ring makes them unrecoverable. The password is also visible in
/// the container's configuration (<c>docker inspect</c>) to anyone with access to Docker.
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

        var password = RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength);

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
}
