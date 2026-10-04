using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Infrastructure.Persistence;

namespace AuroraDbManager.Api.Infrastructure.Auth;

/// <summary><see cref="ICurrentUser"/> read from the claims of the request's validated access token.</summary>
public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private System.Security.Claims.ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId => Guid.TryParse(Claim(AuroraPolicies.SubjectClaim), out var id) ? id : null;

    public string? Username => Claim(AuroraPolicies.NameClaim);

    public UserRole? Role => Claim(AuroraPolicies.RoleClaim) switch
    {
        null => null,
        var role when Enum.GetValues<UserRole>().Any(known => AuroraPolicies.RoleName(known) == role) => EnumStorage.FromDbValue<UserRole>(role),
        _ => null
    };

    private string? Claim(string type) => IsAuthenticated ? Principal!.FindFirst(type)?.Value : null;
}
