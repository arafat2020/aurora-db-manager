using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using AuroraDbManager.Api.Application.Backups;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Application.Restores;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Backups;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AuroraDbManager.Api.Infrastructure.Restores;

/// <summary>
/// Restores a PostgreSQL database from a custom-format archive with <c>pg_restore</c>, into the
/// database that already exists; the database itself is neither dropped nor created.
/// </summary>
/// <remarks>
/// <para>
/// <b>Emptying.</b> <c>pg_restore --clean</c> alone would only drop what the archive contains,
/// and leave everything created since the backup in place, which is a merge. Instead, every
/// schema of the database that is not the system's is dropped with all it holds, large objects
/// are removed, and <c>public</c> is put back as in a new database. Before that, the sessions
/// connected to this database, and to no other, are ended: one open transaction would otherwise
/// block the drop. A session that reconnects and takes locks again makes the attempt fail after
/// the lock timeout instead of waiting forever.
/// </para>
/// <para>
/// <b>Loading.</b> <c>pg_restore</c> runs in a single transaction and stops at the first error,
/// so the load takes effect completely or not at all; a failed load leaves the database empty,
/// not half restored. <c>--no-owner</c> and <c>--no-privileges</c> keep the archive from
/// deciding who owns or may use what: everything restored belongs to the administrator the
/// server is managed with. <c>--create</c> is not used. <c>--clean --if-exists</c> lets an
/// archive that carries its own <c>public</c> schema replace the one just put back.
/// </para>
/// <para>
/// The connection factory replaces the driver connection; only tests supply one.
/// </para>
/// </remarks>
public sealed partial class PostgreSqlRestoreManager(
    IBackupStorage storage,
    IInstanceEndpointResolver endpoints,
    IInstanceSecretStore secrets,
    IProcessRunner processes,
    IArtifactHasher hasher,
    IOptions<BackupOptions> options,
    ILogger<PostgreSqlRestoreManager> logger,
    Func<string, DbConnection>? connectionFactory = null)
    : DumpRestoreManager(storage, endpoints, secrets, processes, hasher, options, logger)
{
    /// <summary>Ends every other session connected to the database this session is on.</summary>
    public const string TerminateSessionsSql =
        "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()";

    public const string SetLockTimeoutSql = "SELECT set_config('lock_timeout', @timeout, false)";

    /// <summary>
    /// Drops every schema that is not the system's, removes all large objects, and recreates
    /// <c>public</c> the way a new database has it. No part of it comes from outside.
    /// </summary>
    public const string EmptyDatabaseSql =
        """
        DO $aurora$
        DECLARE
            target record;
        BEGIN
            FOR target IN
                SELECT nspname FROM pg_namespace
                WHERE nspname <> 'information_schema' AND nspname NOT LIKE 'pg\_%'
            LOOP
                EXECUTE format('DROP SCHEMA %I CASCADE', target.nspname);
            END LOOP;

            PERFORM lo_unlink(oid) FROM pg_largeobject_metadata;

            CREATE SCHEMA public;
            ALTER SCHEMA public OWNER TO pg_database_owner;
            GRANT USAGE ON SCHEMA public TO PUBLIC;
            COMMENT ON SCHEMA public IS 'standard public schema';
        END
        $aurora$
        """;

    public override InstanceEngine Engine => InstanceEngine.Postgres;

    protected override string Extension => PostgresDumpFormat.Extension;

    protected override string Tool => "pg_restore";

    protected override string CredentialFileContent(InstanceEndpoint endpoint, Database database, string adminPassword) =>
        PostgresDumpFormat.PasswordFile(endpoint, database.Name, adminPassword);

    protected override Task<bool> IsDumpAsync(string path, CancellationToken cancellationToken) =>
        PostgresDumpFormat.IsArchiveAsync(path, cancellationToken);

    // Reading the archive's table of contents needs no server and shows whether pg_restore can read it.
    protected override ProcessRequest BuildPreflightRequest(string artifactPath, BackupOptions options) =>
        new(options.Tools.PgRestorePath, ["--list", artifactPath], new Dictionary<string, string>(), TimeSpan.FromSeconds(options.Restore.TimeoutSeconds));

    protected override RestoreOperationException? CheckPreflight(ProcessResult result)
    {
        if (!result.TimedOut && result.ExitCode != 0
            && result.StandardError.Contains("unsupported version", StringComparison.OrdinalIgnoreCase))
        {
            return new RestoreOperationException(
                RestoreErrorCodes.RestoreToolUnavailable,
                "The pg_restore installed on the server is older than the pg_dump that wrote the backup.");
        }

        // Exit code 0 is not enough: pg_restore lists a damaged archive without complaint when it
        // reads a nonsensical number of entries, and would then "restore" nothing into a database
        // that has just been emptied. An archive pg_dump wrote always has entries, even for an
        // empty database.
        var entries = TocEntriesPattern().Match(result.StandardOutput);
        var readable = !result.TimedOut
            && result.ExitCode == 0
            && entries.Success
            && long.TryParse(entries.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var count)
            && count > 0;

        return readable
            ? null
            : new RestoreOperationException(
                RestoreErrorCodes.RestoreArtifactInvalid,
                "The backup's artifact is not a readable PostgreSQL archive.");
    }

    [GeneratedRegex(@"^;\s*TOC Entries:\s*(-?\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex TocEntriesPattern();

    protected override ProcessRequest BuildRestoreRequest(
        InstanceEndpoint endpoint, Database database, string artifactPath, string credentialFilePath, BackupOptions options) =>
        new(
            options.Tools.PgRestorePath,
            [
                $"--dbname={database.Name}",
                $"--host={endpoint.Host}",
                $"--port={endpoint.Port.ToString(CultureInfo.InvariantCulture)}",
                $"--username={PostgresDumpFormat.AdminUser}",
                "--no-password",
                "--no-owner",
                "--no-privileges",
                "--clean",
                "--if-exists",
                "--single-transaction",
                "--exit-on-error",
                artifactPath
            ],
            new Dictionary<string, string>
            {
                ["PGPASSFILE"] = credentialFilePath,
                ["PGCONNECT_TIMEOUT"] = options.ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture)
            },
            TimeSpan.FromSeconds(options.Restore.TimeoutSeconds));

    protected override RestoreOperationException? ClassifyFailure(string diagnostics)
    {
        if (diagnostics.Contains("database \"", StringComparison.Ordinal) && diagnostics.Contains("does not exist", StringComparison.Ordinal))
        {
            return new RestoreOperationException(
                RestoreErrorCodes.RestoreDatabaseUnavailable,
                "The database does not exist in the instance's database server.");
        }

        if (diagnostics.Contains("connection to server", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("could not connect", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("could not translate host name", StringComparison.OrdinalIgnoreCase))
        {
            return new RestoreOperationException(
                RestoreErrorCodes.RestoreConnectionFailed,
                "Could not connect to the instance's database server.");
        }

        return null;
    }

    protected override string BuildConnectionString(InstanceEndpoint endpoint, Database database, string adminPassword, BackupOptions options) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            Username = PostgresDumpFormat.AdminUser,
            Password = adminPassword,
            // The target database itself, not the maintenance database: what is emptied is what is connected to.
            Database = database.Name,
            Timeout = options.ConnectTimeoutSeconds,
            Pooling = false
        }.ConnectionString;

    protected override DbConnection CreateConnection(string connectionString) =>
        connectionFactory?.Invoke(connectionString) ?? new NpgsqlConnection(connectionString);

    protected override async Task EmptyDatabaseAsync(DbConnection connection, BackupOptions options, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, TerminateSessionsSql, options, cancellationToken);
        await ExecuteAsync(
            connection, SetLockTimeoutSql, options, cancellationToken,
            ("timeout", $"{options.Restore.LockTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}s"));
        await ExecuteAsync(connection, EmptyDatabaseSql, options, cancellationToken);
    }
}
