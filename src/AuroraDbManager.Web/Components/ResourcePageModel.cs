using AuroraDbManager.Web.Pages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuroraDbManager.Web.Components;

/// <summary>
/// What the pages about instances and databases have in common: how they say that something is
/// not there, that the application refused, that a form was not valid, and that a change was made.
/// </summary>
public abstract class ResourcePageModel : PageModel
{
    /// <summary>Why the application did not do what the form asked; null if it did, or was not asked.</summary>
    public Alert? Problem { get; private set; }

    /// <summary>404, on the error page, which says what was not found.</summary>
    protected IActionResult Missing(string what)
    {
        HttpContext.Items[ErrorModel.MissingResourceKey] = what;
        return NotFound();
    }

    /// <summary>The page again, with the application's refusal on it and the status of that refusal.</summary>
    protected PageResult Rejected(Rejection rejection)
    {
        Problem = new Alert(StatusTone.Warning, rejection.Message, rejection.Code);
        Response.StatusCode = rejection.Status;
        return Page();
    }

    /// <summary>The form again, with its errors at the fields.</summary>
    protected PageResult Invalid()
    {
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Page();
    }

    /// <summary>A message for the page this request redirects to.</summary>
    protected void Announce(StatusTone tone, string message) => Flash.Set(TempData, tone, message);
}
