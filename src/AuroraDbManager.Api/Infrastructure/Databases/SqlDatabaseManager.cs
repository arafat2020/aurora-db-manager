using System.Data.Common;
using AuroraDbManager.Api.Application.Credentials;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Databases;

/// <summary>
/// What the PostgreSQL and MySQL managers have in common: connect to the instance's server as its
/// administrator, check whether the database exists, create or drop it, and turn driver failures
/// into client-safe errors. Everything engine-specific is left to the subclass.
/// </summary>
/// <remarks>
/// <para>
/// <b>Connection.</b> One unpooled connection per operation, to the endpoint given by
/// <see cref="IInstanceEndpointResolver"/>, with the administrator password from
/// <see cref="IInstanceSecretStore"/>. The connection string holds that password and is never
/// logged or put into an exception.
/// </para>
/// <para>
/// <b>Idempotency.</b> Creating a database that already exists and dropping one that does not both
/// succeed, so an attempt interrupted after the engine did its work can simply be repeated. A
/// database found under the expected name is taken to be the one meant: the connection goes to the
/// instance's own server, and a name is unique within an instance.
/// </para>
/// <para>
/// <b>SQL.</b> The database name is the only variable part. It is passed as a parameter where the
/// engine accepts one, and quoted as an identifier by the driver where it does not.
/// </para>
/// <para>
/// <b>The administrator's password.</b> Changing it is the one statement that carries a secret.
/// How it is carried is the engine's business, see the subclasses; what is common is that a
/// failure of that statement is reported without the driver's exception, whose message may quote
/// the statement, and that checking a password is a connection opened for nothing else.
/// </para>
/// </remarks>
public abstract class SqlDatabaseManager(
    IInstanceEndpointResolver endpoints,
    IInstanceSecretStore secrets,
    IOptions<DatabaseManagerOptions> options,
    ILogger logger) : IDatabaseManager, IAdminCredentialManager
{
    public abstract InstanceEngine Engine { get; }

    /// <summary>Query that returns a row if the database named by the parameter <c>@name</c> exists.</summary>
    protected abstract string ExistsSql { get; }

    /// <summary>The connection string for the server's administrator. Contains the password.</summary>
    protected abstract string BuildConnectionString(InstanceEndpoint endpoint, string adminPassword, DatabaseManagerOptions options);

    /// <summary>Creates a closed connection of the engine's driver.</summary>
    protected abstract DbConnection CreateConnection(string connectionString);

    /// <summary>Quotes <paramref name="name"/> as an identifier by the engine's rules.</summary>
    protected abstract string QuoteIdentifier(string name);

    protected abstract string CreateSql(string quotedName);

    protected abstract string DropSql(string quotedName);

    /// <summary>Whether a failed <c>CREATE DATABASE</c> failed because the database exists.</summary>
    protected abstract bool IsAlreadyExists(Exception exception);

    /// <summary>Whether the driver gave up waiting for the server.</summary>
    protected abstract bool IsTimeout(Exception exception);

    /// <summary>Whether a failed connection attempt failed because the server refused the password.</summary>
    protected abstract bool IsAuthenticationFailure(Exception exception);

    /// <summary>
    /// Makes <paramref name="newPassword"/> the password of the administrator, on a connection
    /// that is open as the administrator.
    /// </summary>
    protected abstract Task ChangeAdminPasswordAsync(DbConnection connection, string newPassword, CancellationToken cancellationToken);

    public async Task CreateDatabaseAsync(Instance instance, Database database, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(instance, cancellationToken);

        try
        {
            if (await ExistsAsync(connection, database.Name, cancellationToken))
            {
                logger.LogInformation(
                    "Database {DatabaseId} already exists in instance {InstanceId}; adopted",
                    database.Id, instance.Id);
                return;
            }

            try
            {
                await ExecuteAsync(connection, CreateSql(QuoteIdentifier(database.Name)), cancellationToken);
            }
            catch (DbException exception) when (IsAlreadyExists(exception))
            {
                // Created between the check and the statement, by an earlier attempt still finishing.
                logger.LogInformation(
                    "Database {DatabaseId} already exists in instance {InstanceId}; adopted",
                    database.Id, instance.Id);
                return;
            }

            logger.LogInformation("Created database {DatabaseId} in instance {InstanceId}", database.Id, instance.Id);
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            throw Translate(exception, DatabaseErrorCodes.DatabaseCreateFailed, "The database could not be created.");
        }
    }

    public async Task DeleteDatabaseAsync(Instance instance, Database database, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(instance, cancellationToken);

        try
        {
            if (!await ExistsAsync(connection, database.Name, cancellationToken))
            {
                logger.LogInformation(
                    "Database {DatabaseId} does not exist in instance {InstanceId}; nothing to drop",
                    database.Id, instance.Id);
                return;
            }

            await ExecuteAsync(connection, DropSql(QuoteIdentifier(database.Name)), cancellationToken);
            logger.LogInformation("Dropped database {DatabaseId} from instance {InstanceId}", database.Id, instance.Id);
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            throw Translate(exception, DatabaseErrorCodes.DatabaseDeleteFailed, "The database could not be deleted.");
        }
    }

    public async Task<bool> AuthenticatesAsync(Instance instance, string password, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(instance, cancellationToken, password);
            return true;
        }
        catch (DatabaseOperationException exception) when (exception.InnerException is { } cause && IsAuthenticationFailure(cause))
        {
            return false;
        }
    }

    public async Task ChangeAdminPasswordAsync(
        Instance instance, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(instance, cancellationToken, currentPassword);

        try
        {
            await ChangeAdminPasswordAsync(connection, newPassword, cancellationToken);
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            // The driver's exception is not kept, not even for the log: a server that rejects a
            // statement may quote it, and this statement is the one that carries a password.
            logger.LogWarning(
                "The database server of instance {InstanceId} did not change the administrator password: {ExceptionType}",
                instance.Id, exception.GetType().Name);

            throw IsTimeout(exception)
                ? new DatabaseOperationException(
                    DatabaseErrorCodes.DatabaseOperationTimeout, "The instance's database server did not respond in time.")
                : new DatabaseOperationException(
                    CredentialErrorCodes.DatabaseFailed, "The database server did not change the administrator password.");
        }

        logger.LogInformation("Changed the administrator password in the database server of instance {InstanceId}", instance.Id);
    }

    /// <summary>The administrator's own name, for the statements that need it.</summary>
    protected string AdminUser => EngineDefaults.AdminUser(Engine);

    /// <summary>
    /// Opens a connection as the administrator: with the stored password, or with
    /// <paramref name="password"/> when a rotation has to say which one.
    /// </summary>
    private async Task<DbConnection> OpenAsync(Instance instance, CancellationToken cancellationToken, string? password = null)
    {
        var endpoint = await endpoints.ResolveAsync(instance, cancellationToken);
        password ??= await secrets.GetOrCreateAdminPasswordAsync(instance.Id, cancellationToken);
        var connection = CreateConnection(BuildConnectionString(endpoint, password, options.Value));

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

            throw Translate(
                exception,
                DatabaseErrorCodes.DatabaseConnectionFailed,
                "Could not connect to the instance's database server.");
        }
    }

    private async Task<bool> ExistsAsync(DbConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ExistsSql;
        command.CommandTimeout = options.Value.CommandTimeoutSeconds;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "name";
        parameter.Value = name;
        command.Parameters.Add(parameter);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = options.Value.CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    // The driver's exception is kept as the inner exception for logs; clients only see the code and message.
    private DatabaseOperationException Translate(Exception exception, string failureCode, string failureMessage) =>
        exception switch
        {
            DatabaseOperationException known => known,
            _ when IsTimeout(exception) => new DatabaseOperationException(
                DatabaseErrorCodes.DatabaseOperationTimeout,
                "The instance's database server did not respond in time.",
                exception),
            _ => new DatabaseOperationException(failureCode, failureMessage, exception)
        };

    /// <summary>
    /// Whether <paramref name="exception"/> or anything it wraps is a <see cref="TimeoutException"/>
    /// or matches <paramref name="isDriverTimeout"/>. Drivers wrap the timeout that caused a failure.
    /// </summary>
    protected static bool WrapsTimeout(Exception exception, Func<Exception, bool> isDriverTimeout)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException || isDriverTimeout(current))
            {
                return true;
            }
        }

        return false;
    }
}
