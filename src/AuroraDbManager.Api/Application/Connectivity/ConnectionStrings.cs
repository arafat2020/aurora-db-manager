using System.Globalization;
using AuroraDbManager.Api.Domain.Instances;

namespace AuroraDbManager.Api.Application.Connectivity;

/// <summary>
/// Writes connection URIs. The one place that does, so that the one rule about them holds
/// everywhere: <b>a connection string never contains a password.</b> Where the password goes
/// there is a placeholder, and the person connecting puts the password in on their side. No
/// method here takes a password, so none can end up in a response, a page or a log through it.
/// </summary>
public static class ConnectionStrings
{
    /// <summary>What stands where the password goes.</summary>
    public const string PasswordPlaceholder = "<password>";

    /// <summary>What stands where the host goes when the server's configuration does not say which address clients use.</summary>
    public const string HostPlaceholder = "<server-address>";

    /// <summary>
    /// <c>postgresql://postgres:&lt;password&gt;@host:15432/app</c>, or the same with <c>mysql://root</c>.
    /// </summary>
    public static string Template(InstanceEngine engine, string? host, int port, string database) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{EngineDefaults.UriScheme(engine)}://{Uri.EscapeDataString(EngineDefaults.AdminUser(engine))}:{PasswordPlaceholder}@{HostPart(host)}:{port}/{Uri.EscapeDataString(database)}");

    // An IPv6 address is bracketed in a URI.
    private static string HostPart(string? host) =>
        string.IsNullOrWhiteSpace(host) ? HostPlaceholder : host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
}
