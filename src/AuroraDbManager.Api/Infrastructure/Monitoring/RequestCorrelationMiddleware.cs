using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Monitoring;

namespace AuroraDbManager.Api.Infrastructure.Monitoring;

/// <summary>
/// Gives every request an id: the client's <c>X-Request-Id</c> if it sent a usable one, a new one
/// otherwise. The id is returned in the same header, is the request's trace identifier, and is in
/// the scope of everything logged while the request is handled. Nothing is stored per request.
/// </summary>
public sealed class RequestCorrelationMiddleware(RequestDelegate next, TimeProvider timeProvider, ILogger<RequestCorrelationMiddleware> logger)
{
    public const string HeaderName = "X-Request-Id";

    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = Usable(context.Request.Headers[HeaderName].ToString()) ?? Guid.NewGuid().ToString("N");

        context.TraceIdentifier = requestId;
        RequestCorrelation.Current = requestId;

        // When the response starts, so the header survives an error handler that resets the response.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = requestId;
            return Task.CompletedTask;
        });

        using var scope = logger.BeginScope("Request {" + RequestCorrelation.LogProperty + "}", requestId);
        var started = timeProvider.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            // The path only: a query string is not logged. Probes arrive every few seconds and
            // are kept out of the ordinary log.
            var level = context.Request.Path.StartsWithSegments("/health") ? LogLevel.Debug : LogLevel.Information;
            // Who made the request, by id; "anonymous" when nobody was signed in. Never the token.
            logger.Log(
                level,
                "HTTP {Method} {Path} responded {StatusCode} in {DurationMs} ms for user {UserId}",
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.StatusCode,
                (long)timeProvider.GetElapsedTime(started).TotalMilliseconds,
                context.User.Identity?.IsAuthenticated == true ? context.User.FindFirst(AuroraPolicies.SubjectClaim)?.Value : "anonymous");
        }
    }

    /// <summary>
    /// The client's id if it is short and made of plain characters only, so that it can go into a
    /// header and a log line as it is; null otherwise.
    /// </summary>
    private static string? Usable(string value) =>
        value.Length is > 0 and <= MaxLength && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? value
            : null;
}
