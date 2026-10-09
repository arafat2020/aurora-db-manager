using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Application.Credentials;

/// <summary>
/// Changes and checks the password of an instance's database administrator, the account
/// <see cref="EngineDefaults.AdminUser"/> names, in the database server itself and over the
/// engine's own protocol. There is one implementation per <see cref="InstanceEngine"/>.
/// </summary>
/// <remarks>
/// Passwords are passed in and never come out: not in a return value, an exception or a log.
/// Each call performs one attempt; the job running it retries.
/// </remarks>
public interface IAdminCredentialManager
{
    InstanceEngine Engine { get; }

    /// <summary>
    /// Whether the server accepts <paramref name="password"/> for its administrator, found out
    /// with a connection of its own that is opened, used once and closed.
    /// </summary>
    /// <returns>True if the server let the administrator in; false if it refused the password.</returns>
    /// <exception cref="DatabaseOperationException">
    /// Neither is known: the server could not be located, did not answer, or failed otherwise.
    /// </exception>
    Task<bool> AuthenticatesAsync(Instance instance, string password, CancellationToken cancellationToken);

    /// <summary>
    /// Connects with <paramref name="currentPassword"/> and makes <paramref name="newPassword"/>
    /// the administrator's password. Sessions that are open stay open; new ones need the new password.
    /// </summary>
    /// <exception cref="DatabaseOperationException">The server was not reached or did not make the change.</exception>
    Task ChangeAdminPasswordAsync(Instance instance, string currentPassword, string newPassword, CancellationToken cancellationToken);
}
