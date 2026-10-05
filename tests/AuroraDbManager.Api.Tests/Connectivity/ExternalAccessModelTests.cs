using AuroraDbManager.Api.Application.Connectivity;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Tests.Connectivity;

/// <summary>
/// The rules of external access where they are stated: on the instance, in the database's
/// constraints, in the server's settings, and in the one place that writes connection strings.
/// </summary>
public sealed class ExternalAccessModelTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public ExternalAccessModelTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static Instance Running()
    {
        var instance = Instance.Create("orders", InstanceEngine.Postgres, "16", 1, 512, 1, Now);
        instance.MarkRunning(Now);
        return instance;
    }

    // --- The instance --------------------------------------------------------------------------

    [Fact]
    public void NewInstance_HasNoExternalAccess_AndNoPort()
    {
        var instance = Instance.Create("orders", InstanceEngine.Postgres, "16", 1, 512, 1, Now);

        Assert.False(instance.ExternalAccessEnabled);
        Assert.Null(instance.ExternalPort);
    }

    [Fact]
    public void Enabling_TakesAPort_AndDisabling_GivesItUp()
    {
        var instance = Running();

        instance.EnableExternalAccess(15432, Now.AddMinutes(1));
        Assert.True(instance.ExternalAccessEnabled);
        Assert.Equal(15432, instance.ExternalPort);
        Assert.Equal(Now.AddMinutes(1), instance.UpdatedAt);

        instance.DisableExternalAccess(Now.AddMinutes(2));
        Assert.False(instance.ExternalAccessEnabled);
        Assert.Null(instance.ExternalPort);
        Assert.Equal(Now.AddMinutes(2), instance.UpdatedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(80)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void Enabling_NeedsAPortThatIsNotPrivileged_AndIsAPort(int port)
    {
        var instance = Running();

        Assert.Throws<ArgumentOutOfRangeException>(() => instance.EnableExternalAccess(port, Now));
        Assert.False(instance.ExternalAccessEnabled);
        Assert.Null(instance.ExternalPort);
    }

    [Fact]
    public void Enabling_IsForARunningInstance_AndNotTwice_AndDisabling_NeedsItEnabled()
    {
        var provisioning = Instance.Create("orders", InstanceEngine.Postgres, "16", 1, 512, 1, Now);
        var failed = Instance.Create("orders", InstanceEngine.Postgres, "16", 1, 512, 1, Now);
        failed.MarkFailed("PROVISIONING_FAILED", "It failed.", Now);
        var running = Running();
        running.EnableExternalAccess(15432, Now);

        Assert.Throws<InvalidOperationException>(() => provisioning.EnableExternalAccess(15432, Now));
        Assert.Throws<InvalidOperationException>(() => failed.EnableExternalAccess(15432, Now));
        Assert.Throws<InvalidOperationException>(() => running.EnableExternalAccess(15433, Now));
        Assert.Equal(15432, running.ExternalPort);
        Assert.Throws<InvalidOperationException>(() => provisioning.DisableExternalAccess(Now));
    }

    // --- The database's own guarantees ---------------------------------------------------------

    [Fact]
    public async Task InstanceCreatedThroughTheApi_IsStoredPrivate()
    {
        var (instanceId, _) = await _client.CreateInstanceAsync();

        var stored = await _factory.WithDbAsync(db => db.Instances.AsNoTracking().SingleAsync(instance => instance.Id == instanceId));

        Assert.False(stored.ExternalAccessEnabled);
        Assert.Null(stored.ExternalPort);
    }

    [Theory]
    [InlineData("external_access_enabled = 1")]
    [InlineData("external_port = 15432")]
    [InlineData("external_access_enabled = 1, external_port = 80")]
    [InlineData("external_access_enabled = 1, external_port = 70000")]
    public async Task Database_RefusesAnEnabledInstanceWithoutAPort_APortWithoutBeingEnabled_AndAPortThatIsNone(string assignment)
    {
        await _factory.CreateRunningInstanceAsync(_client);

        // Written past the application, as a bug or a hand would: the constraint is what answers.
        await Assert.ThrowsAnyAsync<Exception>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"UPDATE instances SET {assignment}")));

        var stored = await _factory.WithDbAsync(db => db.Instances.AsNoTracking().SingleAsync());
        Assert.False(stored.ExternalAccessEnabled);
        Assert.Null(stored.ExternalPort);
    }

    [Fact]
    public async Task Database_RefusesOnePortForTwoInstances()
    {
        await _factory.CreateRunningInstanceAsync(_client, name: "one");
        await _factory.CreateRunningInstanceAsync(_client, name: "two");

        await Assert.ThrowsAnyAsync<Exception>(() => _factory.WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE instances SET external_access_enabled = 1, external_port = 15432")));

        Assert.Equal(0, await _factory.WithDbAsync(db => db.Instances.CountAsync(instance => instance.ExternalAccessEnabled)));
    }

    // --- The server's settings -----------------------------------------------------------------

    [Fact]
    public void Defaults_BindToThisHostOnly_AndUseAnUnprivilegedRange()
    {
        var options = new ExternalAccessOptions();

        Assert.Null(options.Validate());
        Assert.Equal("127.0.0.1", options.NormalizedBindAddress);
        Assert.False(options.BindsEveryInterface);
        Assert.Equal("127.0.0.1", options.ClientHost);
        Assert.True(options.PortRangeStart >= 1024);
        Assert.True(options.PortRangeEnd >= options.PortRangeStart);
    }

    [Theory]
    [InlineData("0.0.0.0", null, null, true)]
    [InlineData("::", null, null, true)]
    [InlineData("0.0.0.0", "db.example.com", "db.example.com", true)]
    [InlineData("10.0.0.5", null, "10.0.0.5", false)]
    [InlineData("10.0.0.5", "db.internal", "db.internal", false)]
    [InlineData(" 127.0.0.1 ", " ", "127.0.0.1", false)]
    public void ClientHost_IsTheAdvertisedHost_OrTheBindAddressIfItNamesOne(string bind, string? advertised, string? host, bool everyInterface)
    {
        var options = new ExternalAccessOptions { BindAddress = bind, AdvertisedHost = advertised };

        Assert.Null(options.Validate());
        Assert.Equal(host, options.ClientHost);
        Assert.Equal(everyInterface, options.BindsEveryInterface);
    }

    [Theory]
    [InlineData("", null, 15432, 16432, "BindAddress")]
    [InlineData("localhost", null, 15432, 16432, "BindAddress")]
    [InlineData("127.0.0.1; rm -rf /", null, 15432, 16432, "BindAddress")]
    [InlineData("127.0.0.1", "not a host!", 15432, 16432, "AdvertisedHost")]
    [InlineData("127.0.0.1", "db.example.com/?x=<script>", 15432, 16432, "AdvertisedHost")]
    [InlineData("127.0.0.1", null, 1023, 16432, "PortRange")]
    [InlineData("127.0.0.1", null, 80, 443, "PortRange")]
    [InlineData("127.0.0.1", null, 15432, 65536, "PortRange")]
    [InlineData("127.0.0.1", null, 16432, 15432, "PortRange")]
    public void Settings_AreChecked_AndNeverAllowAPrivilegedPort(string bind, string? advertised, int start, int end, string setting)
    {
        var options = new ExternalAccessOptions { BindAddress = bind, AdvertisedHost = advertised, PortRangeStart = start, PortRangeEnd = end };

        Assert.Contains(setting, options.Validate(), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidSettings_StopTheApplicationFromStarting()
    {
        using var factory = new ApiFactory { ConfigureExternalAccess = options => options.PortRangeStart = 22 };

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ExternalAccess:PortRangeStart", exception.ToString(), StringComparison.Ordinal);
    }

    // --- Connection strings --------------------------------------------------------------------

    [Theory]
    [InlineData(InstanceEngine.Postgres, "127.0.0.1", 15432, "app", "postgresql://postgres:<password>@127.0.0.1:15432/app")]
    [InlineData(InstanceEngine.Mysql, "db.example.com", 15433, "orders_2026", "mysql://root:<password>@db.example.com:15433/orders_2026")]
    [InlineData(InstanceEngine.Postgres, "2001:db8::1", 15432, "app", "postgresql://postgres:<password>@[2001:db8::1]:15432/app")]
    [InlineData(InstanceEngine.Postgres, null, 15432, "app", "postgresql://postgres:<password>@<server-address>:15432/app")]
    public void ConnectionString_IsATemplate_WithAPlaceholderWhereThePasswordGoes(
        InstanceEngine engine, string? host, int port, string database, string expected)
    {
        Assert.Equal(expected, ConnectionStrings.Template(engine, host, port, database));
    }

    [Fact]
    public void ConnectionStrings_CannotBeGivenAPassword()
    {
        // No method that writes a connection string has a parameter a password could arrive through.
        var parameters = typeof(ConnectionStrings).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.Name!)
            .ToList();

        Assert.Equal(["engine", "host", "port", "database"], parameters);
    }

    [Fact]
    public void EngineDefaults_AreTheEnginesOwnPortAndAdministrator()
    {
        Assert.Equal((5432, "postgres"), (EngineDefaults.Port(InstanceEngine.Postgres), EngineDefaults.AdminUser(InstanceEngine.Postgres)));
        Assert.Equal((3306, "root"), (EngineDefaults.Port(InstanceEngine.Mysql), EngineDefaults.AdminUser(InstanceEngine.Mysql)));
    }
}
