using System.Net;
using System.Net.Sockets;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Databases;
using AuroraDbManager.Api.Tests.Docker;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AuroraDbManager.Api.Tests.Databases;

/// <summary>
/// The PostgreSQL and MySQL managers without a database server. Most tests run the real manager
/// against a <see cref="FakeSqlServer"/>; the connection-failure and timeout tests run it with its
/// real driver against local sockets that refuse or never answer.
/// </summary>
public sealed class DatabaseManagerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class StubEndpointResolver(InstanceEndpoint endpoint) : IInstanceEndpointResolver
    {
        public DatabaseOperationException? Failure { get; set; }

        public List<Guid> ResolvedInstanceIds { get; } = [];

        public Task<InstanceEndpoint> ResolveAsync(Instance instance, CancellationToken cancellationToken)
        {
            ResolvedInstanceIds.Add(instance.Id);
            return Failure is null ? Task.FromResult(endpoint) : Task.FromException<InstanceEndpoint>(Failure);
        }
    }

    /// <summary>A manager of one engine with everything around it, on a fake server unless told otherwise.</summary>
    private sealed class Harness
    {
        public Harness(InstanceEngine engine, InstanceEndpoint? endpoint = null, int connectTimeoutSeconds = 10, bool realDriver = false)
        {
            Engine = engine;
            Server = new FakeSqlServer(engine == InstanceEngine.Postgres ? '"' : '`');
            Endpoints = new StubEndpointResolver(endpoint ?? new InstanceEndpoint("10.20.30.40", engine == InstanceEngine.Postgres ? 5432 : 3306));
            Instance = Instance.Create("db", engine, engine == InstanceEngine.Postgres ? "16" : "8.4", 1, 1024, 20, Now);

            var options = Options.Create(new DatabaseManagerOptions { ConnectTimeoutSeconds = connectTimeoutSeconds, CommandTimeoutSeconds = 5 });
            Func<string, System.Data.Common.DbConnection>? factory = realDriver ? null : Server.Connect;
            Manager = engine == InstanceEngine.Postgres
                ? new PostgreSqlDatabaseManager(Endpoints, Secrets, options, PostgresLog, factory)
                : new MySqlDatabaseManager(Endpoints, Secrets, options, MysqlLog, factory);
        }

        public InstanceEngine Engine { get; }

        public FakeSqlServer Server { get; }

        public StubEndpointResolver Endpoints { get; }

        public InMemoryInstanceSecretStore Secrets { get; } = new();

        public RecordingLogger<PostgreSqlDatabaseManager> PostgresLog { get; } = new();

        public RecordingLogger<MySqlDatabaseManager> MysqlLog { get; } = new();

        public Instance Instance { get; }

        public IDatabaseManager Manager { get; }

        public IEnumerable<string> LogEntries => PostgresLog.Entries.Concat(MysqlLog.Entries);

        public Database NewDatabase(string name = "app") => Database.Create(Instance.Id, name, Now);

        public Task CreateAsync(Database database, CancellationToken cancellationToken = default) =>
            Manager.CreateDatabaseAsync(Instance, database, cancellationToken);

        public Task DeleteAsync(Database database, CancellationToken cancellationToken = default) =>
            Manager.DeleteDatabaseAsync(Instance, database, cancellationToken);

        public string Quote(string name) => Engine == InstanceEngine.Postgres
            ? $"\"{name.Replace("\"", "\"\"")}\""
            : $"`{name.Replace("`", "``")}`";

        public string CreateStatement(string name) => Engine == InstanceEngine.Postgres
            ? $"CREATE DATABASE {Quote(name)}"
            : $"CREATE DATABASE IF NOT EXISTS {Quote(name)}";

        public string DropStatement(string name) => Engine == InstanceEngine.Postgres
            ? $"DROP DATABASE IF EXISTS {Quote(name)} WITH (FORCE)"
            : $"DROP DATABASE IF EXISTS {Quote(name)}";
    }

    /// <summary>A database whose name never went through validation, as if validation had a hole.</summary>
    private static Database DatabaseNamed(Harness harness, string name)
    {
        var database = harness.NewDatabase();
        typeof(Database).GetProperty(nameof(Database.Name))!.SetValue(database, name);
        return database;
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public void Engine_IsTheManagersOwn(InstanceEngine engine)
    {
        Assert.Equal(engine, new Harness(engine).Manager.Engine);
    }

    // --- Create -------------------------------------------------------------------------------

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task Create_NewDatabase_ChecksThenCreatesItWithAQuotedIdentifier(InstanceEngine engine)
    {
        var harness = new Harness(engine);

        await harness.CreateAsync(harness.NewDatabase("app"));

        Assert.Equal(["app"], harness.Server.Databases);
        Assert.Equal(2, harness.Server.Statements.Count);
        Assert.StartsWith("SELECT 1 FROM ", harness.Server.Statements[0]);
        Assert.Equal(harness.CreateStatement("app"), harness.Server.Statements[1]);
        Assert.Equal(0, harness.Server.OpenConnections);
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task Create_DatabaseAlreadyExists_IsAdoptedWithoutAnotherCreate(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        harness.Server.Databases.Add("app");

        await harness.CreateAsync(harness.NewDatabase("app"));

        Assert.Equal(["app"], harness.Server.Databases);
        Assert.StartsWith("SELECT 1 FROM ", Assert.Single(harness.Server.Statements));
        Assert.Contains(harness.LogEntries, entry => entry.Contains("already exists"));
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task Create_Twice_LeavesOneDatabase(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        var database = harness.NewDatabase("app");

        await harness.CreateAsync(database);
        await harness.CreateAsync(database);

        Assert.Equal(["app"], harness.Server.Databases);
        Assert.Single(harness.Server.Statements, statement => statement.StartsWith("CREATE DATABASE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("42P04")]
    [InlineData("23505")]
    public async Task Postgres_Create_ServerReportsTheDatabaseAsExistingAfterTheCheck_IsSuccess(string sqlState)
    {
        var harness = new Harness(InstanceEngine.Postgres);
        // Another attempt's CREATE DATABASE lands between this one's check and its statement.
        harness.Server.StatementFailure = sql => sql.StartsWith("CREATE", StringComparison.Ordinal)
            ? new PostgresException("database \"app\" already exists", "ERROR", "ERROR", sqlState)
            : null;

        await harness.CreateAsync(harness.NewDatabase("app"));

        Assert.Equal(2, harness.Server.Statements.Count);
    }

    // --- Delete -------------------------------------------------------------------------------

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task Delete_ExistingDatabase_DropsOnlyThatDatabase(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        harness.Server.Databases.UnionWith(["app", "analytics"]);

        await harness.DeleteAsync(harness.NewDatabase("app"));

        Assert.Equal(["analytics"], harness.Server.Databases);
        Assert.Equal(harness.DropStatement("app"), harness.Server.Statements[^1]);
        Assert.Equal(0, harness.Server.OpenConnections);
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task Delete_DatabaseAlreadyAbsent_IsSuccessWithoutADrop(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        harness.Server.Databases.Add("analytics");

        await harness.DeleteAsync(harness.NewDatabase("app"));

        Assert.Equal(["analytics"], harness.Server.Databases);
        Assert.StartsWith("SELECT 1 FROM ", Assert.Single(harness.Server.Statements));
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task Delete_Twice_Succeeds(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        var database = harness.NewDatabase("app");
        await harness.CreateAsync(database);

        await harness.DeleteAsync(database);
        await harness.DeleteAsync(database);

        Assert.Empty(harness.Server.Databases);
        Assert.Single(harness.Server.Statements, statement => statement.StartsWith("DROP DATABASE", StringComparison.Ordinal));
    }

    // --- Identifier handling ------------------------------------------------------------------

    [Theory]
    [InlineData(InstanceEngine.Postgres, "app\"; DROP DATABASE \"postgres")]
    [InlineData(InstanceEngine.Postgres, "we\"\"ird")]
    [InlineData(InstanceEngine.Postgres, "semi;colon -- comment")]
    [InlineData(InstanceEngine.Mysql, "app`; DROP DATABASE `mysql")]
    [InlineData(InstanceEngine.Mysql, "we``ird")]
    [InlineData(InstanceEngine.Mysql, "semi;colon -- comment")]
    public async Task HostileName_IsQuotedAsOneIdentifier_AndNeverBecomesSql(InstanceEngine engine, string name)
    {
        var harness = new Harness(engine);
        harness.Server.Databases.Add(engine == InstanceEngine.Postgres ? "postgres" : "mysql");
        var database = DatabaseNamed(harness, name);

        await harness.CreateAsync(database);

        // One database, named exactly as given; the system database it tried to drop is untouched.
        Assert.Equal(2, harness.Server.Databases.Count);
        Assert.Contains(name, harness.Server.Databases);
        Assert.Equal(harness.CreateStatement(name), harness.Server.Statements[^1]);

        await harness.DeleteAsync(database);

        Assert.Single(harness.Server.Databases);
        Assert.Equal(harness.DropStatement(name), harness.Server.Statements[^1]);
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task ExistenceCheck_PassesTheNameAsAParameter(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        var database = DatabaseNamed(harness, "x' OR '1'='1");

        // The fake server itself asserts that the name is a parameter and absent from the statement.
        await harness.CreateAsync(database);

        Assert.DoesNotContain("OR", harness.Server.Statements[0]);
        Assert.Contains("@name", harness.Server.Statements[0]);
    }

    // --- Connection ---------------------------------------------------------------------------

    [Theory]
    [InlineData(InstanceEngine.Postgres, "postgres", 5432)]
    [InlineData(InstanceEngine.Mysql, "root", 3306)]
    public async Task Connects_ToTheResolvedEndpoint_AsTheAdministrator_WithTheStoredPassword_Unpooled(
        InstanceEngine engine, string adminUser, int port)
    {
        var harness = new Harness(engine);
        var password = await harness.Secrets.GetOrCreateAdminPasswordAsync(harness.Instance.Id, default);

        await harness.CreateAsync(harness.NewDatabase());

        Assert.Equal([harness.Instance.Id], harness.Endpoints.ResolvedInstanceIds);
        var connection = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = Assert.Single(harness.Server.ConnectionStrings) };
        Assert.Equal("10.20.30.40", connection[engine == InstanceEngine.Postgres ? "Host" : "Server"]);
        Assert.Equal(port.ToString(), connection["Port"].ToString());
        Assert.Equal(adminUser, connection[engine == InstanceEngine.Postgres ? "Username" : "User ID"]);
        Assert.Equal(password, connection["Password"]);
        Assert.Equal("false", connection["Pooling"].ToString(), ignoreCase: true);
        // No password was generated here: the manager used the instance's existing one.
        Assert.Equal(1, harness.Secrets.CreatedCount);
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task EndpointCannotBeResolved_FailsAsEngineUnavailable_WithoutConnecting(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        harness.Endpoints.Failure = new DatabaseOperationException(
            DatabaseErrorCodes.DatabaseEngineUnavailable, "The instance's database server is not running.");

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() => harness.CreateAsync(harness.NewDatabase()));

        Assert.Equal("DATABASE_ENGINE_UNAVAILABLE", exception.Code);
        Assert.Empty(harness.Server.ConnectionStrings);
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task ConnectionFails_IsReportedSafely_WithoutThePasswordAnywhere(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        var password = await harness.Secrets.GetOrCreateAdminPasswordAsync(harness.Instance.Id, default);
        harness.Server.OpenFailure = new IOException("raw-driver-detail: connection reset by peer");

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() => harness.DeleteAsync(harness.NewDatabase()));

        Assert.Equal("DATABASE_CONNECTION_FAILED", exception.Code);
        Assert.Equal("Could not connect to the instance's database server.", exception.Message);
        Assert.Same(harness.Server.OpenFailure, exception.InnerException);
        Assert.DoesNotContain(password, exception.ToString());
        Assert.DoesNotContain("10.20.30.40", exception.Message);
        Assert.DoesNotContain(harness.LogEntries, entry => entry.Contains(password));
        Assert.Empty(harness.Server.Statements);
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task ConnectionTimesOut_IsReportedAsTimeout(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        harness.Server.OpenFailure = new InvalidOperationException("driver wrapper", new TimeoutException("raw-driver-detail"));

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() => harness.CreateAsync(harness.NewDatabase()));

        Assert.Equal("DATABASE_OPERATION_TIMEOUT", exception.Code);
        Assert.DoesNotContain("raw-driver-detail", exception.Message);
    }

    // --- Statement failures -------------------------------------------------------------------

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task Create_StatementFails_IsReportedAsCreateFailed_WithoutDriverDetails_AndIsNotRetriedHere(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        var failure = new IOException("raw-driver-detail: permission denied to create database");
        harness.Server.StatementFailure = sql => sql.StartsWith("CREATE", StringComparison.Ordinal) ? failure : null;

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() => harness.CreateAsync(harness.NewDatabase()));

        Assert.Equal("DATABASE_CREATE_FAILED", exception.Code);
        Assert.Equal("The database could not be created.", exception.Message);
        Assert.Same(failure, exception.InnerException);
        // One attempt: retrying is the job's business.
        Assert.Single(harness.Server.Statements, statement => statement.StartsWith("CREATE", StringComparison.Ordinal));
        Assert.Equal(0, harness.Server.OpenConnections);
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task Delete_StatementFails_IsReportedAsDeleteFailed_AndTheDatabaseIsStillThere(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        harness.Server.Databases.Add("app");
        harness.Server.StatementFailure = sql => sql.StartsWith("DROP", StringComparison.Ordinal) ? new IOException("raw-driver-detail") : null;

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() => harness.DeleteAsync(harness.NewDatabase("app")));

        Assert.Equal("DATABASE_DELETE_FAILED", exception.Code);
        Assert.Equal("The database could not be deleted.", exception.Message);
        Assert.Equal(["app"], harness.Server.Databases);
        Assert.Equal(0, harness.Server.OpenConnections);
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task StatementTimesOut_IsReportedAsTimeout(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        harness.Server.StatementFailure = sql => sql.StartsWith("CREATE", StringComparison.Ordinal)
            ? new InvalidOperationException("driver wrapper", new TimeoutException("raw-driver-detail"))
            : null;

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() => harness.CreateAsync(harness.NewDatabase()));

        Assert.Equal("DATABASE_OPERATION_TIMEOUT", exception.Code);
    }

    [Fact]
    public async Task Postgres_StatementCancelledByTheServersTimeout_IsReportedAsTimeout()
    {
        var harness = new Harness(InstanceEngine.Postgres);
        harness.Server.Databases.Add("app");
        harness.Server.StatementFailure = sql => sql.StartsWith("DROP", StringComparison.Ordinal)
            ? new PostgresException("canceling statement due to statement timeout", "ERROR", "ERROR", "57014")
            : null;

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() => harness.DeleteAsync(harness.NewDatabase("app")));

        Assert.Equal("DATABASE_OPERATION_TIMEOUT", exception.Code);
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task Cancellation_IsNotTurnedIntoAFailure(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        using var cancellation = new CancellationTokenSource();
        harness.Server.StatementFailure = _ =>
        {
            cancellation.Cancel();
            return new OperationCanceledException(cancellation.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.CreateAsync(harness.NewDatabase(), cancellation.Token));

        Assert.Equal(0, harness.Server.OpenConnections);
    }

    // --- The real drivers, against sockets that are not database servers ----------------------

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task RealDriver_NothingListensAtTheEndpoint_FailsAsConnectionFailed(InstanceEngine engine)
    {
        var harness = new Harness(engine, new InstanceEndpoint("127.0.0.1", UnusedPort()), realDriver: true);
        var password = await harness.Secrets.GetOrCreateAdminPasswordAsync(harness.Instance.Id, default);

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() => harness.CreateAsync(harness.NewDatabase()));

        Assert.Equal("DATABASE_CONNECTION_FAILED", exception.Code);
        Assert.Equal("Could not connect to the instance's database server.", exception.Message);
        Assert.NotNull(exception.InnerException);
        Assert.DoesNotContain(password, exception.ToString());
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task RealDriver_ServerAcceptsButNeverAnswers_FailsAsTimeoutWithinTheConnectTimeout(InstanceEngine engine)
    {
        // Accepts TCP connections and then says nothing, like a server that hangs.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        var harness = new Harness(engine, new InstanceEndpoint("127.0.0.1", port), connectTimeoutSeconds: 1, realDriver: true);

        var started = DateTime.UtcNow;
        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(() => harness.CreateAsync(harness.NewDatabase()));

        Assert.Equal("DATABASE_OPERATION_TIMEOUT", exception.Code);
        Assert.Equal("The instance's database server did not respond in time.", exception.Message);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.Zero, TimeSpan.FromSeconds(15));
    }

    private static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
