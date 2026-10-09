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

    protected override bool IsAuthenticationFailure(Exception exception) =>
        exception is MySqlException { ErrorCode: MySqlErrorCode.AccessDenied };

    /// <remarks>
    /// The image creates the administrator once per host it may connect from, <c>root@localhost</c>
    /// and <c>root@%</c>, with the same password. All of them are changed, in one statement, which
    /// MySQL 8 carries out for all or for none: no account is left behind with the old password.
    /// The password is a parameter of the command; the hosts are read from the server and written
    /// as string literals by the driver's own escaping.
    /// </remarks>
    protected override async Task ChangeAdminPasswordAsync(DbConnection connection, string newPassword, CancellationToken cancellationToken)
    {
        var hosts = new List<string>();
        await using (var accounts = connection.CreateCommand())
        {
            accounts.CommandText = "SELECT Host FROM mysql.user WHERE User = @user ORDER BY Host";
            AddParameter(accounts, "user", AdminUser);
            await using var reader = await accounts.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hosts.Add(reader.GetString(0));
            }
        }

        if (hosts.Count == 0)
        {
            throw new InvalidOperationException("The server has no administrator account.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "ALTER USER " + string.Join(
            ", ",
            hosts.Select(host => $"'{MySqlHelper.EscapeString(AdminUser)}'@'{MySqlHelper.EscapeString(host)}' IDENTIFIED BY @password"));
        AddParameter(command, "password", newPassword);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
