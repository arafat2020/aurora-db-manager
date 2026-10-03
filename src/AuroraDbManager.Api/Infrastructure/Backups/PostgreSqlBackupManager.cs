using System.Globalization;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Backups;

/// <summary>
/// Backs up a PostgreSQL database with <c>pg_dump</c> in custom format (<c>-F c</c>), the format
/// <c>pg_restore</c> reads. The password is given through a password file named by
/// <c>PGPASSFILE</c>; <c>--no-password</c> makes <c>pg_dump</c> fail rather than ask for one.
/// </summary>
public sealed class PostgreSqlBackupManager(
    IBackupStorageResolver storages,
    IInstanceEndpointResolver endpoints,
    IInstanceSecretStore secrets,
    IProcessRunner processes,
    IArtifactHasher hasher,
    IOptions<BackupOptions> options,
    ILogger<PostgreSqlBackupManager> logger)
    : DumpBackupManager(storages, endpoints, secrets, processes, hasher, options, logger)
{
    private const string AdminUser = PostgresDumpFormat.AdminUser;

    public override InstanceEngine Engine => InstanceEngine.Postgres;

    protected override string Extension => PostgresDumpFormat.Extension;

    protected override string Tool => "pg_dump";

    protected override string CredentialFileContent(InstanceEndpoint endpoint, Database database, string adminPassword) =>
        PostgresDumpFormat.PasswordFile(endpoint, database.Name, adminPassword);

    protected override ProcessRequest BuildRequest(
        InstanceEndpoint endpoint, Database database, string outputPath, string credentialFilePath, BackupOptions options) =>
        new(
            options.Tools.PgDumpPath,
            [
                "--format=custom",
                $"--file={outputPath}",
                $"--host={endpoint.Host}",
                $"--port={endpoint.Port.ToString(CultureInfo.InvariantCulture)}",
                $"--username={AdminUser}",
                "--no-password",
                $"--dbname={database.Name}"
            ],
            new Dictionary<string, string>
            {
                ["PGPASSFILE"] = credentialFilePath,
                ["PGCONNECT_TIMEOUT"] = options.ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture)
            },
            TimeSpan.FromSeconds(options.TimeoutSeconds));

    protected override Task<bool> IsCompleteDumpAsync(string path, CancellationToken cancellationToken) =>
        PostgresDumpFormat.IsArchiveAsync(path, cancellationToken);

    protected override BackupOperationException? ClassifyFailure(string diagnostics)
    {
        if (diagnostics.Contains("server version mismatch", StringComparison.OrdinalIgnoreCase))
        {
            return new BackupOperationException(
                BackupErrorCodes.BackupToolUnavailable,
                "The pg_dump installed on the server is older than the instance's PostgreSQL version.");
        }

        if (diagnostics.Contains("database \"", StringComparison.Ordinal) && diagnostics.Contains("does not exist", StringComparison.Ordinal))
        {
            return new BackupOperationException(
                BackupErrorCodes.BackupDatabaseUnavailable,
                "The database does not exist in the instance's database server.");
        }

        if (diagnostics.Contains("connection to server", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("could not connect", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("could not translate host name", StringComparison.OrdinalIgnoreCase))
        {
            return new BackupOperationException(
                BackupErrorCodes.BackupConnectionFailed,
                "Could not connect to the instance's database server.");
        }

        return null;
    }
}
