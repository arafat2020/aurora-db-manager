using System.Net;
using System.Net.Http.Json;
using AuroraDbManager.Api.Domain.Users;
using AuroraDbManager.Api.Infrastructure.Security;
using static AuroraDbManager.Api.Tests.ApiClientExtensions;

namespace AuroraDbManager.Api.Tests.Security;

/// <summary>
/// The limit on sign-in attempts per client address, with ASP.NET Core's real rate limiter and
/// real forwarded-header handling. The test server has no sockets, so a test names the address a
/// request arrives from with <see cref="ApiFactory.RemoteAddressHeader"/>; that stands for the TCP
/// connection, which a client cannot choose. <c>X-Forwarded-For</c> is what a client can choose.
/// </summary>
public sealed class LoginRateLimitTests : IDisposable
{
    private const string Admin = "root-admin";
    private const string Password = "correct horse battery staple";
    private const string Proxy = "10.0.0.5";

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }
    }

    private (ApiFactory Factory, HttpClient Anonymous) Application(int permits = 3, int windowSeconds = 60, params string[] trustedProxies)
    {
        var factory = new ApiFactory
        {
            BootstrapAdmin = (Admin, Password),
            ConfigureSecurity = options =>
            {
                options.LoginRateLimit.PermitLimit = permits;
                options.LoginRateLimit.WindowSeconds = windowSeconds;
                options.TrustedProxies = [.. trustedProxies];
            }
        };
        var client = factory.CreateAnonymousClient();
        _disposables.Add(factory);
        _disposables.Add(client);
        return (factory, client);
    }

    private static Task<HttpResponseMessage> AttemptAsync(
        HttpClient client, string password, string? from = null, string? forwardedFor = null, string username = Admin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, LoginUrl) { Content = JsonContent.Create(new { username, password }) };
        if (from is not null)
        {
            request.Headers.Add(ApiFactory.RemoteAddressHeader, from);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return client.SendAsync(request);
    }

    private static async Task ExhaustAsync(HttpClient client, int attempts, string? from = null, string? forwardedFor = null)
    {
        for (var i = 0; i < attempts; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await AttemptAsync(client, $"wrong-password-{i}", from, forwardedFor)).StatusCode);
        }
    }

    // --- The limit ----------------------------------------------------------------------------

    [Fact]
    public async Task RepeatedFailedAttempts_AreEventuallyRefused_With429_InTheStandardErrorFormat()
    {
        var (_, client) = Application(permits: 3);
        await ExhaustAsync(client, 3);

        var response = await AttemptAsync(client, "wrong-password-again");

        var error = await response.AssertErrorAsync(HttpStatusCode.TooManyRequests, "TOO_MANY_REQUESTS");
        Assert.Equal(["code", "message"], error.EnumerateObject().Select(property => property.Name).Order());
        Assert.Single(response.Headers.GetValues("X-Request-Id"));
    }

    [Fact]
    public async Task Refusal_SaysWhenToTryAgain_AndNothingElseAboutTheLimiter()
    {
        var (_, client) = Application(permits: 2, windowSeconds: 30);
        await ExhaustAsync(client, 2);

        var response = await AttemptAsync(client, "wrong-password-again");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.InRange(int.Parse(Assert.Single(response.Headers.GetValues("Retry-After"))), 1, 30);

        // Not the limit, not what is left of it, not the window.
        var headers = response.Headers.Concat(response.Content.Headers).Select(header => header.Key).ToList();
        Assert.DoesNotContain(headers, name => name.Contains("RateLimit", StringComparison.OrdinalIgnoreCase));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("permit", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("window", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnceRefused_EvenTheRightPasswordIsRefused_SoTheLimitCannotBeUsedToTestPasswords()
    {
        var (_, client) = Application(permits: 3);
        await ExhaustAsync(client, 3);

        var right = await AttemptAsync(client, Password);
        var wrong = await AttemptAsync(client, "wrong-password-again");

        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await right.Content.ReadAsStringAsync());
        Assert.DoesNotContain("accessToken", await right.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulSignIn_WithinTheLimit_Works_AlsoAfterAFewMistakes()
    {
        var (_, client) = Application(permits: 5);
        await ExhaustAsync(client, 2);

        var response = await AttemptAsync(client, Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AfterTheWindow_TheClientCanSignInAgain()
    {
        var (_, client) = Application(permits: 2, windowSeconds: 1);
        await ExhaustAsync(client, 2);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await AttemptAsync(client, Password)).StatusCode);

        // The real limiter and the real clock: wait the window out.
        HttpStatusCode status;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            status = (await AttemptAsync(client, Password)).StatusCode;
        }
        while (status == HttpStatusCode.TooManyRequests && DateTime.UtcNow < deadline);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task ManagementTraffic_HasNoPartInTheLoginLimit_InEitherDirection()
    {
        var (factory, client) = Application(permits: 2);
        using var admin = factory.CreateClientAs(UserRole.Admin);

        // Plenty of ordinary requests do not use up sign-in attempts...
        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(InstancesUrl)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await AttemptAsync(client, Password)).StatusCode);

        // ...and a client that is refused at login is still served everywhere else.
        await ExhaustAsync(client, 1);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await AttemptAsync(client, Password)).StatusCode);
        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(JobsUrl)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(HealthUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ReadinessUrl)).StatusCode);
    }

    [Fact]
    public async Task Refusal_IsLogged_WithTheClientAddress_AndNoPassword()
    {
        var (factory, client) = Application(permits: 1);
        await ExhaustAsync(client, 1, from: "203.0.113.50");

        await AttemptAsync(client, "the-password-that-was-refused", from: "203.0.113.50");

        Assert.Contains(factory.Logs.Entries, entry =>
            entry.Contains("Warning", StringComparison.Ordinal)
            && entry.Contains("Login rate limit exceeded for client 203.0.113.50", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains("the-password-that-was-refused", StringComparison.Ordinal));
    }

    // --- Per client ---------------------------------------------------------------------------

    [Fact]
    public async Task EachClientAddress_HasItsOwnLimit()
    {
        var (_, client) = Application(permits: 2);
        await ExhaustAsync(client, 2, from: "203.0.113.10");

        Assert.Equal(HttpStatusCode.TooManyRequests, (await AttemptAsync(client, Password, from: "203.0.113.10")).StatusCode);
        // Someone else, somewhere else, is not affected.
        Assert.Equal(HttpStatusCode.OK, (await AttemptAsync(client, Password, from: "203.0.113.11")).StatusCode);
    }

    [Fact]
    public async Task LimitIsPerClient_NotPerUsername_SoGuessingAcrossAccountsIsLimitedToo()
    {
        var (_, client) = Application(permits: 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await AttemptAsync(client, "a-common-password-1", username: $"user-{i}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await AttemptAsync(client, "a-common-password-1", username: "user-3")).StatusCode);
    }

    [Fact]
    public async Task Ipv6Client_IsLimitedAsItsNetwork_NotPerAddressItCanPick()
    {
        var (_, client) = Application(permits: 2);
        await AttemptAsync(client, "wrong-password-1", from: "2001:db8:1:2::1");
        await AttemptAsync(client, "wrong-password-2", from: "2001:db8:1:2::2");

        // A third address of the same /64 is the same client; another network is not.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await AttemptAsync(client, Password, from: "2001:db8:1:2:ffff:ffff:ffff:ffff")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AttemptAsync(client, Password, from: "2001:db8:1:3::1")).StatusCode);
    }

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("::1", "::/64")]
    [InlineData(null, "unknown")]
    public void ClientKey_IsTheAddress_OrItsNetworkForIpv6(string? address, string expected)
    {
        Assert.Equal(expected, SecurityHardening.ClientKey(address is null ? null : IPAddress.Parse(address)));
    }

    // --- Forwarded headers --------------------------------------------------------------------

    [Fact]
    public async Task WithoutTrustedProxies_ForwardedFor_IsIgnored_AndCannotBeUsedToEscapeTheLimit()
    {
        var (_, client) = Application(permits: 3);

        // One client, claiming to be a different one every time.
        for (var i = 0; i < 3; i++)
        {
            await AttemptAsync(client, $"wrong-password-{i}", from: "198.51.100.7", forwardedFor: $"203.0.113.{i + 1}");
        }

        var response = await AttemptAsync(client, Password, from: "198.51.100.7", forwardedFor: "203.0.113.99");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task UntrustedClient_CannotSpoofItsAddress_EvenWhenAProxyIsConfigured()
    {
        var (factory, client) = Application(permits: 3, trustedProxies: Proxy);

        // Not the proxy: what it says about where the request came from is not believed.
        for (var i = 0; i < 3; i++)
        {
            await AttemptAsync(client, $"wrong-password-{i}", from: "198.51.100.7", forwardedFor: $"203.0.113.{i + 1}");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await AttemptAsync(client, Password, from: "198.51.100.7", forwardedFor: "203.0.113.99")).StatusCode);
        Assert.Contains(factory.Logs.Entries, entry => entry.Contains("Login rate limit exceeded for client 198.51.100.7", StringComparison.Ordinal));
        // And it could not use up anyone else's attempts either.
        Assert.Equal(HttpStatusCode.OK, (await AttemptAsync(client, Password, from: Proxy, forwardedFor: "203.0.113.1")).StatusCode);
    }

    [Fact]
    public async Task TrustedProxy_ReportsTheClient_AndEachClientBehindItHasItsOwnLimit()
    {
        var (factory, client) = Application(permits: 2, trustedProxies: Proxy);
        await ExhaustAsync(client, 2, from: Proxy, forwardedFor: "203.0.113.20");

        // The client the proxy names is limited; the proxy's other clients are not.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await AttemptAsync(client, Password, from: Proxy, forwardedFor: "203.0.113.20")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AttemptAsync(client, Password, from: Proxy, forwardedFor: "203.0.113.21")).StatusCode);
        Assert.Contains(factory.Logs.Entries, entry => entry.Contains("Login rate limit exceeded for client 203.0.113.20", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClientBehindATrustedProxy_CannotSpoofByPrependingAddresses()
    {
        var (_, client) = Application(permits: 2, trustedProxies: Proxy);

        // The proxy appends the address it saw; whatever the client put in front of it is not used.
        for (var i = 0; i < 2; i++)
        {
            await AttemptAsync(client, $"wrong-password-{i}", from: Proxy, forwardedFor: $"192.0.2.{i + 1}, 203.0.113.30");
        }

        var response = await AttemptAsync(client, Password, from: Proxy, forwardedFor: "192.0.2.99, 203.0.113.30");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task TrustedProxies_CanBeANetwork()
    {
        var (_, client) = Application(permits: 1, trustedProxies: "172.16.0.0/12");
        await ExhaustAsync(client, 1, from: "172.20.1.9", forwardedFor: "203.0.113.40");

        Assert.Equal(HttpStatusCode.TooManyRequests, (await AttemptAsync(client, Password, from: "172.20.1.9", forwardedFor: "203.0.113.40")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AttemptAsync(client, Password, from: "172.20.1.9", forwardedFor: "203.0.113.41")).StatusCode);
        // An address outside the network is an ordinary client, whatever it claims.
        await ExhaustAsync(client, 1, from: "172.32.0.1", forwardedFor: "203.0.113.42");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await AttemptAsync(client, Password, from: "172.32.0.1", forwardedFor: "203.0.113.43")).StatusCode);
    }
}
