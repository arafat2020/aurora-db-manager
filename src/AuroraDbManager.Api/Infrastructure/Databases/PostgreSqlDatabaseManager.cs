using System.Data.Common;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AuroraDbManager.Api.Infrastructure.Databases;

/// <summary>
/// Manages databases of PostgreSQL instances over the PostgreSQL protocol, through Npgsql. It
/// connects as the <c>postgres</c> superuser to the <c>postgres</c> maintenance database.
/// </summary>
/// <remarks>The connection factory replaces the driver connection; only tests supply one.</remarks>
public sealed class PostgreSqlDatabaseManager(
    IInstanceEndpointResolver endpoints,
    IInstanceSecretStore secrets,
    IOptions<DatabaseManagerOptions> options,
    ILogger<PostgreSqlDatabaseManager> logger,
    Func<string, DbConnection>? connectionFactory = null)
    : SqlDatabaseManager(endpoints, secrets, options, logger)
{
    private const string AdminUser = "postgres";
    private const string MaintenanceDatabase = "postgres";

    // PostgreSQL error codes (SQLSTATE).
    private const string DuplicateDatabase = "42P04";
    private const string UniqueViolation = "23505";
    private const string QueryCanceled = "57014";

    private static readonly NpgsqlCommandBuilder Quoting = new();

    public override InstanceEngine Engine => InstanceEngine.Postgres;

    protected override string ExistsSql => "SELECT 1 FROM pg_database WHERE datname = @name";

    protected override string BuildConnectionString(InstanceEndpoint endpoint, string adminPassword, DatabaseManagerOptions options) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            Username = AdminUser,
            Password = adminPassword,
            Database = MaintenanceDatabase,
            Timeout = options.ConnectTimeoutSeconds,
            CommandTimeout = options.CommandTimeoutSeconds,
            // One short-lived connection per operation; nothing is kept open towards an instance.
            Pooling = false
        }.ConnectionString;

    protected override DbConnection CreateConnection(string connectionString) =>
        connectionFactory?.Invoke(connectionString) ?? new NpgsqlConnection(connectionString);

    protected override string QuoteIdentifier(string name) => Quoting.QuoteIdentifier(name);

    protected override string CreateSql(string quotedName) => $"CREATE DATABASE {quotedName}";

    // FORCE ends the sessions still connected to the database; without it one open client
    // connection would make every attempt fail.
    protected override string DropSql(string quotedName) => $"DROP DATABASE IF EXISTS {quotedName} WITH (FORCE)";

    // Two concurrent CREATE DATABASE statements can also collide on pg_database's unique index.
    protected override bool IsAlreadyExists(Exception exception) =>
        exception is PostgresException { SqlState: DuplicateDatabase or UniqueViolation };

    protected override bool IsTimeout(Exception exception) =>
        WrapsTimeout(exception, wrapped => wrapped is PostgresException { SqlState: QueryCanceled });
}
