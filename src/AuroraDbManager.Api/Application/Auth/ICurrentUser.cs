using AuroraDbManager.Api.Domain.Users;

namespace AuroraDbManager.Api.Application.Auth;

/// <summary>
/// Who the current request is made by, for application code that needs to know, without it
/// having to know about HTTP or tokens. Outside a request, in a job or the scheduler, nobody is
/// signed in: background work is the application's own and is done for no user.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>Null when nobody is signed in.</summary>
    Guid? UserId { get; }

    string? Username { get; }

    UserRole? Role { get; }
}
