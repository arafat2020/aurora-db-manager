using AuroraDbManager.Api.Application.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuroraDbManager.Api.Infrastructure.Auth;

/// <summary>
/// Tells ASP.NET Core's JWT bearer authentication how an access token is validated: signed with
/// the configured key and HMAC-SHA256 only, issued by and for this application, and not expired.
/// The validation itself is the framework's.
/// </summary>
public sealed class JwtBearerSetup(IOptions<AuthOptions> auth, TimeProvider timeProvider) : IConfigureNamedOptions<JwtBearerOptions>
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    public void Configure(JwtBearerOptions options) => Configure(JwtBearerDefaults.AuthenticationScheme, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
        {
            return;
        }

        var jwt = auth.Value.Jwt;

        // Claims keep the names they have in the token: "sub", "name", "role".
        options.MapInboundClaims = false;
        // Why a token was refused is nobody's business but the log's: the challenge says "Bearer" and no more.
        options.IncludeErrorDetails = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = TokenService.SigningKey(jwt),
            // The algorithm tokens are issued with and no other: a token cannot choose a weaker one, or none.
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            // Against the application's clock, the one tokens are issued by.
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;
                return expires is { } end && end + ClockSkew > now && (notBefore is not { } start || start - ClockSkew <= now);
            },
            NameClaimType = AuroraPolicies.NameClaim,
            RoleClaimType = AuroraPolicies.RoleClaim
        };
    }
}
