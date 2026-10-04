using System.Security.Claims;
using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Domain.Users;

namespace AuroraDbManager.Web.Components;

/// <summary>
/// What the UI shows and hides for the signed-in user, read from the claims of the request: no
/// lookup, no extra request. It decides what is offered, never what is allowed; every page and
/// every operation is authorized by the same policies as the API, whatever the UI showed.
/// </summary>
public static class SignedInUser
{
    public static string Name(this ClaimsPrincipal user) => user.FindFirstValue(AuroraPolicies.NameClaim) ?? string.Empty;

    /// <summary>The role as it is written everywhere else: <c>admin</c>, <c>operator</c> or <c>viewer</c>.</summary>
    public static string RoleName(this ClaimsPrincipal user) => user.FindFirstValue(AuroraPolicies.RoleClaim) ?? string.Empty;

    public static bool IsSignedIn(this ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true;

    /// <summary>May manage instances and users.</summary>
    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(AuroraPolicies.RoleName(UserRole.Admin));

    /// <summary>May operate databases, backups, restores and schedules.</summary>
    public static bool IsOperator(this ClaimsPrincipal user) => user.IsAdmin() || user.IsInRole(AuroraPolicies.RoleName(UserRole.Operator));
}
