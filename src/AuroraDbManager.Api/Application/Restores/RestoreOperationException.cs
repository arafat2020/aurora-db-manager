namespace AuroraDbManager.Api.Application.Restores;

/// <summary>
/// A restore attempt failed. <see cref="Code"/> and <see cref="Exception.Message"/> are stored on
/// the job and returned by the API, so they must not contain program output, SQL, filesystem
/// paths, object keys, command lines or credentials. Details belong in the inner exception and the logs.
/// </summary>
public sealed class RestoreOperationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
