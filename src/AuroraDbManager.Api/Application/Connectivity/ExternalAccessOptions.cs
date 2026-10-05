using System.Net;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Application.Connectivity;

/// <summary>
/// How instances are made reachable from outside the Docker network, bound from the
/// <c>ExternalAccess</c> configuration section. These are the server's settings: no request can
/// choose a bind address or a port. Nothing here exposes anything by itself; an instance's port
/// is published only once an administrator enables external access for that instance.
/// </summary>
public sealed class ExternalAccessOptions
{
    public const string SectionName = "ExternalAccess";

    /// <summary>
    /// The address of the Docker host that published database ports are bound to.
    /// <c>127.0.0.1</c> (the default) is reachable from the host itself only; a private interface's
    /// address from that network; <c>0.0.0.0</c> from every network the host is on. Whether a
    /// bound port can actually be reached is then up to the host's firewall and network, which
    /// Aurora does not configure.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// The host name or address clients are told to connect to. Empty for the bind address, which
    /// is right whenever that is a specific address; needed when it is <c>0.0.0.0</c>.
    /// </summary>
    public string? AdvertisedHost { get; set; }

    /// <summary>First host port that may be given to an instance.</summary>
    public int PortRangeStart { get; set; } = 15432;

    /// <summary>Last host port that may be given to an instance.</summary>
    public int PortRangeEnd { get; set; } = 16432;

    /// <summary>The bind address as Docker reports it back.</summary>
    public string NormalizedBindAddress => IPAddress.Parse(BindAddress.Trim()).ToString();

    /// <summary>Whether ports are bound on every interface of the host.</summary>
    public bool BindsEveryInterface => IPAddress.Parse(BindAddress.Trim()) is var address
        && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any));

    /// <summary>
    /// The host clients connect to: the advertised one, or the bind address if it names one
    /// address. Null when ports are bound on every interface and no host is advertised: the
    /// server has an address, but this configuration does not say which.
    /// </summary>
    public string? ClientHost =>
        !string.IsNullOrWhiteSpace(AdvertisedHost) ? AdvertisedHost.Trim() : BindsEveryInterface ? null : NormalizedBindAddress;

    /// <summary>Returns what is wrong with the settings, or null if they are usable.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(BindAddress) || !IPAddress.TryParse(BindAddress.Trim(), out _))
        {
            return $"{SectionName}:BindAddress must be an IP address of the Docker host, such as 127.0.0.1.";
        }

        if (PortRangeStart < Instance.MinExternalPort || PortRangeEnd > Instance.MaxExternalPort || PortRangeStart > PortRangeEnd)
        {
            return $"{SectionName}:PortRangeStart and {SectionName}:PortRangeEnd must be a range within {Instance.MinExternalPort} to {Instance.MaxExternalPort}.";
        }

        // Shown to clients as a host, and put into connection strings: a host name or an address, nothing else.
        if (!string.IsNullOrWhiteSpace(AdvertisedHost)
            && (AdvertisedHost.Trim().Length > 253 || Uri.CheckHostName(AdvertisedHost.Trim()) == UriHostNameType.Unknown))
        {
            return $"{SectionName}:AdvertisedHost must be a host name or an IP address, or empty.";
        }

        return null;
    }
}

/// <summary>Fails startup with the exact setting that is missing or wrong.</summary>
public sealed class ExternalAccessOptionsValidator : IValidateOptions<ExternalAccessOptions>
{
    public ValidateOptionsResult Validate(string? name, ExternalAccessOptions options) =>
        options.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
}
