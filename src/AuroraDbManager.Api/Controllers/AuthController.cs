using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Errors;
using AuroraDbManager.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AuroraDbManager.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    /// <summary>Signs a user in and returns an access token.</summary>
    /// <remarks>
    /// Send the token with every other request as <c>Authorization: Bearer {accessToken}</c>. It
    /// is valid until <c>expiresAt</c>; sign in again for another. There is no way to register:
    /// users are created by an administrator. A wrong password, an unknown username and a
    /// disabled user are all answered with the same <c>401 INVALID_CREDENTIALS</c>. Attempts are
    /// limited per client address; beyond the limit the answer is <c>429 TOO_MANY_REQUESTS</c>
    /// with a <c>Retry-After</c> header, whether or not the credentials were right.
    /// </remarks>
    [AllowAnonymous]
    [EnableRateLimiting(SecurityHardening.LoginRateLimitPolicy)]
    [HttpPost("login")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var token = await auth.LoginAsync(request, cancellationToken);
        if (token is null)
        {
            return Unauthorized(ApiErrorResponse.Create(ErrorCodes.InvalidCredentials, "Invalid username or password."));
        }

        // A credential: not to be kept by anything between the server and the client.
        Response.Headers.CacheControl = "no-store";
        return Ok(token);
    }
}
