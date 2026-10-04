using System.ComponentModel.DataAnnotations;
using AuroraDbManager.Api.Application.Auth;
using AuroraUser = AuroraDbManager.Api.Domain.Users.User;
using AuroraDbManager.Api.Infrastructure.Security;
using AuroraDbManager.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace AuroraDbManager.Web.Pages;

/// <summary>
/// Signing in to the UI. The credentials are checked where the API's are, by
/// <see cref="AuthService"/>; what differs is what a successful check leads to: here a cookie,
/// there a token. No token is ever given to the browser.
/// </summary>
[AllowAnonymous]
// The same limit, and the same bucket, as signing in through the API: an attempt is an attempt.
[EnableRateLimiting(SecurityHardening.LoginRateLimitPolicy)]
public sealed class LoginModel(AuthService auth) : PageModel
{
    [BindProperty]
    public Credentials Input { get; set; } = new();

    /// <summary>Where to go once signed in; only ever a path of this application.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>The attempt failed. Why is not said: a wrong password, an unknown name and a disabled user read the same.</summary>
    public bool Failed { get; private set; }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? LocalRedirect(Destination()) : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await auth.AuthenticateAsync(Input.Username!, Input.Password!, cancellationToken);
        if (user is null)
        {
            Failed = true;
            // Nothing of what was typed is kept for the next rendering.
            ModelState.Clear();
            Input = new Credentials();
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Page();
        }

        await HttpContext.SignInAsync(WebAuthentication.CookieScheme, WebAuthentication.PrincipalFor(user));
        return LocalRedirect(Destination());
    }

    // Never somewhere else: a link to the sign-in page must not be able to send the user on to another site.
    private string Destination() =>
        !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) && !ReturnUrl.StartsWith(WebAuthentication.LoginPath, StringComparison.OrdinalIgnoreCase)
            ? ReturnUrl
            : "/";

    public sealed class Credentials
    {
        [Required(ErrorMessage = "Enter your username.")]
        [MaxLength(AuroraUser.UsernameMaxLength, ErrorMessage = "That is too long to be a username.")]
        public string? Username { get; set; }

        [Required(ErrorMessage = "Enter your password.")]
        [MaxLength(AuroraUser.PasswordMaxLength, ErrorMessage = "That is too long to be a password.")]
        [DataType(DataType.Password)]
        public string? Password { get; set; }
    }
}
