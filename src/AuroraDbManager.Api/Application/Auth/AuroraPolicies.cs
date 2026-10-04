using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;

namespace AuroraDbManager.Api.Application.Auth;

/// <summary>
/// The authorization policies, and the only place that says which role satisfies which. Endpoints
/// name a policy; nothing else in the application looks at roles.
/// </summary>
public static class AuroraPolicies
{
    /// <summary>Reading: any signed-in user.</summary>
    public const string Viewer = "viewer";

    /// <summary>Operating databases, backups, restores and schedules: operators and administrators.</summary>
    public const string Operator = "operator";

    /// <summary>Instances and users: administrators only.</summary>
    public const string Admin = "admin";

    /// <summary>The claims an access token carries, and nothing else is ever put into one.</summary>
    public const string SubjectClaim = "sub";
    public const string NameClaim = "name";
    public const string RoleClaim = "role";

    /// <summary>A role as it is written in a token, in the API and in the database.</summary>
    public static string RoleName(UserRole role) => EnumStorage.ToDbValue(role);

    public static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy(Viewer, policy => policy.RequireRole(RoleName(UserRole.Admin), RoleName(UserRole.Operator), RoleName(UserRole.Viewer)));
        options.AddPolicy(Operator, policy => policy.RequireRole(RoleName(UserRole.Admin), RoleName(UserRole.Operator)));
        options.AddPolicy(Admin, policy => policy.RequireRole(RoleName(UserRole.Admin)));

        // An endpoint that names no policy is not thereby public: it needs a signed-in user with a
        // known role. Public endpoints say so themselves, with AllowAnonymous.
        options.FallbackPolicy = options.GetPolicy(Viewer);
    }
}
