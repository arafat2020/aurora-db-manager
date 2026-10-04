using AuroraDbManager.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuroraDbManager.Web.Pages;

/// <summary>
/// Signing out: a POST, with an antiforgery token like every other change, so that no link and
/// no other site can sign a user out. A GET changes nothing.
/// </summary>
[AllowAnonymous]
public sealed class LogoutModel : PageModel
{
    public IActionResult OnGet() => LocalRedirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(WebAuthentication.CookieScheme);
        return LocalRedirect(WebAuthentication.LoginPath);
    }
}
