namespace AuroraDbManager.Api.Application.Databases;

/// <summary>
/// An engine-side database operation failed. <see cref="Code"/> and <see cref="Exception.Message"/>
/// are stored on the job and returned by the API, so they must not contain driver messages, SQL,
/// connection strings or credentials. The driver's exception is kept as the inner exception, for logs.
/// </summary>
public sealed class DatabaseOperationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
