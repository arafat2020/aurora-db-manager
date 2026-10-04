using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AuroraDbManager.Api.Errors;

public static class ErrorHandling
{
    /// <summary>Turns MVC model-state failures into the API's validation error response.</summary>
    public static IActionResult ValidationFailed(ActionContext context)
    {
        // When the body cannot be read, MVC also reports the body parameter itself as
        // missing ("request"), which means nothing to an API client.
        var bodyParameters = context.ActionDescriptor.Parameters
            .Where(parameter => parameter.BindingInfo?.BindingSource == BindingSource.Body)
            .Select(parameter => parameter.Name)
            .ToHashSet();

        var details = context.ModelState
            .Where(entry => entry.Value is { Errors.Count: > 0 } && !bodyParameters.Contains(entry.Key))
            .GroupBy(entry => FieldName(entry.Key))
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(entry => Messages(entry.Key, entry.Value!)).Distinct().ToArray());

        return new BadRequestObjectResult(ApiErrorResponse.Create(
            ErrorCodes.ValidationFailed,
            "The request is invalid.",
            details));
    }

    /// <summary>
    /// Answers an unhandled exception. A request the server itself refused while it was being
    /// read, a body that grew past the size limit for instance, keeps the status the server gave
    /// it; that is the client's error, not the application's. Everything else is a 500. Either
    /// way the body is the standard one and says nothing of the exception.
    /// </summary>
    public static Task WriteExceptionBodyAsync(HttpContext context)
    {
        if (RefusedStatus(context) is { } status)
        {
            context.Response.StatusCode = status;
        }

        return WriteStatusCodeBodyAsync(context);
    }

    /// <summary>
    /// The status of a request that failed because the server refused it while reading it; null
    /// if the request did not fail that way.
    /// </summary>
    public static int? RefusedStatus(HttpContext context)
    {
        for (var error = context.Features.Get<IExceptionHandlerFeature>()?.Error; error is not null; error = error.InnerException)
        {
            if (error is BadHttpRequestException { StatusCode: >= 400 and < 500 } refused)
            {
                return refused.StatusCode;
            }
        }

        return null;
    }

    /// <summary>Gives bodiless error responses (unknown routes, unhandled exceptions) the standard error body.</summary>
    public static Task WriteStatusCodeBodyAsync(HttpContext context)
    {
        var (code, message) = context.Response.StatusCode switch
        {
            // Why a token was not accepted is not said: missing, malformed, expired and forged all read the same.
            StatusCodes.Status401Unauthorized => (ErrorCodes.Unauthorized, "Authentication is required."),
            StatusCodes.Status403Forbidden => (ErrorCodes.Forbidden, "You are not allowed to do this."),
            StatusCodes.Status404NotFound => (ErrorCodes.NotFound, "The requested resource was not found."),
            StatusCodes.Status405MethodNotAllowed => (ErrorCodes.MethodNotAllowed, "The HTTP method is not allowed for this resource."),
            StatusCodes.Status413PayloadTooLarge => (ErrorCodes.RequestTooLarge, "The request body is too large."),
            StatusCodes.Status429TooManyRequests => (ErrorCodes.TooManyRequests, "Too many attempts. Try again later."),
            StatusCodes.Status415UnsupportedMediaType => (ErrorCodes.UnsupportedMediaType, "The request content type is not supported."),
            >= StatusCodes.Status500InternalServerError => (ErrorCodes.InternalError, "An unexpected error occurred."),
            _ => (ErrorCodes.RequestFailed, "The request could not be processed.")
        };

        return context.Response.WriteAsJsonAsync(ApiErrorResponse.Create(code, message));
    }

    // JSON deserialization failures are keyed by JSON path ("$", "$.cpu"); a missing
    // body is keyed by the empty string.
    private static string FieldName(string key) =>
        key is "" or "$" ? "body" : key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key;

    private static IEnumerable<string> Messages(string key, ModelStateEntry entry)
    {
        // Deserializer messages mention CLR type names, so they are replaced.
        if (key.StartsWith('$'))
        {
            return [key == "$" ? "The request body is not valid JSON." : "The value is not valid."];
        }

        return entry.Errors.Select(error =>
            string.IsNullOrEmpty(error.ErrorMessage) ? "The value is not valid." : error.ErrorMessage);
    }
}
