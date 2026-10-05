using AuroraDbManager.Api.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuroraDbManager.Web.Pages;

/// <summary>
/// The page shown in place of one that could not be: not allowed, not there, refused, or failed.
/// It says what happened in a sentence and gives the request id, under which the server's log
/// has the rest. It never shows an exception, and is the same in every environment.
/// </summary>
[AllowAnonymous]
// Shown in place of a failed POST as well, the one with a missing antiforgery token included.
[IgnoreAntiforgeryToken]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ErrorModel : PageModel
{
    /// <summary>
    /// Where a page that looked for something and did not find it says what that was ("Instance"),
    /// in <see cref="HttpContext.Items"/>, so that this page can say so too.
    /// </summary>
    public const string MissingResourceKey = "Aurora.MissingResource";

    public int Status { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Message { get; private set; } = string.Empty;

    public string Icon { get; private set; } = "danger";

    /// <summary>The id the request has in the response header and in the log.</summary>
    public string RequestId => HttpContext.TraceIdentifier;

    public int? RetryAfterSeconds { get; private set; }

    public void OnGet(int? code) => Describe(code);

    public void OnPost(int? code) => Describe(code);

    private void Describe(int? code)
    {
        // A request the server refused while reading it keeps the status the server gave it.
        Status = ErrorHandling.RefusedStatus(HttpContext) ?? code ?? Response.StatusCode;
        if (Status is < 400 or > 599)
        {
            // Asked for directly, with no error behind it.
            Status = StatusCodes.Status404NotFound;
        }

        Response.StatusCode = Status;

        (Title, Message, Icon) = Status switch
        {
            StatusCodes.Status400BadRequest => (
                "That request could not be processed",
                "The form may have been open for too long. Go back, reload the page and try again.",
                "warning"),
            StatusCodes.Status403Forbidden => (
                "You don't have permission",
                "Your role does not allow this. If you need access, ask an administrator.",
                "lock"),
            StatusCodes.Status404NotFound => (
                "Page not found",
                "There is nothing at this address. It may have been deleted, or the link may be wrong.",
                "search"),
            StatusCodes.Status405MethodNotAllowed => (
                "That is not possible here",
                "This address does not accept that kind of request.",
                "warning"),
            StatusCodes.Status409Conflict => (
                "That could not be done right now",
                "It conflicts with the current state of what you were changing. Reload and try again.",
                "warning"),
            StatusCodes.Status413PayloadTooLarge => (
                "That request is too large",
                "What was sent is larger than Aurora accepts.",
                "warning"),
            StatusCodes.Status429TooManyRequests => (
                "Too many attempts",
                "There have been too many sign-in attempts from your address.",
                "warning"),
            >= 500 => (
                "Something went wrong",
                "The server could not complete the request. If it keeps happening, give the request ID below to an administrator.",
                "danger"),
            _ => (
                "That request could not be processed",
                "The server could not accept the request as it was sent.",
                "warning")
        };

        if (Status == StatusCodes.Status404NotFound && HttpContext.Items[MissingResourceKey] is string missing)
        {
            (Title, Message) = ($"{missing} not found", "It may have been deleted, or the link may be wrong.");
        }

        if (Status == StatusCodes.Status429TooManyRequests
            && int.TryParse(Response.Headers.RetryAfter.ToString(), out var seconds)
            && seconds > 0)
        {
            RetryAfterSeconds = seconds;
        }
    }
}
