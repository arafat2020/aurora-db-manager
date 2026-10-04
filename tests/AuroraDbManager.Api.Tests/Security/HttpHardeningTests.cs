using System.Net;
using System.Net.Http.Json;
using System.Text;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Security;

/// <summary>
/// How the API presents itself over HTTP: the headers on every response, what it does not say
/// about itself, CORS (none), the request size limit, HTTPS, and the settings it refuses to start with.
/// </summary>
public sealed class HttpHardeningTests : IDisposable
{
    private const string Proxy = "10.0.0.5";

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly HttpClient _anonymous;
    private readonly List<IDisposable> _disposables = [];

    public HttpHardeningTests()
    {
        _admin = _factory.CreateClientAs(UserRole.Admin);
        _anonymous = _factory.CreateAnonymousClient();
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _anonymous.Dispose();
        _admin.Dispose();
        _factory.Dispose();
    }

    private ApiFactory Production(Action<SecurityOptions>? configure = null)
    {
        var factory = new ApiFactory { EnvironmentName = "Production", ConfigureSecurity = configure };
        _disposables.Add(factory);
        return factory;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : null;

    // --- Response headers ---------------------------------------------------------------------

    public static TheoryData<string, string, string, int> Responses => new()
    {
        { "admin", "GET", "/api/v1/instances", 200 },
        { "admin", "GET", "/api/v1/monitoring/summary", 200 },
        { "admin", "GET", $"/api/v1/instances/{Guid.Empty}", 404 },
        { "admin", "POST", "/api/v1/instances", 400 },
        { "admin", "GET", "/api/v1/no-such-thing", 404 },
        { "anonymous", "GET", "/api/v1/instances", 401 },
        { "anonymous", "POST", "/api/v1/auth/login", 400 },
        { "anonymous", "GET", "/health", 200 },
        { "anonymous", "GET", "/health/ready", 200 }
    };

    [Theory]
    [MemberData(nameof(Responses))]
    public async Task EveryResponse_SuccessOrError_CarriesTheSecurityHeaders(string who, string method, string url, int expectedStatus)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { });
        }

        var response = await (who == "admin" ? _admin : _anonymous).SendAsync(request);

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("default-src 'none'; frame-ancestors 'none'", Header(response, "Content-Security-Policy"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        // The health checks say "no-store, no-cache" themselves; everything else gets "no-store".
        Assert.Contains("no-store", Header(response, "Cache-Control"), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Responses))]
    public async Task NoResponse_SaysWhatSoftwareServesIt(string who, string method, string url, int expectedStatus)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { });
        }

        var response = await (who == "admin" ? _admin : _anonymous).SendAsync(request);

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        foreach (var name in new[] { "Server", "X-Powered-By", "X-AspNet-Version", "X-AspNetMvc-Version", "X-SourceFiles" })
        {
            Assert.Null(Header(response, name));
        }
    }

    [Fact]
    public void Server_IsToldNotToNameItself_AndToCutOffLargeBodies()
    {
        // The test server is not Kestrel; what Kestrel is configured with is checked here, and
        // exercised over a real socket in the smoke test described in docs/security.md.
        var kestrel = _factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.False(kestrel.AddServerHeader);
        Assert.Equal(1024 * 1024, kestrel.Limits.MaxRequestBodySize);
    }

    // --- CORS ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:3000")]
    [InlineData("null")]
    public async Task Cors_IsNotEnabled_NoOriginIsEverAllowed(string origin)
    {
        using var simple = new HttpRequestMessage(HttpMethod.Get, InstancesUrl);
        simple.Headers.Add("Origin", origin);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, InstancesUrl);
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization, content-type");

        var simpleResponse = await _admin.SendAsync(simple);
        var preflightResponse = await _anonymous.SendAsync(preflight);

        // The request itself is served: CORS is the browser's rule, and the browser gets no permission.
        Assert.Equal(HttpStatusCode.OK, simpleResponse.StatusCode);
        Assert.False(preflightResponse.IsSuccessStatusCode);
        foreach (var response in new[] { simpleResponse, preflightResponse })
        {
            Assert.DoesNotContain(
                response.Headers.Select(header => header.Key),
                name => name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
        }
    }

    // --- Request size -------------------------------------------------------------------------

    [Fact]
    public async Task RequestBodyOverTheLimit_IsRefusedWith413_BeforeItIsRead_WhoeverSendsIt()
    {
        var oversized = new string('x', 1024 * 1024);
        var body = $"{{\"name\":\"{oversized}\",\"engine\":\"postgres\",\"version\":\"16\",\"cpu\":1,\"memoryMb\":512,\"storageGb\":1}}";

        var asAdmin = await _admin.PostAsync(InstancesUrl, new StringContent(body, Encoding.UTF8, "application/json"));
        var atLogin = await _anonymous.PostAsync(LoginUrl, new StringContent(body, Encoding.UTF8, "application/json"));

        await asAdmin.AssertErrorAsync(HttpStatusCode.RequestEntityTooLarge, "REQUEST_TOO_LARGE");
        await atLogin.AssertErrorAsync(HttpStatusCode.RequestEntityTooLarge, "REQUEST_TOO_LARGE");
        Assert.DoesNotContain(oversized[..64], await asAdmin.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestBodyUnderTheLimit_IsHandledNormally_EvenWhenItIsLargeForThisApi()
    {
        // Half a megabyte of JSON: far more than any real request, and under the limit.
        var padding = new string(' ', 512 * 1024);
        var body = $"{{{padding}\"name\":\"production-db\",\"engine\":\"postgres\",\"version\":\"16\",\"cpu\":1,\"memoryMb\":512,\"storageGb\":1}}";

        var response = await _admin.PostAsync(InstancesUrl, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData(413, "REQUEST_TOO_LARGE", true)]
    [InlineData(400, "REQUEST_FAILED", false)]
    public async Task BodyTheServerCutsOffWhileItIsRead_IsTheClientsError_NotAServerError(int status, string code, bool wrapped)
    {
        // What Kestrel throws from the middle of a read when a body of undeclared length passes
        // the limit; the test server has no such limit, so the handler is given the exception itself.
        Exception error = new Microsoft.AspNetCore.Http.BadHttpRequestException("Request body too large. raw-server-detail", status);
        if (wrapped)
        {
            error = new InvalidOperationException("reading the body failed", new IOException("wrapped", error));
        }

        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = _factory.Services };
        context.Response.Body = new MemoryStream();
        context.Response.StatusCode = 500;
        context.Features.Set<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>(
            new Microsoft.AspNetCore.Diagnostics.ExceptionHandlerFeature { Error = error });

        await Errors.ErrorHandling.WriteExceptionBodyAsync(context);

        Assert.Equal(status, context.Response.StatusCode);
        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Contains(code, body, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-server-detail", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnyOtherUnhandledException_StaysAnInternalError()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = _factory.Services };
        context.Response.Body = new MemoryStream();
        context.Response.StatusCode = 500;
        context.Features.Set<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>(
            new Microsoft.AspNetCore.Diagnostics.ExceptionHandlerFeature { Error = new InvalidOperationException("raw-server-detail") });

        await Errors.ErrorHandling.WriteExceptionBodyAsync(context);

        Assert.Equal(500, context.Response.StatusCode);
        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Contains("INTERNAL_ERROR", body, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-server-detail", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestSizeLimit_IsConfigurable()
    {
        using var factory = new ApiFactory { ConfigureSecurity = options => options.MaxRequestBodyBytes = 2048 };
        using var admin = factory.CreateClientAs(UserRole.Admin);

        var small = await admin.PostAsJsonAsync(InstancesUrl, ValidInstanceRequest());
        var large = await admin.PostAsync(InstancesUrl, new StringContent(new string(' ', 4096) + "{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, small.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        Assert.Equal(2048, factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize);
    }

    // --- HTTPS --------------------------------------------------------------------------------

    private static HttpRequestMessage Get(string url, string? from = null, string? forwardedProto = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (from is not null)
        {
            request.Headers.Add(ApiFactory.RemoteAddressHeader, from);
        }

        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }

        return request;
    }

    [Fact]
    public async Task InProduction_HttpsResponses_TellClientsToStayOnHttps()
    {
        var factory = Production();
        using var https = factory.CreateDefaultClient(new Uri("https://aurora.example.test"));
        using var http = factory.CreateDefaultClient(new Uri("http://aurora.example.test"));

        var secure = await https.GetAsync(HealthUrl);
        var plain = await http.GetAsync(HealthUrl);

        Assert.StartsWith("max-age=", Header(secure, "Strict-Transport-Security"));
        // Only ever said over HTTPS itself.
        Assert.Null(Header(plain, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task InDevelopment_NoStrictTransportSecurity_SoLocalWorkIsNotPinnedToHttps()
    {
        using var https = _factory.CreateDefaultClient(new Uri("https://aurora.example.test"));

        var response = await https.GetAsync(HealthUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Header(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task HttpsRedirectionTurnedOff_NeitherRedirectsNorPins()
    {
        var factory = Production(options => options.HttpsRedirection = false);
        using var https = factory.CreateDefaultClient(new Uri("https://aurora.example.test"));

        var response = await https.GetAsync(HealthUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Header(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task TrustedProxy_SayingTheClientUsedHttps_IsBelieved_AndAnyoneElseSayingSoIsNot()
    {
        var factory = Production(options => options.TrustedProxies = [Proxy]);
        using var client = factory.CreateDefaultClient(new Uri("http://aurora.example.test"));

        // TLS ended at the proxy: for Aurora the request is an HTTPS one.
        var viaProxy = await client.SendAsync(Get(HealthUrl, from: Proxy, forwardedProto: "https"));
        // A client talking plain HTTP directly, and claiming otherwise.
        var direct = await client.SendAsync(Get(HealthUrl, from: "198.51.100.7", forwardedProto: "https"));

        Assert.StartsWith("max-age=", Header(viaProxy, "Strict-Transport-Security"));
        Assert.Null(Header(direct, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task WithoutTrustedProxies_ForwardedProto_IsIgnored()
    {
        var factory = Production();
        using var client = factory.CreateDefaultClient(new Uri("http://aurora.example.test"));

        var response = await client.SendAsync(Get(HealthUrl, from: Proxy, forwardedProto: "https"));

        Assert.Null(Header(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task InProduction_TheOpenApiDocument_IsNotServed()
    {
        var factory = Production();
        using var admin = factory.CreateClientAs(UserRole.Admin);
        using var anonymous = factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/openapi/v1.json")).StatusCode);
        // Development serves it, to anyone who can reach a development server.
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task InProduction_AnUnhandledFailure_IsTheGenericError_NotADiagnosticsPage()
    {
        var factory = Production();
        using var admin = factory.CreateClientAs(UserRole.Admin);
        await factory.WithDbAsync(db => Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database, "DROP TABLE jobs"));

        foreach (var client in new[] { admin, _admin })
        {
            if (client == _admin)
            {
                await _factory.WithDbAsync(db => Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database, "DROP TABLE jobs"));
            }

            var response = await client.GetAsync(JobsUrl);

            // The same in development: the developer page never replaces the API's own error.
            var error = await response.AssertErrorAsync(HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
            Assert.Equal("An unexpected error occurred.", error.GetProperty("message").GetString());
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("jobs", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SQLite", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
        }
    }

    // --- Settings the application will not start with ------------------------------------------

    [Fact]
    public void UnrestrictedForwardedHeaders_AreRefusedAtStartup_WithAMessageThatSaysWhatToUseInstead()
    {
        // What ASPNETCORE_FORWARDEDHEADERS_ENABLED=true sets: forwarded headers believed from anyone.
        using var factory = new ApiFactory { HostSettings = new Dictionary<string, string> { ["ForwardedHeaders_Enabled"] = "true" } };

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("Security:TrustedProxies", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("*")]
    [InlineData("proxy.internal")]
    [InlineData("10.0.0.5/40")]
    [InlineData("")]
    public void TrustedProxies_ThatWouldTrustEveryone_OrAreNotAddresses_AreRefused(string proxy)
    {
        var options = new SecurityOptions { TrustedProxies = [proxy] };

        Assert.Contains("Security:TrustedProxies", options.Validate(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.0/12")]
    [InlineData("::1")]
    [InlineData("fd00::/8")]
    [InlineData(" 127.0.0.1 ")]
    public void TrustedProxies_AddressesAndNetworks_AreAccepted(string proxy)
    {
        Assert.Null(new SecurityOptions { TrustedProxies = [proxy] }.Validate());
    }

    [Fact]
    public void Defaults_TrustNoProxy_LimitLoginAttempts_AndRequestSize_AndKeepHttpsRedirection()
    {
        var options = new SecurityOptions();

        Assert.Null(options.Validate());
        Assert.Empty(options.TrustedProxies);
        Assert.Equal(1, options.ForwardLimit);
        Assert.True(options.HttpsRedirection);
        Assert.Equal(1024 * 1024, options.MaxRequestBodyBytes);
        Assert.Equal(10, options.LoginRateLimit.PermitLimit);
        Assert.Equal(60, options.LoginRateLimit.WindowSeconds);
    }

    [Fact]
    public void OutOfRangeSettings_AreRefused()
    {
        Assert.Contains("ForwardLimit", new SecurityOptions { ForwardLimit = 0 }.Validate(), StringComparison.Ordinal);
        Assert.Contains("MaxRequestBodyBytes", new SecurityOptions { MaxRequestBodyBytes = 10 }.Validate(), StringComparison.Ordinal);
        Assert.Contains("MaxRequestBodyBytes", new SecurityOptions { MaxRequestBodyBytes = long.MaxValue }.Validate(), StringComparison.Ordinal);
        Assert.Contains("PermitLimit", new SecurityOptions { LoginRateLimit = { PermitLimit = 0 } }.Validate(), StringComparison.Ordinal);
        Assert.Contains("WindowSeconds", new SecurityOptions { LoginRateLimit = { WindowSeconds = 0 } }.Validate(), StringComparison.Ordinal);
    }
}
