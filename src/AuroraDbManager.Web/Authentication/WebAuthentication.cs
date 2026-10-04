using System.Security.Claims;
using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Infrastructure.Persistence;
using AuroraDbManager.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Web.Authentication;

/// <summary>
/// How the UI host knows who is asking. There are two ways in, for two kinds of client, and a
/// request only ever gets one of them:
/// <list type="bullet">
/// <item>the REST API (<c>/api</c>, <c>/health</c>, <c>/openapi</c>) takes a bearer token and nothing else;</item>
/// <item>every page takes the sign-in cookie and nothing else.</item>
/// </list>
/// The cookie is never accepted by the API. A browser sends cookies by itself, also when another
/// site makes it send the request, and API requests carry no antiforgery token; a bearer token
/// has to be attached on purpose. Both ways identify the same users with the same roles.
/// </summary>
public static class WebAuthentication
{
    /// <summary>The scheme that picks one of the two for each request.</summary>
    public const string Scheme = "Aurora";

    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public const string LoginPath = "/login";

    /// <summary>The paths that are the API's, and so the bearer token's.</summary>
    public static bool IsApiRequest(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/api")
        || context.Request.Path.StartsWithSegments("/health")
        || context.Request.Path.StartsWithSegments("/openapi");

    public static AuthenticationBuilder AddAuroraCookie(this AuthenticationBuilder authentication)
    {
        authentication.Services.AddScoped<SignedInUserValidator>();
        authentication.Services.AddOptions<CookieAuthenticationOptions>(CookieScheme)
            .Configure<IOptions<WebOptions>, IOptions<SecurityOptions>, IHostEnvironment>(Configure);

        return authentication
            .AddPolicyScheme(Scheme, Scheme, options =>
                options.ForwardDefaultSelector = context => IsApiRequest(context) ? JwtBearerDefaults.AuthenticationScheme : CookieScheme)
            .AddCookie(CookieScheme);
    }

    private static void Configure(CookieAuthenticationOptions cookie, IOptions<WebOptions> web, IOptions<SecurityOptions> security, IHostEnvironment environment)
    {
        // Sent over HTTPS only, wherever HTTPS is what the installation runs on: everywhere but
        // development and an installation that has deliberately turned HTTPS off.
        var httpsOnly = security.Value.HttpsRedirection && !environment.IsDevelopment();

        // "__Host-": the browser itself then refuses the cookie unless it is Secure, for this host only, and for every path.
        cookie.Cookie.Name = httpsOnly ? "__Host-aurora.session" : "aurora.session";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SecurePolicy = httpsOnly ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        // Not sent with requests other sites make, except when the user follows a link here.
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.Cookie.IsEssential = true;
        cookie.Cookie.Path = "/";

        // A fixed lifetime: a session ends when it was going to end, however busy it was.
        cookie.ExpireTimeSpan = TimeSpan.FromHours(web.Value.SessionHours);
        cookie.SlidingExpiration = false;

        cookie.LoginPath = LoginPath;
        cookie.LogoutPath = "/logout";
        cookie.ReturnUrlParameter = "returnUrl";
        cookie.EventsType = typeof(SignedInUserValidator);
    }

    /// <summary>
    /// What the cookie says about a user: who they are, by id and name, and their role. Nothing
    /// else, and nothing secret; the same three claims an access token carries.
    /// </summary>
    public static ClaimsPrincipal PrincipalFor(User user) => new(new ClaimsIdentity(
        [
            new Claim(AuroraPolicies.SubjectClaim, user.Id.ToString("D")),
            new Claim(AuroraPolicies.NameClaim, user.Username),
            new Claim(AuroraPolicies.RoleClaim, AuroraPolicies.RoleName(user.Role))
        ],
        CookieScheme,
        AuroraPolicies.NameClaim,
        AuroraPolicies.RoleClaim));
}

/// <summary>
/// Checks, on every page request, that the signed-in user is still a user: not deleted, not
/// disabled, and with the role the cookie says. A disabled user is signed out at their next
/// request, and a changed role applies from the next request, not from the next sign-in.
/// </summary>
/// <remarks>
/// One lookup by primary key per page. The UI can afford that; it is why its sessions may last
/// hours, where the API's stateless tokens last minutes.
/// </remarks>
public sealed class SignedInUserValidator(AppDbContext db) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        User? user;
        try
        {
            user = Guid.TryParse(principal?.FindFirstValue(AuroraPolicies.SubjectClaim), out var id)
                ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, context.HttpContext.RequestAborted)
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The system database cannot be asked. The cookie is left as it is, so that the user is
            // shown an error page rather than a sign-in form that could not work either; no page
            // can do anything without the database.
            return;
        }

        if (user is not { Enabled: true })
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(WebAuthentication.CookieScheme);
            return;
        }

        if (principal!.FindFirstValue(AuroraPolicies.RoleClaim) != AuroraPolicies.RoleName(user.Role))
        {
            // The role is the database's, not the cookie's. The session's end stays where it was.
            context.ReplacePrincipal(WebAuthentication.PrincipalFor(user));
            context.ShouldRenew = true;
        }
    }

    // A page the user may not see is answered with 403 and the error page, at the address they
    // asked for, rather than with a redirect to somewhere else.
    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
