namespace AuroraDbManager.Api.Application.Backups;

/// <summary>
/// A backup attempt failed. <see cref="Code"/> and <see cref="Exception.Message"/> are stored on
/// the job and the backup and returned by the API, so they must not contain program output,
/// filesystem paths, command lines or credentials. Details belong in the inner exception and the logs.
/// </summary>
public sealed class BackupOperationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
