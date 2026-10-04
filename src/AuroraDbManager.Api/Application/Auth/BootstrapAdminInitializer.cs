using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Auth;

/// <summary>
/// Creates the first administrator from <c>Authentication:BootstrapAdmin</c> at startup, and only
/// the first: once any administrator exists, the settings are ignored, whatever they say. Nothing
/// is ever overwritten, and without the settings no account is created.
/// </summary>
/// <remarks>
/// Tried once before the application starts serving. If the system database cannot be reached
/// then, the application still starts, as it does for everything else, and this is tried again
/// in the background until it has gone through.
/// </remarks>
public sealed class BootstrapAdminInitializer(
    IServiceScopeFactory scopeFactory,
    IOptions<AuthOptions> options,
    PasswordHashing passwords,
    TimeProvider timeProvider,
    ILogger<BootstrapAdminInitializer> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private bool _done;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _done = await TryAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!_done)
            {
                await Task.Delay(RetryDelay, timeProvider, stoppingToken);
                _done = await TryAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <returns>False if it could not be found out whether an administrator exists, or one could not be created; it is then tried again.</returns>
    private async Task<bool> TryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await EnsureAdministratorAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "It could not be checked whether an administrator exists; trying again in {RetryDelaySeconds}s",
                RetryDelay.TotalSeconds);
            return false;
        }
    }

    private async Task EnsureAdministratorAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(user => user.Role == UserRole.Admin, cancellationToken))
        {
            return;
        }

        var bootstrap = options.Value.BootstrapAdmin;
        if (!bootstrap.IsConfigured)
        {
            logger.LogWarning(
                "No administrator exists and none is configured: nobody can sign in. "
                + "Set Authentication:BootstrapAdmin:Username and Authentication:BootstrapAdmin:Password and restart to create the first one");
            return;
        }

        var normalized = User.Normalize(bootstrap.Username!);
        if (await db.Users.AnyAsync(user => user.NormalizedUsername == normalized, cancellationToken))
        {
            // Never overwritten, and never promoted behind an administrator's back.
            logger.LogError(
                "The bootstrap administrator {Username} was not created: a user of that name exists and is not an administrator",
                bootstrap.Username);
            return;
        }

        var admin = User.Create(
            bootstrap.Username!, passwords.Hash(bootstrap.Password!), UserRole.Admin, enabled: true, timeProvider.GetUtcNow().UtcDateTime);
        db.Users.Add(admin);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another process starting at the same moment created it first.
            logger.LogInformation("The bootstrap administrator {Username} already exists", bootstrap.Username);
            return;
        }

        logger.LogInformation(
            "Bootstrap administrator {Username} created as user {UserId}. The bootstrap settings are no longer used and should be removed",
            admin.Username, admin.Id);
    }
}
