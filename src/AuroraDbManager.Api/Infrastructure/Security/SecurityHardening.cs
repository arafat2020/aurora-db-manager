using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using AuroraDbManager.Api.Errors;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Api.Infrastructure.Security;

/// <summary>
/// How the API is exposed to the network: which proxies are believed, how often a client may try
/// to sign in, how large a request may be, and which headers every response carries. Small,
/// targeted protections built from what ASP.NET Core provides; nothing here is a framework.
/// </summary>
public static class SecurityHardening
{
    /// <summary>The rate-limiting policy of the login endpoint, and of nothing else.</summary>
    public const string LoginRateLimitPolicy = "login";

    public static IServiceCollection AddAuroraSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SecurityOptions>()
            .Bind(configuration.GetSection(SecurityOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SecurityOptions>, SecurityOptionsValidator>();

        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<SecurityOptions>>((forwarded, security) =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            forwarded.ForwardLimit = security.Value.ForwardLimit;

            // Exactly the configured proxies: not even the loopback default.
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            foreach (var proxy in security.Value.TrustedProxies)
            {
                if (!SecurityOptions.TryParseProxy(proxy, out var address, out var network))
                {
                    continue;
                }

                if (address is not null)
                {
                    forwarded.KnownProxies.Add(address);
                }
                else
                {
                    forwarded.KnownIPNetworks.Add(network!.Value);
                }
            }
        });

        services.AddOptions<KestrelServerOptions>().Configure<IOptions<SecurityOptions>>((kestrel, security) =>
        {
            // Says nothing about what serves the API.
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = security.Value.MaxRequestBodyBytes;
        });

        services.AddRateLimiter(limiter =>
        {
            limiter.OnRejected = RejectAsync;
            limiter.AddPolicy(LoginRateLimitPolicy, context =>
            {
                var limit = context.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.LoginRateLimit;
                return RateLimitPartition.GetFixedWindowLimiter(ClientKey(context.Connection.RemoteIpAddress), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limit.PermitLimit,
                    Window = TimeSpan.FromSeconds(limit.WindowSeconds),
                    // Refused at once: an attempt that waits in a queue is still an attempt.
                    QueueLimit = 0
                });
            });
        });

        return services;
    }

    /// <summary>
    /// Makes the connection's address and scheme those the trusted proxy reports. Does nothing,
    /// and reads no forwarded header at all, unless proxies are configured. First in the
    /// pipeline, so everything after it, the rate limiter included, sees the real client.
    /// </summary>
    public static void UseAuroraForwardedHeaders(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<SecurityOptions>>().Value.TrustedProxies.Count > 0)
        {
            app.UseForwardedHeaders();
        }
    }

    /// <summary>Puts the same few headers on every response, errors included.</summary>
    public static void UseAuroraSecurityHeaders(this WebApplication app) => app.Use((context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            // A response is what its content type says, and is never guessed to be something that runs.
            headers.XContentTypeOptions = "nosniff";
            // Nothing this API returns is a page: it loads nothing, and nothing may frame it.
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            headers.XFrameOptions = "DENY";
            headers.Append("Referrer-Policy", "no-referrer");
            // Responses describe the infrastructure and are for the caller who was authorized, not for a cache.
            if (headers.CacheControl.Count == 0)
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });
        return next(context);
    });

    /// <summary>
    /// Refuses a request whose body is larger than <see cref="SecurityOptions.MaxRequestBodyBytes"/>
    /// before any of it is read. A body of unknown length is cut off by the server at the same limit.
    /// </summary>
    public static void UseAuroraRequestLimits(this WebApplication app) => app.Use(async (context, next) =>
    {
        var limit = context.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.MaxRequestBodyBytes;
        if (context.Request.ContentLength > limit)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await ErrorHandling.WriteStatusCodeBodyAsync(context);
            return;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = limit;
        }

        await next(context);
    });

    /// <summary>HTTP to HTTPS, and outside development the instruction to stay there, unless turned off.</summary>
    public static void UseAuroraHttps(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<SecurityOptions>>().Value.HttpsRedirection)
        {
            return;
        }

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseHttpsRedirection();
    }

    /// <summary>
    /// What identifies a client for rate limiting: its address as the connection, or a trusted
    /// proxy, reports it. An IPv6 client is its /64, since one client typically has that whole
    /// network to choose addresses from. Clients whose address is unknown share one bucket.
    /// </summary>
    public static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    private static async ValueTask RejectAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        // When to try again, and nothing else about the limiter: not the limit, not what is left.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SecurityHardening).FullName!)
            .LogWarning("Login rate limit exceeded for client {ClientAddress}", ClientKey(http.Connection.RemoteIpAddress));

        await ErrorHandling.WriteStatusCodeBodyAsync(http);
    }
}
