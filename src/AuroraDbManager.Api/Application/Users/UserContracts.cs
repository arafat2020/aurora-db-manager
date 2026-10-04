using System.ComponentModel.DataAnnotations;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Api.Application.Users;

public sealed class CreateUserRequest
{
    /// <summary>
    /// The name the user signs in with: 3 to 64 letters, digits, dots, dashes and underscores.
    /// Unique whatever its case. It cannot be changed later.
    /// </summary>
    [Required(ErrorMessage = "username is required.")]
    [RegularExpression(
        "^[A-Za-z0-9._-]{3,64}$",
        ErrorMessage = "username must be 3 to 64 letters, digits, dots, dashes or underscores.")]
    public string? Username { get; init; }

    /// <summary>The user's password, 12 to 128 characters. Only a hash of it is kept.</summary>
    [Required(ErrorMessage = "password is required.")]
    [StringLength(User.PasswordMaxLength, MinimumLength = User.PasswordMinLength, ErrorMessage = "password must be {2} to {1} characters long.")]
    public string? Password { get; init; }

    /// <summary>What the user may do: <c>admin</c>, <c>operator</c> or <c>viewer</c>.</summary>
    // Bound as a string so an unknown role yields a normal validation error. Keep in sync with UserRole.
    [Required(ErrorMessage = "role is required.")]
    [AllowedValues("admin", "operator", "viewer", ErrorMessage = "role must be one of: admin, operator, viewer.")]
    public string? Role { get; init; }

    /// <summary>Whether the user may sign in. True when left out.</summary>
    public bool? Enabled { get; init; }
}

public sealed class UpdateUserRequest
{
    /// <summary>What the user may do: <c>admin</c>, <c>operator</c> or <c>viewer</c>.</summary>
    [Required(ErrorMessage = "role is required.")]
    [AllowedValues("admin", "operator", "viewer", ErrorMessage = "role must be one of: admin, operator, viewer.")]
    public string? Role { get; init; }

    /// <summary>Whether the user may sign in.</summary>
    [Required(ErrorMessage = "enabled is required.")]
    public bool? Enabled { get; init; }

    /// <summary>A new password, 12 to 128 characters. Left out, the password stays as it is.</summary>
    [StringLength(User.PasswordMaxLength, MinimumLength = User.PasswordMinLength, ErrorMessage = "password must be {2} to {1} characters long.")]
    public string? Password { get; init; }
}

public sealed class ListUsersQuery
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>1-based page number. Defaults to 1.</summary>
    [FromQuery(Name = "page")]
    [Range(1, int.MaxValue, ErrorMessage = "page must be greater than zero.")]
    public int Page { get; init; } = 1;

    /// <summary>Number of items per page, between 1 and 100. Defaults to 20.</summary>
    [FromQuery(Name = "pageSize")]
    [Range(1, MaxPageSize, ErrorMessage = "pageSize must be between {1} and {2}.")]
    public int PageSize { get; init; } = DefaultPageSize;
}

/// <param name="Id">Unique identifier of the user.</param>
/// <param name="Username">The name the user signs in with.</param>
/// <param name="Role">What the user may do: <c>admin</c>, <c>operator</c> or <c>viewer</c>.</param>
/// <param name="Enabled">Whether the user may sign in.</param>
/// <param name="CreatedAt">UTC time the user was created.</param>
/// <param name="UpdatedAt">UTC time the user last changed.</param>
// The password hash is deliberately absent: it never leaves the server.
public sealed record UserResponse(Guid Id, string Username, UserRole Role, bool Enabled, DateTime CreatedAt, DateTime UpdatedAt)
{
    public static UserResponse From(User user) => new(
        user.Id, user.Username, user.Role, user.Enabled, Utc(user.CreatedAt), Utc(user.UpdatedAt));

    // The stored instants are UTC whatever kind the database provider hands back.
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <param name="Items">Users on the requested page, newest first.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Requested number of items per page.</param>
/// <param name="TotalCount">Total number of users across all pages.</param>
public sealed record UserListResponse(IReadOnlyList<UserResponse> Items, int Page, int PageSize, int TotalCount);

/// <param name="Status">Whether the operation succeeded, and if not, why.</param>
/// <param name="User">The user as it now is; set only when <paramref name="Status"/> is <see cref="UserStatus.Ok"/> and the user still exists.</param>
public sealed record UserResult(UserStatus Status, UserResponse? User = null);

public enum UserStatus
{
    Ok,
    NotFound,

    /// <summary>Not created because a user of that name, in whatever case, exists.</summary>
    UsernameAlreadyExists,

    /// <summary>Not done because it would leave the installation without an administrator who can sign in.</summary>
    LastAdministrator
}

internal static class UserRoles
{
    public static UserRole Parse(string role) => EnumStorage.FromDbValue<UserRole>(role);
}
