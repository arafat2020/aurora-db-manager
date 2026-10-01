using System.Text.Json.Serialization;

namespace AuroraDbManager.Api.Errors;

/// <summary>Envelope returned by every failed request.</summary>
public sealed record ApiErrorResponse(ApiError Error)
{
    public static ApiErrorResponse Create(
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? details = null) =>
        new(new ApiError(code, message, details));
}

/// <param name="Code">Stable, machine-readable error code, e.g. <c>INSTANCE_NOT_FOUND</c>.</param>
/// <param name="Message">Human-readable description of the error.</param>
/// <param name="Details">Validation messages keyed by request field. Only present on validation errors.</param>
public sealed record ApiError(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string[]>? Details = null);
