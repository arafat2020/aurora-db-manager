using System.Data.Common;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Restores;
using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Backups;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Restores;

/// <summary>
/// What the PostgreSQL and MySQL restore managers have in common. A restore replaces what is in
/// the target database with what is in the backup; it is not a merge. Everything engine-specific
/// is left to the subclass.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order of events.</b> (1) The artifact is fetched from the storage the backup is in, which
/// the backup's own record names and which need not be the server's current default, into the attempt's
/// own directory as a <c>.partial</c> file. (2) It must have exactly the size the backup records,
/// the SHA-256 the backup records, and look like a dump of the engine; only then is it given its
/// final name. A backup completed before checksums were recorded has none to compare with and is
/// held to size and format only. (3) The restore
/// program is run once without a database, to prove that it is installed and can read the
/// artifact. Up to here nothing has touched the database, and a failure leaves it as it was.
/// (4) The sessions connected to the target database are ended and the database is emptied.
/// (5) The restore program loads the artifact. (6) The database is connected to once more, to see
/// that it is usable. The attempt's directory and the credential file are removed however the
/// attempt ends.
/// </para>
/// <para>
/// <b>Repeating an attempt.</b> Every attempt fetches the artifact afresh and empties the
/// database before loading, so it does not matter what an earlier attempt left behind: nothing,
/// half a download, an empty database, or half the backup's tables.
/// </para>
/// <para>
/// <b>Where it runs.</b> Like a backup: the restore program runs on the machine of the API and
/// connects to the instance's server at the endpoint given by <see cref="IInstanceEndpointResolver"/>.
/// The password comes from <see cref="IInstanceSecretStore"/> and reaches the program through a
/// <see cref="TemporaryCredentialFile"/>, never as an argument or an environment variable.
/// </para>
/// </remarks>
public abstract class DumpRestoreManager(
    IBackupStorageResolver storages,
    IInstanceEndpointResolver endpoints,
    IInstanceSecretStore secrets,
    IProcessRunner processes,
    IArtifactHasher hasher,
    IOptions<BackupOptions> options,
    ILogger logger) : IRestoreManager
{
    private const string StagingSuffix = ".partial";
    private const int MaxLoggedDiagnostics = 4000;
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public abstract InstanceEngine Engine { get; }

    /// <summary>File extension of the engine's artifacts, without the dot.</summary>
    protected abstract string Extension { get; }

    /// <summary>Name of the restore program, for logs and messages.</summary>
    protected abstract string Tool { get; }

    /// <summary>The content of the file that gives the program the administrator's password.</summary>
    protected abstract string CredentialFileContent(InstanceEndpoint endpoint, Database database, string adminPassword);

    /// <summary>Whether the file looks like a dump of this engine.</summary>
    protected abstract Task<bool> IsDumpAsync(string path, CancellationToken cancellationToken);

    /// <summary>A run of the program that proves it is installed and usable, without connecting to anything.</summary>
    protected abstract ProcessRequest BuildPreflightRequest(string artifactPath, BackupOptions options);

    /// <summary>Judges the preflight run: null if the program is usable on the artifact, otherwise what is wrong.</summary>
    protected abstract RestoreOperationException? CheckPreflight(ProcessResult result);

    /// <summary>The run that loads the artifact into the database. Must not contain the password.</summary>
    protected abstract ProcessRequest BuildRestoreRequest(
        InstanceEndpoint endpoint, Database database, string artifactPath, string credentialFilePath, BackupOptions options);

    /// <summary>Tells what a failed restore run's diagnostics mean; null for a failure of no particular kind.</summary>
    protected abstract RestoreOperationException? ClassifyFailure(string diagnostics);

    /// <summary>The connection string for the server's administrator, with the target database selected. Contains the password.</summary>
    protected abstract string BuildConnectionString(InstanceEndpoint endpoint, Database database, string adminPassword, BackupOptions options);

    /// <summary>Creates a closed connection of the engine's driver.</summary>
    protected abstract DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// Ends the other sessions connected to the database the connection is on, and to no other,
    /// and removes everything in that database, leaving it as a newly created one.
    /// </summary>
    protected abstract Task EmptyDatabaseAsync(DbConnection connection, BackupOptions options, CancellationToken cancellationToken);

    public async Task RestoreAsync(Instance instance, Database database, Backup backup, Guid jobId, CancellationToken cancellationToken)
    {
        if (backup.Status != BackupStatus.Completed || backup.Path is null || backup.SizeBytes is null || backup.DatabaseId != database.Id)
        {
            throw new RestoreOperationException(RestoreErrorCodes.RestoreInvalidState, "The backup is not a completed backup of the database.");
        }

        // The storage the backup's own record names, whatever the server's default for new
        // backups is now.
        IBackupStorage storage;
        try
        {
            storage = storages.Resolve(backup.StorageType);
        }
        catch (BackupOperationException exception)
        {
            throw new RestoreOperationException(exception.Code, exception.Message, exception);
        }

        InstanceEndpoint endpoint;
        try
        {
            endpoint = await endpoints.ResolveAsync(instance, cancellationToken);
        }
        catch (DatabaseOperationException exception)
        {
            throw new RestoreOperationException(RestoreErrorCodes.RestoreDatabaseUnavailable, exception.Message, exception);
        }

        var password = await secrets.GetOrCreateAdminPasswordAsync(instance.Id, cancellationToken);
        var directory = Path.Combine(StagingRoot, jobId.ToString("D"));

        try
        {
            var artifactPath = await FetchArtifactAsync(storage, backup, directory, cancellationToken);

            await using var credentials = await TemporaryCredentialFile.CreateAsync(
                CredentialFileContent(endpoint, database, password), cancellationToken);

            await PreflightAsync(artifactPath, password, cancellationToken);

            // From here on the database is changed.
            var connectionString = BuildConnectionString(endpoint, database, password, options.Value);
            await EmptyAsync(connectionString, database, cancellationToken);
            await LoadAsync(endpoint, database, backup, artifactPath, credentials.Path, password, cancellationToken);
            await VerifyAsync(connectionString, cancellationToken);

            logger.LogInformation("Restored backup {BackupId} into database {DatabaseId}", backup.Id, database.Id);
        }
        finally
        {
            RemoveDirectory(directory);
        }
    }

    private string StagingRoot => Path.GetFullPath(
        string.IsNullOrWhiteSpace(options.Value.Restore.StagingPath)
            ? Path.Combine(Path.GetTempPath(), "aurora-restore-staging")
            : options.Value.Restore.StagingPath);

    /// <summary>Copies the artifact out of the storage and returns its local path once it has been checked.</summary>
    private async Task<string> FetchArtifactAsync(
        IBackupStorage storage, Backup backup, string directory, CancellationToken cancellationToken)
    {
        var artifactPath = Path.Combine(directory, $"artifact.{Extension}");
        var partialPath = artifactPath + StagingSuffix;

        try
        {
            // An earlier attempt of the same job may have been interrupted here; none of it is reused.
            RemoveDirectory(directory);
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(StagingRoot, DirectoryMode);
                Directory.CreateDirectory(directory, DirectoryMode);
            }

            // Created here, private, so the download only ever writes into an existing private file.
            var create = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                create.UnixCreateMode = FileMode;
            }

            using (new FileStream(partialPath, create))
            {
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new RestoreOperationException(RestoreErrorCodes.RestoreStorageFailed, "The backup could not be staged for the restore.", exception);
        }

        logger.LogInformation("Fetching the artifact of backup {BackupId} from {StorageType} storage", backup.Id, backup.StorageType);

        try
        {
            await storage.DownloadAsync(
                new BackupArtifact(backup.StorageType, backup.Path!, backup.SizeBytes!.Value, backup.Checksum), partialPath, cancellationToken);
        }
        catch (BackupOperationException exception)
        {
            throw exception.Code switch
            {
                BackupErrorCodes.BackupArtifactNotFound => new RestoreOperationException(
                    RestoreErrorCodes.RestoreArtifactNotFound, "The backup's artifact is not in the backup storage.", exception),
                BackupErrorCodes.BackupStorageFailed => new RestoreOperationException(
                    RestoreErrorCodes.RestoreStorageFailed, "The backup could not be read from the backup storage.", exception),
                // The object storage's own failures keep their code: they mean the same here.
                _ => new RestoreOperationException(exception.Code, exception.Message, exception)
            };
        }

        // A file that is not exactly what the backup recorded is not that backup. Size first:
        // it costs nothing.
        var size = new FileInfo(partialPath).Length;
        if (size == 0 || size != backup.SizeBytes)
        {
            logger.LogWarning(
                "The artifact of backup {BackupId} is {SizeBytes} bytes, recorded as {RecordedSizeBytes}",
                backup.Id, size, backup.SizeBytes);
            throw new RestoreOperationException(
                RestoreErrorCodes.RestoreArtifactInvalid, "The backup's artifact is not the backup that was stored.");
        }

        if (backup.Checksum is not null)
        {
            // Every byte of what was fetched, against what was verified when the backup completed.
            var checksum = await hasher.ComputeAsync(partialPath, cancellationToken);
            if (!string.Equals(checksum, backup.Checksum, StringComparison.Ordinal))
            {
                // Both values are safe to log; neither goes to the client.
                logger.LogWarning(
                    "The artifact of backup {BackupId} has checksum {Checksum}, recorded as {RecordedChecksum}",
                    backup.Id, checksum, backup.Checksum);
                throw new RestoreOperationException(
                    RestoreErrorCodes.RestoreArtifactChecksumMismatch,
                    "The backup's artifact does not have the checksum recorded for the backup.");
            }
        }
        else
        {
            // Completed before checksums existed: there is nothing to compare with, and no
            // checksum is made up for it. It is held to its size and format, as it always was.
            logger.LogInformation("Backup {BackupId} has no recorded checksum; its artifact is checked by size and format only", backup.Id);
        }

        if (!await IsDumpAsync(partialPath, cancellationToken))
        {
            logger.LogWarning(
                "The artifact of backup {BackupId} is {SizeBytes} bytes, recorded as {RecordedSizeBytes}, or is not a dump",
                backup.Id, size, backup.SizeBytes);
            throw new RestoreOperationException(
                RestoreErrorCodes.RestoreArtifactInvalid, "The backup's artifact is not the backup that was stored.");
        }

        try
        {
            File.Move(partialPath, artifactPath, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RestoreOperationException(RestoreErrorCodes.RestoreStorageFailed, "The backup could not be staged for the restore.", exception);
        }

        return artifactPath;
    }

    private async Task PreflightAsync(string artifactPath, string password, CancellationToken cancellationToken)
    {
        var result = await RunAsync(BuildPreflightRequest(artifactPath, options.Value), cancellationToken);
        if (CheckPreflight(result) is { } problem)
        {
            logger.LogWarning(
                "{Tool} could not be used on the artifact (exit code {ExitCode}): {Diagnostics}",
                Tool, result.ExitCode, Diagnostics(result, password));
            throw problem;
        }
    }

    private async Task EmptyAsync(string connectionString, Database database, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, RestoreErrorCodes.RestoreConnectionFailed, cancellationToken);

        try
        {
            await EmptyDatabaseAsync(connection, options.Value, cancellationToken);
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            throw new RestoreOperationException(
                RestoreErrorCodes.RestoreProcessFailed, "The database could not be emptied before the restore.", exception);
        }

        logger.LogInformation("Emptied database {DatabaseId} for the restore", database.Id);
    }

    private async Task LoadAsync(
        InstanceEndpoint endpoint,
        Database database,
        Backup backup,
        string artifactPath,
        string credentialFilePath,
        string password,
        CancellationToken cancellationToken)
    {
        var request = BuildRestoreRequest(endpoint, database, artifactPath, credentialFilePath, options.Value);

        logger.LogInformation("Starting {Tool} for backup {BackupId} into database {DatabaseId}", Tool, backup.Id, database.Id);
        var result = await RunAsync(request, cancellationToken);

        if (result.TimedOut)
        {
            logger.LogWarning(
                "{Tool} restoring backup {BackupId} was stopped after {TimeoutSeconds}s: {Diagnostics}",
                Tool, backup.Id, request.Timeout.TotalSeconds, Diagnostics(result, password));
            throw new RestoreOperationException(RestoreErrorCodes.RestoreOperationTimeout, "The restore did not finish in time.");
        }

        if (result.ExitCode != 0)
        {
            // The program's own words stay in the log; the client gets a code and a fixed message.
            logger.LogWarning(
                "{Tool} restoring backup {BackupId} exited with code {ExitCode}: {Diagnostics}",
                Tool, backup.Id, result.ExitCode, Diagnostics(result, password));
            throw ClassifyFailure(result.StandardError)
                ?? new RestoreOperationException(RestoreErrorCodes.RestoreProcessFailed, $"The restore program {Tool} failed.");
        }
    }

    /// <summary>The program's word that it succeeded is not enough: the database must answer.</summary>
    private async Task VerifyAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, RestoreErrorCodes.RestoreVerificationFailed, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            throw new RestoreOperationException(
                RestoreErrorCodes.RestoreVerificationFailed, "The restored database could not be used.", exception);
        }
    }

    private async Task<DbConnection> OpenAsync(string connectionString, string failureCode, CancellationToken cancellationToken)
    {
        var connection = CreateConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (Exception exception)
        {
            await connection.DisposeAsync();
            if (IsCancellation(exception, cancellationToken))
            {
                throw;
            }

            // The driver's exception is kept for logs; it never carries the connection string.
            throw new RestoreOperationException(failureCode, "Could not connect to the database.", exception);
        }
    }

    private async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await processes.RunAsync(request, cancellationToken);
        }
        catch (ProcessStartException exception)
        {
            throw new RestoreOperationException(
                RestoreErrorCodes.RestoreToolUnavailable,
                $"The restore program {Tool} is not available on the server.",
                exception);
        }
    }

    private void RemoveDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left behind; another attempt of the same job removes it before it starts.
            logger.LogWarning(exception, "A restore working directory could not be removed");
        }
    }

    /// <summary>Runs one statement on the connection.</summary>
    protected static async Task ExecuteAsync(
        DbConnection connection, string sql, BackupOptions options, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = options.Restore.TimeoutSeconds;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Runs one query on the connection and returns its single value as text, or null if there is none.</summary>
    protected static async Task<string?> ScalarAsync(DbConnection connection, string sql, BackupOptions options, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = options.Restore.TimeoutSeconds;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    /// <summary>The program's diagnostics, fit for a log: without the password, and bounded.</summary>
    private static string Diagnostics(ProcessResult result, string password)
    {
        var text = result.StandardError.Replace(password, "***", StringComparison.Ordinal).Trim();
        return text.Length <= MaxLoggedDiagnostics ? text : text[..MaxLoggedDiagnostics];
    }
}
