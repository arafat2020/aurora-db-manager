using System.ComponentModel.DataAnnotations;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Application.Auth;

/// <summary>Signs users in: checks a username and password and, if they are right, issues an access token.</summary>
public sealed class AuthService(
    AppDbContext db,
    PasswordHashing passwords,
    TokenService tokens,
    TimeProvider timeProvider,
    ILogger<AuthService> logger)
{
    /// <returns>
    /// The token, or null if the user cannot be signed in. Why not, an unknown name, a wrong
    /// password or a disabled account, is deliberately not told apart for the caller: the answer
    /// must not reveal which usernames exist.
    /// </returns>
    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var normalized = User.Normalize(request.Username!.Trim());
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, cancellationToken);

        if (user is null)
        {
            // The same work as for a user who exists, so the time taken says nothing either.
            passwords.VerifyAgainstNoUser(request.Password!);
            // The name that was tried is not logged: what gets typed into that field by mistake is, often enough, a password.
            logger.LogWarning("Login failed: {Reason}", "unknown_user");
            return null;
        }

        var verification = passwords.Verify(user.PasswordHash, request.Password!);
        if (!verification.Succeeded)
        {
            logger.LogWarning("Login failed for user {UserId}: {Reason}", user.Id, "wrong_password");
            return null;
        }

        // Checked after the password, so only someone who knows it could tell a disabled account from a wrong password, and even they are not told.
        if (!user.Enabled)
        {
            logger.LogWarning("Login failed for user {UserId}: {Reason}", user.Id, "user_disabled");
            return null;
        }

        if (verification.NeedsRehash)
        {
            user.ChangePasswordHash(passwords.Hash(request.Password!), timeProvider.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(cancellationToken);
        }

        var token = tokens.Issue(user);
        logger.LogInformation("User {UserId} signed in with role {Role}", user.Id, user.Role);
        return new LoginResponse(token.Value, "Bearer", token.ExpiresAt);
    }
}

public sealed class LoginRequest
{
    /// <summary>The user's name. Case does not matter.</summary>
    [Required(ErrorMessage = "username is required.")]
    [MaxLength(User.UsernameMaxLength, ErrorMessage = "username must be at most {1} characters.")]
    public string? Username { get; init; }

    /// <summary>The user's password.</summary>
    [Required(ErrorMessage = "password is required.")]
    [MaxLength(User.PasswordMaxLength, ErrorMessage = "password must be at most {1} characters.")]
    public string? Password { get; init; }
}

/// <param name="AccessToken">The token to send with every request, as <c>Authorization: Bearer {token}</c>.</param>
/// <param name="TokenType">Always <c>Bearer</c>.</param>
/// <param name="ExpiresAt">UTC time the token stops being accepted; sign in again to get another.</param>
public sealed record LoginResponse(string AccessToken, string TokenType, DateTime ExpiresAt);
