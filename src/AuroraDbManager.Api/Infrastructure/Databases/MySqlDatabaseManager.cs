using System.Data.Common;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace AuroraDbManager.Api.Infrastructure.Databases;

/// <summary>
/// Manages databases of MySQL instances over the MySQL protocol, through MySqlConnector. It
/// connects as <c>root</c>, without selecting a database.
/// </summary>
/// <remarks>The connection factory replaces the driver connection; only tests supply one.</remarks>
public sealed class MySqlDatabaseManager(
    IInstanceEndpointResolver endpoints,
    IInstanceSecretStore secrets,
    IOptions<DatabaseManagerOptions> options,
    ILogger<MySqlDatabaseManager> logger,
    Func<string, DbConnection>? connectionFactory = null)
    : SqlDatabaseManager(endpoints, secrets, options, logger)
{
    private const string AdminUser = "root";

    private static readonly MySqlCommandBuilder Quoting = new();

    public override InstanceEngine Engine => InstanceEngine.Mysql;

    protected override string ExistsSql => "SELECT 1 FROM information_schema.schemata WHERE schema_name = @name";

    protected override string BuildConnectionString(InstanceEndpoint endpoint, string adminPassword, DatabaseManagerOptions options) =>
        new MySqlConnectionStringBuilder
        {
            Server = endpoint.Host,
            Port = (uint)endpoint.Port,
            UserID = AdminUser,
            Password = adminPassword,
            ConnectionTimeout = (uint)options.ConnectTimeoutSeconds,
            DefaultCommandTimeout = (uint)options.CommandTimeoutSeconds,
            // One short-lived connection per operation; nothing is kept open towards an instance.
            Pooling = false
        }.ConnectionString;

    protected override DbConnection CreateConnection(string connectionString) =>
        connectionFactory?.Invoke(connectionString) ?? new MySqlConnection(connectionString);

    protected override string QuoteIdentifier(string name) => Quoting.QuoteIdentifier(name);

    protected override string CreateSql(string quotedName) => $"CREATE DATABASE IF NOT EXISTS {quotedName}";

    protected override string DropSql(string quotedName) => $"DROP DATABASE IF EXISTS {quotedName}";

    protected override bool IsAlreadyExists(Exception exception) =>
        exception is MySqlException { ErrorCode: MySqlErrorCode.DatabaseCreateExists };

    // A connect timeout is reported as "unable to connect" wrapping the timeout that caused it.
    protected override bool IsTimeout(Exception exception) =>
        WrapsTimeout(exception, wrapped => wrapped is MySqlException { ErrorCode: MySqlErrorCode.CommandTimeoutExpired });
}
