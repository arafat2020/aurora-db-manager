namespace AuroraDbManager.Api.Application.Jobs;

/// <summary>
/// A failed job attempt. <see cref="Code"/> and <see cref="Exception.Message"/> are stored on the
/// job and returned by the API, so they must not contain internal details.
/// </summary>
public sealed class JobExecutionException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
