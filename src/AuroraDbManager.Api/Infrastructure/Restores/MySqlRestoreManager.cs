using System.Data.Common;
using System.Globalization;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Restores;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Backups;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace AuroraDbManager.Api.Infrastructure.Restores;

/// <summary>
/// Restores a MySQL database from the SQL script <c>mysqldump</c> wrote, with the <c>mysql</c>
/// client, into the database that already exists; the database itself is neither dropped nor created.
/// </summary>
/// <remarks>
/// <para>
/// <b>Emptying.</b> The script drops and recreates the tables it contains, but leaves anything
/// created since the backup in place, which would be a merge. So first every view, table (and
/// with it its triggers), procedure, function and event of the database is dropped. Before that,
/// the sessions whose current database is this one, and no others, are ended: an open transaction
/// would otherwise block the drops. A session that reconnects and takes locks again makes the
/// attempt fail after the lock timeout instead of waiting forever.
/// </para>
/// <para>
/// <b>Loading.</b> The script is streamed to the client's standard input, preceded by one
/// <c>USE</c> statement for the target database. The script names no database itself, and
/// <c>--one-database</c> makes the client ignore any statement that would act on another one,
/// should a script ever try. That option makes the client ignore everything until a <c>USE</c>
/// for the named database has been seen, which is why the statement is sent first. MySQL's DDL is not
/// transactional: a load that fails part way leaves the tables loaded so far, and the next
/// attempt starts by emptying the database again.
/// </para>
/// <para>
/// The names in the <c>DROP</c> statements are quoted by the server itself, in the query that
/// lists them. The connection factory replaces the driver connection; only tests supply one.
/// </para>
/// </remarks>
public sealed class MySqlRestoreManager(
    IBackupStorage storage,
    IInstanceEndpointResolver endpoints,
    IInstanceSecretStore secrets,
    IProcessRunner processes,
    IOptions<BackupOptions> options,
    ILogger<MySqlRestoreManager> logger,
    Func<string, DbConnection>? connectionFactory = null)
    : DumpRestoreManager(storage, endpoints, secrets, processes, options, logger)
{
    private static readonly MySqlCommandBuilder Quoting = new();

    // A name can contain anything but this character, so it separates names safely.
    private const char Separator = '\0';

    /// <summary>The ids of the other sessions whose current database is the one this session is on.</summary>
    public const string OtherSessionsSql =
        "SELECT GROUP_CONCAT(id SEPARATOR '\\0') FROM information_schema.processlist WHERE db = DATABASE() AND id <> CONNECTION_ID()";

    /// <summary>Lists, already quoted, what a statement <c>DROP VIEW IF EXISTS</c> has to name.</summary>
    public const string ViewsSql =
        "SELECT GROUP_CONCAT(CONCAT('`', REPLACE(table_name, '`', '``'), '`') SEPARATOR ', ') FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'VIEW'";

    public const string TablesSql =
        "SELECT GROUP_CONCAT(CONCAT('`', REPLACE(table_name, '`', '``'), '`') SEPARATOR ', ') FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'";

    /// <summary>Lists what follows <c>DROP</c> for every procedure and function: its kind, <c>IF EXISTS</c> and its quoted name.</summary>
    public const string RoutinesSql =
        "SELECT GROUP_CONCAT(CONCAT(routine_type, ' IF EXISTS `', REPLACE(routine_name, '`', '``'), '`') SEPARATOR '\\0') FROM information_schema.routines WHERE routine_schema = DATABASE() AND routine_type IN ('PROCEDURE', 'FUNCTION')";

    public const string EventsSql =
        "SELECT GROUP_CONCAT(CONCAT('EVENT IF EXISTS `', REPLACE(event_name, '`', '``'), '`') SEPARATOR '\\0') FROM information_schema.events WHERE event_schema = DATABASE()";

    public override InstanceEngine Engine => InstanceEngine.Mysql;

    protected override string Extension => MySqlDumpFormat.Extension;

    protected override string Tool => "mysql";

    protected override string CredentialFileContent(InstanceEndpoint endpoint, Database database, string adminPassword) =>
        MySqlDumpFormat.OptionFile(adminPassword);

    protected override Task<bool> IsDumpAsync(string path, CancellationToken cancellationToken) =>
        MySqlDumpFormat.IsCompleteDumpAsync(path, cancellationToken);

    protected override ProcessRequest BuildPreflightRequest(string artifactPath, BackupOptions options) =>
        new(options.Tools.MySqlPath, ["--version"], new Dictionary<string, string>(), TimeSpan.FromSeconds(60));

    protected override RestoreOperationException? CheckPreflight(ProcessResult result) =>
        !result.TimedOut && result.ExitCode == 0
            ? null
            : new RestoreOperationException(RestoreErrorCodes.RestoreToolUnavailable, "The restore program mysql is not usable on the server.");

    protected override ProcessRequest BuildRestoreRequest(
        InstanceEndpoint endpoint, Database database, string artifactPath, string credentialFilePath, BackupOptions options) =>
        new(
            options.Tools.MySqlPath,
            [
                // Must come first to be honoured.
                $"--defaults-extra-file={credentialFilePath}",
                $"--host={endpoint.Host}",
                $"--port={endpoint.Port.ToString(CultureInfo.InvariantCulture)}",
                "--protocol=TCP",
                $"--user={MySqlDumpFormat.AdminUser}",
                $"--connect-timeout={options.ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}",
                "--default-character-set=utf8mb4",
                // Statements for any database but the selected one are ignored.
                "--one-database",
                $"--database={database.Name}"
            ],
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(options.Restore.TimeoutSeconds),
            // With --one-database the client acts on nothing until the script has selected the
            // named database; the dump selects none, so this does. The name is quoted by the driver.
            StandardInput: $"USE {Quoting.QuoteIdentifier(database.Name)};\n",
            // The script is streamed from the file; it is never an argument and never in memory as a whole.
            StandardInputFilePath: artifactPath);

    protected override RestoreOperationException? ClassifyFailure(string diagnostics)
    {
        if (diagnostics.Contains("Unknown database", StringComparison.OrdinalIgnoreCase))
        {
            return new RestoreOperationException(
                RestoreErrorCodes.RestoreDatabaseUnavailable,
                "The database does not exist in the instance's database server.");
        }

        if (diagnostics.Contains("Can't connect", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("Lost connection", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("Unknown MySQL server host", StringComparison.OrdinalIgnoreCase))
        {
            return new RestoreOperationException(
                RestoreErrorCodes.RestoreConnectionFailed,
                "Could not connect to the instance's database server.");
        }

        return null;
    }

    protected override string BuildConnectionString(InstanceEndpoint endpoint, Database database, string adminPassword, BackupOptions options) =>
        new MySqlConnectionStringBuilder
        {
            Server = endpoint.Host,
            Port = (uint)endpoint.Port,
            UserID = MySqlDumpFormat.AdminUser,
            Password = adminPassword,
            // The target database itself: what is emptied is what is connected to.
            Database = database.Name,
            ConnectionTimeout = (uint)options.ConnectTimeoutSeconds,
            Pooling = false
        }.ConnectionString;

    protected override DbConnection CreateConnection(string connectionString) =>
        connectionFactory?.Invoke(connectionString) ?? new MySqlConnection(connectionString);

    protected override async Task EmptyDatabaseAsync(DbConnection connection, BackupOptions options, CancellationToken cancellationToken)
    {
        var lockTimeout = options.Restore.LockTimeoutSeconds.ToString(CultureInfo.InvariantCulture);

        // Long enough for the lists below, whatever the number of objects.
        await ExecuteAsync(connection, "SET SESSION group_concat_max_len = 4294967295", options, cancellationToken);
        await ExecuteAsync(connection, $"SET SESSION lock_wait_timeout = {lockTimeout}", options, cancellationToken);

        foreach (var session in Split(await ScalarAsync(connection, OtherSessionsSql, options, cancellationToken)))
        {
            // Session ids are numbers the server handed out; anything else is not put into a statement.
            if (!ulong.TryParse(session, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            try
            {
                await ExecuteAsync(connection, $"KILL {id.ToString(CultureInfo.InvariantCulture)}", options, cancellationToken);
            }
            catch (DbException) when (!cancellationToken.IsCancellationRequested)
            {
                // It ended by itself in the meantime.
            }
        }

        // Tables are dropped in whatever order the server lists them.
        await ExecuteAsync(connection, "SET SESSION foreign_key_checks = 0", options, cancellationToken);

        if (await ScalarAsync(connection, ViewsSql, options, cancellationToken) is { Length: > 0 } views)
        {
            await ExecuteAsync(connection, $"DROP VIEW IF EXISTS {views}", options, cancellationToken);
        }

        if (await ScalarAsync(connection, TablesSql, options, cancellationToken) is { Length: > 0 } tables)
        {
            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {tables}", options, cancellationToken);
        }

        foreach (var sql in new[] { RoutinesSql, EventsSql })
        {
            foreach (var item in Split(await ScalarAsync(connection, sql, options, cancellationToken)))
            {
                await ExecuteAsync(connection, $"DROP {item}", options, cancellationToken);
            }
        }
    }

    private static string[] Split(string? list) =>
        string.IsNullOrEmpty(list) ? [] : list.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
}
