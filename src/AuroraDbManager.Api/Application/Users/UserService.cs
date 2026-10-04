using System.Data;
using System.Data.Common;
using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Application.Users;

/// <summary>
/// Manages the users of this installation: who they are, what role they have, whether they may
/// sign in. Who may call this is decided at the endpoint, by policy; nothing here checks roles.
/// </summary>
/// <remarks>
/// <b>The last administrator.</b> An installation without an enabled administrator could not be
/// managed any more, and nobody would be left to repair that. Deleting, disabling or demoting the
/// only enabled administrator is therefore refused. The check and the change are one serializable
/// transaction, so two requests that each remove a different one of the last two administrators
/// cannot both go through; the one the database refuses is tried once more and then refused properly.
/// </remarks>
public sealed class UserService(
    AppDbContext db,
    PasswordHashing passwords,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<UserService> logger)
{
    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<UserResult> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var normalized = User.Normalize(request.Username!);
        if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized, cancellationToken))
        {
            return new UserResult(UserStatus.UsernameAlreadyExists);
        }

        var user = User.Create(
            request.Username!, passwords.Hash(request.Password!), UserRoles.Parse(request.Role!), request.Enabled ?? true, UtcNow);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The check can be overtaken by a concurrent request; the unique index then refuses this one.
            db.ChangeTracker.Clear();
            if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized, cancellationToken))
            {
                return new UserResult(UserStatus.UsernameAlreadyExists);
            }

            throw;
        }

        logger.LogInformation(
            "User {UserId} created with role {Role} by user {ActorId}", user.Id, user.Role, currentUser.UserId);
        return new UserResult(UserStatus.Ok, UserResponse.From(user));
    }

    public async Task<UserResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        return user is null ? null : UserResponse.From(user);
    }

    public async Task<UserListResponse> ListAsync(ListUsersQuery query, CancellationToken cancellationToken)
    {
        var totalCount = await db.Users.CountAsync(cancellationToken);

        var users = await db.Users.AsNoTracking()
            .OrderByDescending(u => u.CreatedAt)
            .ThenByDescending(u => u.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new UserListResponse(users.Select(UserResponse.From).ToList(), query.Page, query.PageSize, totalCount);
    }

    /// <summary>Sets the user's role and whether they may sign in, and their password if one is given.</summary>
    public Task<UserResult> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken cancellationToken) =>
        WithRetryAsync(() => UpdateOnceAsync(id, request, cancellationToken));

    private async Task<UserResult> UpdateOnceAsync(Guid id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            return new UserResult(UserStatus.NotFound);
        }

        var role = UserRoles.Parse(request.Role!);
        var enabled = request.Enabled!.Value;
        var staysEnabledAdministrator = role == UserRole.Admin && enabled;
        if (!staysEnabledAdministrator && await IsLastEnabledAdministratorAsync(user, cancellationToken))
        {
            return new UserResult(UserStatus.LastAdministrator);
        }

        var now = UtcNow;
        var previousRole = user.Role;
        var wasEnabled = user.Enabled;
        user.ChangeRole(role, now);
        user.SetEnabled(enabled, now);
        if (request.Password is not null)
        {
            user.ChangePasswordHash(passwords.Hash(request.Password), now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "User {UserId} updated by user {ActorId}: role {PreviousRole} -> {Role}, enabled {WasEnabled} -> {Enabled}, password changed: {PasswordChanged}",
            user.Id, currentUser.UserId, previousRole, user.Role, wasEnabled, user.Enabled, request.Password is not null);
        return new UserResult(UserStatus.Ok, UserResponse.From(user));
    }

    public Task<UserResult> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        WithRetryAsync(() => DeleteOnceAsync(id, cancellationToken));

    private async Task<UserResult> DeleteOnceAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            return new UserResult(UserStatus.NotFound);
        }

        if (await IsLastEnabledAdministratorAsync(user, cancellationToken))
        {
            return new UserResult(UserStatus.LastAdministrator);
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("User {UserId} deleted by user {ActorId}", user.Id, currentUser.UserId);
        return new UserResult(UserStatus.Ok);
    }

    /// <summary>
    /// Two requests that change administrators at the same moment cannot both commit: the
    /// database lets one through and refuses the other with a serialization failure, which is
    /// what keeps the last administrator safe. The refused one is run once more, from the start;
    /// it then sees what the other did and is answered on that basis, with a result, not a 500.
    /// </summary>
    private async Task<UserResult> WithRetryAsync(Func<Task<UserResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception exception) when (IsSerializationFailure(exception))
        {
            db.ChangeTracker.Clear();
            return await operation();
        }
    }

    // PostgreSQL's serialization_failure and deadlock_detected, wherever in the chain they are.
    private static bool IsSerializationFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: "40001" or "40P01" })
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> IsLastEnabledAdministratorAsync(User user, CancellationToken cancellationToken) =>
        user is { Role: UserRole.Admin, Enabled: true }
        && !await db.Users.AnyAsync(other => other.Id != user.Id && other.Role == UserRole.Admin && other.Enabled, cancellationToken);
}
