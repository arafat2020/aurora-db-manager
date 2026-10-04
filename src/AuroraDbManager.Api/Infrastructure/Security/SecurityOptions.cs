using System.Net;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Security;

/// <summary>Settings for how the API is exposed, bound from the <c>Security</c> configuration section.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// The reverse proxies in front of Aurora, as addresses (<c>10.0.0.5</c>) or networks
    /// (<c>172.16.0.0/12</c>). Only a request that arrives from one of them has its
    /// <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> believed. Empty, the default, means
    /// there is no proxy: those headers are ignored altogether, whoever sends them.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>How many proxies in a row are in front of Aurora: how many forwarded addresses are taken into account.</summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>
    /// Whether a request over plain HTTP is redirected to HTTPS and, outside development, HTTPS
    /// responses tell clients to keep using it. Turn off only where TLS is neither terminated by
    /// Aurora nor by a proxy that says so in <c>X-Forwarded-Proto</c>.
    /// </summary>
    public bool HttpsRedirection { get; set; } = true;

    /// <summary>
    /// The largest request body accepted. Every request body of this API is a small JSON
    /// document; backups never travel through it.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = 1024 * 1024;

    public LoginRateLimitOptions LoginRateLimit { get; set; } = new();

    /// <summary>Returns what is wrong with the settings, or null if they are usable.</summary>
    public string? Validate()
    {
        foreach (var proxy in TrustedProxies)
        {
            if (!TryParseProxy(proxy, out var address, out var network))
            {
                return $"{SectionName}:TrustedProxies contains '{proxy}', which is neither an IP address nor a network in CIDR notation.";
            }

            // "Everyone" is not a proxy: it would let any client say who it is.
            if ((network is { PrefixLength: 0 }) || IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address))
            {
                return $"{SectionName}:TrustedProxies must name the proxies; '{proxy}' would trust every client.";
            }
        }

        if (ForwardLimit is < 1 or > 10)
        {
            return $"{SectionName}:ForwardLimit must be between 1 and 10.";
        }

        if (MaxRequestBodyBytes is < 1024 or > 100 * 1024 * 1024)
        {
            return $"{SectionName}:MaxRequestBodyBytes must be between 1024 and 104857600.";
        }

        if (LoginRateLimit.PermitLimit is < 1 or > 100_000)
        {
            return $"{SectionName}:LoginRateLimit:PermitLimit must be between 1 and 100000.";
        }

        if (LoginRateLimit.WindowSeconds is < 1 or > 86_400)
        {
            return $"{SectionName}:LoginRateLimit:WindowSeconds must be between 1 and 86400.";
        }

        return null;
    }

    /// <summary>Reads one entry of <see cref="TrustedProxies"/>: an address, or a network.</summary>
    public static bool TryParseProxy(string? value, out IPAddress? address, out IPNetwork? network)
    {
        address = null;
        network = null;
        value = value?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.Contains('/'))
        {
            if (IPNetwork.TryParse(value, out var parsed))
            {
                network = parsed;
                return true;
            }

            return false;
        }

        return IPAddress.TryParse(value, out address);
    }
}

/// <summary>How often one client may try to sign in, bound from <c>Security:LoginRateLimit</c>.</summary>
public sealed class LoginRateLimitOptions
{
    /// <summary>Sign-in attempts allowed per client address in one window, successful or not.</summary>
    public int PermitLimit { get; set; } = 10;

    /// <summary>The length of the window.</summary>
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>Fails startup with the exact setting that is missing or wrong.</summary>
public sealed class SecurityOptionsValidator(IConfiguration configuration) : IValidateOptions<SecurityOptions>
{
    /// <summary>The switch (<c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c>) that makes ASP.NET Core believe forwarded headers from anyone.</summary>
    public const string UnrestrictedForwardingKey = "ForwardedHeaders_Enabled";

    public ValidateOptionsResult Validate(string? name, SecurityOptions options)
    {
        if (configuration.GetValue<bool>(UnrestrictedForwardingKey))
        {
            // With it, any client could choose its own address with X-Forwarded-For and so walk
            // past the login rate limit. Refused rather than quietly accepted.
            return ValidateOptionsResult.Fail(
                "ASPNETCORE_FORWARDEDHEADERS_ENABLED trusts forwarded headers from every client and is not supported. "
                + $"Remove it and list the reverse proxies in {SecurityOptions.SectionName}:TrustedProxies instead.");
        }

        return options.Validate() is { } error ? ValidateOptionsResult.Fail(error) : ValidateOptionsResult.Success;
    }
}
