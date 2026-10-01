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

    /// <summary>Gives bodiless error responses (unknown routes, unhandled exceptions) the standard error body.</summary>
    public static Task WriteStatusCodeBodyAsync(HttpContext context)
    {
        var (code, message) = context.Response.StatusCode switch
        {
            StatusCodes.Status404NotFound => (ErrorCodes.NotFound, "The requested resource was not found."),
            StatusCodes.Status405MethodNotAllowed => (ErrorCodes.MethodNotAllowed, "The HTTP method is not allowed for this resource."),
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
