using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Backups;

/// <summary>
/// What the PostgreSQL and MySQL backup managers have in common: run the engine's own dump
/// program against the instance's server, have it write into a staging file of the backup
/// storage, and finalize that file once the program has succeeded and its output looks complete.
/// Everything engine-specific is left to the subclass.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it runs.</b> The dump program runs on the machine of the API, as a child process, and
/// connects to the instance's server at the endpoint given by <see cref="IInstanceEndpointResolver"/>,
/// the same way the database managers do. The program must be installed there and must be at
/// least as new as the instance's engine.
/// </para>
/// <para>
/// <b>Password.</b> The administrator password comes from <see cref="IInstanceSecretStore"/> and
/// reaches the program through a <see cref="TemporaryCredentialFile"/> that exists only while the
/// program runs. It is never an argument, never an environment variable, and is removed from the
/// program's diagnostics before they are logged.
/// </para>
/// <para>
/// <b>Repeating an attempt.</b> The artifact's place is determined by the backup's id. If a
/// finished artifact is already there, an earlier attempt completed it and was interrupted before
/// that could be recorded; it is adopted as it is, since an artifact only ever appears there
/// complete. An unfinished staging file is not a backup and is discarded and written again.
/// </para>
/// </remarks>
public abstract class DumpBackupManager(
    IBackupStorage storage,
    IInstanceEndpointResolver endpoints,
    IInstanceSecretStore secrets,
    IProcessRunner processes,
    IOptions<BackupOptions> options,
    ILogger logger) : IBackupManager
{
    private const int MaxLoggedDiagnostics = 4000;

    public abstract InstanceEngine Engine { get; }

    /// <summary>File extension of the artifact, without the dot.</summary>
    protected abstract string Extension { get; }

    /// <summary>Name of the dump program, for logs and messages.</summary>
    protected abstract string Tool { get; }

    /// <summary>The content of the file that gives the program the administrator's password.</summary>
    protected abstract string CredentialFileContent(InstanceEndpoint endpoint, Database database, string adminPassword);

    /// <summary>The program, its arguments and its environment. Must not contain the password.</summary>
    protected abstract ProcessRequest BuildRequest(
        InstanceEndpoint endpoint, Database database, string outputPath, string credentialFilePath, BackupOptions options);

    /// <summary>Whether the file the program wrote is a dump that was written to its end.</summary>
    protected abstract Task<bool> IsCompleteDumpAsync(string path, CancellationToken cancellationToken);

    /// <summary>Tells what a failed run's diagnostics mean; null for a failure of no particular kind.</summary>
    protected abstract BackupOperationException? ClassifyFailure(string diagnostics);

    public async Task<BackupArtifact> BackupAsync(Instance instance, Database database, Backup backup, CancellationToken cancellationToken)
    {
        var location = new BackupLocation(instance.Id, database.Id, backup.Id, Extension);

        var finished = await storage.FindAsync(location, cancellationToken);
        if (finished is not null)
        {
            logger.LogInformation(
                "Backup {BackupId} already has a finished artifact of {SizeBytes} bytes; adopted",
                backup.Id, finished.SizeBytes);
            return finished;
        }

        InstanceEndpoint endpoint;
        try
        {
            endpoint = await endpoints.ResolveAsync(instance, cancellationToken);
        }
        catch (DatabaseOperationException exception)
        {
            throw new BackupOperationException(BackupErrorCodes.BackupDatabaseUnavailable, exception.Message, exception);
        }

        var password = await secrets.GetOrCreateAdminPasswordAsync(instance.Id, cancellationToken);

        // Both are removed when this method is left, however it is left: the credential file in
        // every case, the staging file unless it was committed.
        await using var staging = await storage.BeginAsync(location, cancellationToken);
        await using var credentials = await TemporaryCredentialFile.CreateAsync(
            CredentialFileContent(endpoint, database, password), cancellationToken);

        var request = BuildRequest(endpoint, database, staging.FilePath, credentials.Path, options.Value);

        logger.LogInformation("Starting {Tool} for backup {BackupId} of database {DatabaseId}", Tool, backup.Id, database.Id);

        ProcessResult result;
        try
        {
            result = await processes.RunAsync(request, cancellationToken);
        }
        catch (ProcessStartException exception)
        {
            throw new BackupOperationException(
                BackupErrorCodes.BackupToolUnavailable,
                $"The backup program {Tool} is not available on the server.",
                exception);
        }

        if (result.TimedOut)
        {
            logger.LogWarning(
                "{Tool} for backup {BackupId} was stopped after {TimeoutSeconds}s: {Diagnostics}",
                Tool, backup.Id, request.Timeout.TotalSeconds, Diagnostics(result, password));
            throw new BackupOperationException(
                BackupErrorCodes.BackupOperationTimeout, "The backup did not finish in time.");
        }

        if (result.ExitCode != 0)
        {
            // The program's own words stay in the log; the client gets a code and a fixed message.
            logger.LogWarning(
                "{Tool} for backup {BackupId} exited with code {ExitCode}: {Diagnostics}",
                Tool, backup.Id, result.ExitCode, Diagnostics(result, password));
            throw ClassifyFailure(result.StandardError)
                ?? new BackupOperationException(BackupErrorCodes.BackupProcessFailed, $"The backup program {Tool} failed.");
        }

        // Success as reported by the program is not enough to call the file a backup.
        if (!await IsCompleteDumpAsync(staging.FilePath, cancellationToken))
        {
            logger.LogWarning("{Tool} for backup {BackupId} reported success but did not write a complete dump", Tool, backup.Id);
            throw new BackupOperationException(
                BackupErrorCodes.BackupArtifactInvalid, "The backup program did not produce a complete backup.");
        }

        var artifact = await staging.CommitAsync(cancellationToken);
        logger.LogInformation("Backup {BackupId} finished: {SizeBytes} bytes", backup.Id, artifact.SizeBytes);
        return artifact;
    }

    /// <summary>The program's diagnostics, fit for a log: without the password, and bounded.</summary>
    private static string Diagnostics(ProcessResult result, string password)
    {
        var text = result.StandardError.Replace(password, "***", StringComparison.Ordinal).Trim();
        return text.Length <= MaxLoggedDiagnostics ? text : text[..MaxLoggedDiagnostics];
    }
}
