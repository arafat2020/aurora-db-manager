using System.Text.Json.Serialization;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Application.Connectivity;

/// <param name="InstanceId">The instance.</param>
/// <param name="Engine">Database engine: <c>postgres</c> or <c>mysql</c>.</param>
/// <param name="Username">The administrator account of the instance's server. Its password is never part of any response.</param>
/// <param name="Internal">Where the server is for containers on the instance network. Always there.</param>
/// <param name="External">Whether the server's port is published on the host, and where.</param>
public sealed record InstanceConnectionResponse(
    Guid InstanceId,
    InstanceEngine Engine,
    string Username,
    InternalEndpointResponse Internal,
    ExternalAccessResponse External);

/// <param name="Network">The Docker network a client has to be attached to.</param>
/// <param name="Host">The name the server answers to on that network.</param>
/// <param name="Port">The engine's own port.</param>
public sealed record InternalEndpointResponse(string Network, string Host, int Port);

/// <param name="Enabled">Whether the instance's database port is published on the Docker host.</param>
/// <param name="Host">
/// The host to connect to: the server's advertised host, or the address ports are bound to.
/// Left out while disabled, and when ports are bound on every interface and no host is configured.
/// </param>
/// <param name="Port">The host port the database port is published on. Left out while disabled.</param>
/// <param name="BindAddress">
/// The address of the Docker host that ports are bound to: <c>127.0.0.1</c> is this host only,
/// <c>0.0.0.0</c> every interface. Whether a published port can be reached from elsewhere also
/// depends on firewalls and networks, which Aurora neither configures nor knows.
/// </param>
public sealed record ExternalAccessResponse(
    bool Enabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Host,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Port,
    string BindAddress);

/// <param name="DatabaseId">The database.</param>
/// <param name="InstanceId">The instance it is in.</param>
/// <param name="Database">The database's name.</param>
/// <param name="Engine">Database engine: <c>postgres</c> or <c>mysql</c>.</param>
/// <param name="Username">The administrator account of the instance's server. Its password is never part of any response.</param>
/// <param name="Internal">Where the server is for containers on the instance network.</param>
/// <param name="External">Whether the server's port is published on the host, and where.</param>
/// <param name="ConnectionStrings">
/// Connection URIs for the database, with the literal placeholder <c>&lt;password&gt;</c> where the password goes.
/// </param>
public sealed record DatabaseConnectionResponse(
    Guid DatabaseId,
    Guid InstanceId,
    string Database,
    InstanceEngine Engine,
    string Username,
    InternalEndpointResponse Internal,
    ExternalAccessResponse External,
    ConnectionStringsResponse ConnectionStrings);

/// <param name="Internal">For a client on the instance network.</param>
/// <param name="External">For a client that reaches the published port. Left out while external access is disabled.</param>
public sealed record ConnectionStringsResponse(
    string Internal,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? External);
