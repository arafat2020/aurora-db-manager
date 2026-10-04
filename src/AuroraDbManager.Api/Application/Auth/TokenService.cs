using System.Text;
using AuroraDbManager.Api.Domain.Users;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuroraDbManager.Api.Application.Auth;

/// <summary>
/// Issues access tokens: JWTs signed with HMAC-SHA256 and the configured key. A token says who
/// the user is and which role they have, and nothing else: no password, no credential of any
/// kind, nothing about the user beyond id, name and role.
/// </summary>
public sealed class TokenService(IOptions<AuthOptions> options, TimeProvider timeProvider)
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Issue(User user)
    {
        var jwt = options.Value.Jwt;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now.AddMinutes(jwt.AccessTokenLifetimeMinutes);

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expiresAt,
            Claims = new Dictionary<string, object>
            {
                [AuroraPolicies.SubjectClaim] = user.Id.ToString("D"),
                [AuroraPolicies.NameClaim] = user.Username,
                [AuroraPolicies.RoleClaim] = AuroraPolicies.RoleName(user.Role)
            },
            SigningCredentials = new SigningCredentials(SigningKey(jwt), SecurityAlgorithms.HmacSha256)
        });

        return new AccessToken(token, expiresAt);
    }

    public static SymmetricSecurityKey SigningKey(JwtOptions jwt) => new(Encoding.UTF8.GetBytes(jwt.SigningKey));
}

/// <param name="Value">The signed token. A credential: never logged.</param>
/// <param name="ExpiresAt">UTC time the token stops being accepted.</param>
public sealed record AccessToken(string Value, DateTime ExpiresAt);
