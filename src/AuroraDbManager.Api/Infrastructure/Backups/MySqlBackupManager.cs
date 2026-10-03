using System.Globalization;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Backups;

/// <summary>
/// Backs up a MySQL database with <c>mysqldump</c> as an SQL script: one consistent snapshot
/// (<c>--single-transaction</c>) of the database's tables, routines, triggers and events. The
/// password is given through an option file named by <c>--defaults-extra-file</c>. The script
/// does not name the database, so it can later be loaded into any database.
/// </summary>
/// <remarks>
/// <c>mysqldump</c> has no connect timeout of its own; a server that never answers is ended by
/// the backup timeout.
/// </remarks>
public sealed class MySqlBackupManager(
    IBackupStorage storage,
    IInstanceEndpointResolver endpoints,
    IInstanceSecretStore secrets,
    IProcessRunner processes,
    IArtifactHasher hasher,
    IOptions<BackupOptions> options,
    ILogger<MySqlBackupManager> logger)
    : DumpBackupManager(storage, endpoints, secrets, processes, hasher, options, logger)
{
    private const string AdminUser = MySqlDumpFormat.AdminUser;

    public override InstanceEngine Engine => InstanceEngine.Mysql;

    protected override string Extension => MySqlDumpFormat.Extension;

    protected override string Tool => "mysqldump";

    protected override string CredentialFileContent(InstanceEndpoint endpoint, Database database, string adminPassword) =>
        MySqlDumpFormat.OptionFile(adminPassword);

    protected override ProcessRequest BuildRequest(
        InstanceEndpoint endpoint, Database database, string outputPath, string credentialFilePath, BackupOptions options) =>
        new(
            options.Tools.MySqlDumpPath,
            [
                // Must come first to be honoured.
                $"--defaults-extra-file={credentialFilePath}",
                $"--host={endpoint.Host}",
                $"--port={endpoint.Port.ToString(CultureInfo.InvariantCulture)}",
                "--protocol=TCP",
                $"--user={AdminUser}",
                "--single-transaction",
                "--routines",
                "--triggers",
                "--events",
                "--hex-blob",
                $"--result-file={outputPath}",
                // Everything after this is a name, never an option.
                "--",
                database.Name
            ],
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(options.TimeoutSeconds));

    protected override Task<bool> IsCompleteDumpAsync(string path, CancellationToken cancellationToken) =>
        MySqlDumpFormat.IsCompleteDumpAsync(path, cancellationToken);

    protected override BackupOperationException? ClassifyFailure(string diagnostics)
    {
        if (diagnostics.Contains("Unknown database", StringComparison.OrdinalIgnoreCase))
        {
            return new BackupOperationException(
                BackupErrorCodes.BackupDatabaseUnavailable,
                "The database does not exist in the instance's database server.");
        }

        if (diagnostics.Contains("Can't connect", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("Lost connection", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("Unknown MySQL server host", StringComparison.OrdinalIgnoreCase))
        {
            return new BackupOperationException(
                BackupErrorCodes.BackupConnectionFailed,
                "Could not connect to the instance's database server.");
        }

        return null;
    }
}
