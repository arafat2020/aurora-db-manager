using System.Security.Cryptography;
using System.Text;
using AuroraDbManager.Api.Application.Credentials;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Databases;
using AuroraDbManager.Api.Tests.Databases;
using AuroraDbManager.Api.Tests.Docker;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AuroraDbManager.Api.Tests.Credentials;

/// <summary>
/// How the PostgreSQL and MySQL managers change and check the administrator's password, without
/// a database server: the real managers on a <see cref="FakeSqlServer"/> that records what it is
/// sent. What the real engines make of these statements is for the Docker integration tests.
/// </summary>
public sealed class AdminCredentialManagerTests
{
    private const string Current = "CurrentPasswordOfTheAdministrator01";
    private const string Replacement = "ReplacementPasswordForTheAdmin0002";

    private sealed class Endpoint : IInstanceEndpointResolver
    {
        public Task<InstanceEndpoint> ResolveAsync(Instance instance, CancellationToken cancellationToken) =>
            Task.FromResult(new InstanceEndpoint("10.20.30.40", EngineDefaults.Port(instance.Engine)));
    }

    private sealed class Harness
    {
        public Harness(InstanceEngine engine)
        {
            Instance = Instance.Create("db", engine, engine == InstanceEngine.Postgres ? "16" : "8.4", 1, 1024, 20, DateTime.UtcNow);
            var options = Options.Create(new DatabaseManagerOptions());
            Manager = engine == InstanceEngine.Postgres
                ? new PostgreSqlDatabaseManager(new Endpoint(), new InMemoryInstanceSecretStore(), options, PostgresLog, Server.Connect)
                : new MySqlDatabaseManager(new Endpoint(), new InMemoryInstanceSecretStore(), options, MysqlLog, Server.Connect);
        }

        public FakeSqlServer Server { get; } = new('"', permissive: true);

        public Instance Instance { get; }

        public IAdminCredentialManager Manager { get; }

        public RecordingLogger<PostgreSqlDatabaseManager> PostgresLog { get; } = new();

        public RecordingLogger<MySqlDatabaseManager> MysqlLog { get; } = new();

        public IEnumerable<string> Log => PostgresLog.Entries.Concat(MysqlLog.Entries);
    }

    // --- PostgreSQL ---------------------------------------------------------------------------

    [Fact]
    public async Task Postgres_Change_SendsAScramVerifier_AndNeverThePassword()
    {
        var harness = new Harness(InstanceEngine.Postgres);

        await harness.Manager.ChangeAdminPasswordAsync(harness.Instance, Current, Replacement, default);

        var statement = Assert.Single(harness.Server.Statements);
        Assert.Matches(
            "^ALTER ROLE \"postgres\" PASSWORD 'SCRAM-SHA-256\\$4096:[A-Za-z0-9+/]{22}==\\$[A-Za-z0-9+/]{43}=:[A-Za-z0-9+/]{43}='$",
            statement);
        Assert.DoesNotContain(Replacement, statement, StringComparison.Ordinal);
        Assert.DoesNotContain(Current, statement, StringComparison.Ordinal);
        // It connected as the administrator, with the password it was told is the current one.
        Assert.Contains("Username=postgres", Assert.Single(harness.Server.ConnectionStrings), StringComparison.Ordinal);
        Assert.Contains(Current, harness.Server.ConnectionStrings[0], StringComparison.Ordinal);
        Assert.Equal(0, harness.Server.OpenConnections);
        Assert.DoesNotContain(harness.Log, entry => entry.Contains(Replacement, StringComparison.Ordinal) || entry.Contains(Current, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Postgres_Change_UsesAFreshSaltEveryTime()
    {
        var harness = new Harness(InstanceEngine.Postgres);

        await harness.Manager.ChangeAdminPasswordAsync(harness.Instance, Current, Replacement, default);
        await harness.Manager.ChangeAdminPasswordAsync(harness.Instance, Current, Replacement, default);

        Assert.NotEqual(harness.Server.Statements[0], harness.Server.Statements[1]);
    }

    [Fact]
    public void ScramVerifier_IsTheOneOfRfc7677()
    {
        // The exchange of RFC 7677, section 3: user "user", password "pencil".
        var salt = Convert.FromBase64String("W22ZaJ0SNY7soEsUEjb6gQ==");
        const string AuthMessage =
            "n=user,r=rOprNGfwEbeRWgbNEkqO,"
            + "r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096,"
            + "c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0";

        var verifier = ScramSha256Verifier.Create("pencil", salt);

        var parts = verifier.Split('$');
        Assert.Equal("SCRAM-SHA-256", parts[0]);
        Assert.Equal("4096:W22ZaJ0SNY7soEsUEjb6gQ==", parts[1]);
        var serverKey = Convert.FromBase64String(parts[2].Split(':')[1]);
        var storedKey = Convert.FromBase64String(parts[2].Split(':')[0]);
        // A server holding this verifier signs the exchange with the signature the RFC gives...
        Assert.Equal(
            "6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=",
            Convert.ToBase64String(HMACSHA256.HashData(serverKey, Encoding.UTF8.GetBytes(AuthMessage))));
        // ...and accepts the client proof the RFC gives: proof XOR signature is the client key, whose hash is the stored key.
        var proof = Convert.FromBase64String("dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=");
        var signature = HMACSHA256.HashData(storedKey, Encoding.UTF8.GetBytes(AuthMessage));
        var clientKey = proof.Zip(signature, (left, right) => (byte)(left ^ right)).ToArray();
        Assert.Equal(storedKey, SHA256.HashData(clientKey));
    }

    [Fact]
    public async Task Postgres_Authenticates_IsFalseForARefusedPassword_AndTrueOtherwise()
    {
        var harness = new Harness(InstanceEngine.Postgres);

        Assert.True(await harness.Manager.AuthenticatesAsync(harness.Instance, Current, default));

        harness.Server.OpenFailure = new PostgresException("password authentication failed for user \"postgres\"", "FATAL", "FATAL", "28P01");
        Assert.False(await harness.Manager.AuthenticatesAsync(harness.Instance, Current, default));

        // Checking is a connection and nothing else: no statement was sent.
        Assert.Empty(harness.Server.Statements);
        Assert.Equal(0, harness.Server.OpenConnections);
    }

    // --- MySQL --------------------------------------------------------------------------------

    [Fact]
    public async Task Mysql_Change_ChangesEveryRootAccountInOneStatement_WithThePasswordAsAParameter()
    {
        var harness = new Harness(InstanceEngine.Mysql);
        harness.Server.Rows = _ => ["%", "localhost"];

        await harness.Manager.ChangeAdminPasswordAsync(harness.Instance, Current, Replacement, default);

        Assert.Equal(
            [
                "SELECT Host FROM mysql.user WHERE User = @user ORDER BY Host",
                "ALTER USER 'root'@'%' IDENTIFIED BY @password, 'root'@'localhost' IDENTIFIED BY @password"
            ],
            harness.Server.Statements);
        Assert.Equal("root", harness.Server.Parameters[0]["user"]);
        Assert.Equal(Replacement, harness.Server.Parameters[1]["password"]);
        Assert.All(harness.Server.Statements, statement => Assert.DoesNotContain(Replacement, statement, StringComparison.Ordinal));
        Assert.Contains("User ID=root", Assert.Single(harness.Server.ConnectionStrings), StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Log, entry => entry.Contains(Replacement, StringComparison.Ordinal) || entry.Contains(Current, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mysql_Change_WritesAHostTheServerReported_AsALiteral_WhateverIsInIt()
    {
        var harness = new Harness(InstanceEngine.Mysql);
        harness.Server.Rows = _ => ["10.0.0.%' IDENTIFIED BY 'x"];

        await harness.Manager.ChangeAdminPasswordAsync(harness.Instance, Current, Replacement, default);

        Assert.Equal(@"ALTER USER 'root'@'10.0.0.%\' IDENTIFIED BY \'x' IDENTIFIED BY @password", harness.Server.Statements[1]);
    }

    [Fact]
    public async Task Mysql_Change_WithNoAdministratorAccount_Fails_AndChangesNothing()
    {
        var harness = new Harness(InstanceEngine.Mysql);

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(
            () => harness.Manager.ChangeAdminPasswordAsync(harness.Instance, Current, Replacement, default));

        Assert.Equal("CREDENTIAL_ROTATION_DATABASE_FAILED", exception.Code);
        Assert.Single(harness.Server.Statements);
    }

    // --- Both ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task Change_ThatTheServerRejects_IsReportedWithoutWhatTheServerSaid(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        harness.Server.Rows = _ => ["%"];
        // A server quoting the statement back, password and all, as a syntax error does.
        harness.Server.StatementFailure = sql => sql.StartsWith("ALTER", StringComparison.Ordinal)
            ? new InvalidOperationException($"You have an error in your SQL syntax near 'IDENTIFIED BY '{Replacement}''")
            : null;

        var exception = await Assert.ThrowsAsync<DatabaseOperationException>(
            () => harness.Manager.ChangeAdminPasswordAsync(harness.Instance, Current, Replacement, default));

        Assert.Equal("CREDENTIAL_ROTATION_DATABASE_FAILED", exception.Code);
        Assert.Equal("The database server did not change the administrator password.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.False(exception.ToString().Contains(Replacement, StringComparison.Ordinal), "The exception carries the password.");
        Assert.False(harness.Log.Any(entry => entry.Contains(Replacement, StringComparison.Ordinal)), "The log carries the password.");
        Assert.Contains(harness.Log, entry => entry.Contains("did not change the administrator password: InvalidOperationException", StringComparison.Ordinal));
        Assert.Equal(0, harness.Server.OpenConnections);
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task ServerThatCannotBeReached_IsAFailure_NotARefusedPassword(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        harness.Server.OpenFailure = new IOException("raw-driver-detail: connection refused");

        var checking = await Assert.ThrowsAsync<DatabaseOperationException>(
            () => harness.Manager.AuthenticatesAsync(harness.Instance, Current, default));
        var changing = await Assert.ThrowsAsync<DatabaseOperationException>(
            () => harness.Manager.ChangeAdminPasswordAsync(harness.Instance, Current, Replacement, default));

        Assert.Equal("DATABASE_CONNECTION_FAILED", checking.Code);
        Assert.Equal("DATABASE_CONNECTION_FAILED", changing.Code);
        Assert.Empty(harness.Server.Statements);
    }

    [Theory]
    [InlineData(InstanceEngine.Postgres)]
    [InlineData(InstanceEngine.Mysql)]
    public async Task Cancellation_IsNotTurnedIntoAFailure(InstanceEngine engine)
    {
        var harness = new Harness(engine);
        using var cancellation = new CancellationTokenSource();
        harness.Server.Rows = _ => ["%"];
        harness.Server.StatementFailure = _ =>
        {
            cancellation.Cancel();
            return new OperationCanceledException(cancellation.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Manager.ChangeAdminPasswordAsync(harness.Instance, Current, Replacement, cancellation.Token));
    }
}
