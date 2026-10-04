using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuroraDbManager.Api.Tests.Ui;

/// <summary>
/// What a browser does with the UI, as far as the tests need it: it keeps cookies, sends no
/// token of its own, does not follow redirects unless asked, and submits forms with the
/// antiforgery token the page gave it. No fake authentication: signing in is done through the
/// login form, against real users.
/// </summary>
internal static partial class Browser
{
    public const string Admin = "root-admin";
    public const string Password = "correct horse battery staple";

    /// <summary>A client that behaves like a browser that has never been here.</summary>
    public static HttpClient CreateBrowser<TProgram>(this TestHostFactory<TProgram> factory, bool followRedirects = false, string? baseAddress = null)
        where TProgram : class
    {
        var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = followRedirects, HandleCookies = true };
        if (baseAddress is not null)
        {
            options.BaseAddress = new Uri(baseAddress);
        }

        var client = factory.CreateClient(options);
        // The test host gives every client an administrator's bearer token; a browser has none.
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }

    /// <summary>The antiforgery token of the form on a page.</summary>
    public static async Task<string> AntiforgeryTokenAsync(this HttpClient browser, string path)
    {
        var html = await (await browser.GetAsync(path)).Content.ReadAsStringAsync();
        var match = AntiforgeryField().Match(html);
        Assert.True(match.Success, $"The page at {path} has no antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>Submits a form the way a browser does: its fields, and the token of the page it is on.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        this HttpClient browser, string path, IEnumerable<KeyValuePair<string, string>> fields, string? tokenFrom = null, bool withToken = true)
    {
        var form = fields.ToList();
        if (withToken)
        {
            form.Add(new("__RequestVerificationToken", await browser.AntiforgeryTokenAsync(tokenFrom ?? path)));
        }

        return await browser.PostAsync(path, new FormUrlEncodedContent(form));
    }

    /// <summary>Fills in and submits the sign-in form.</summary>
    public static Task<HttpResponseMessage> SubmitLoginAsync(this HttpClient browser, string username, string password, string path = "/login") =>
        browser.PostFormAsync(path, [new("Input.Username", username), new("Input.Password", password)], tokenFrom: "/login");

    /// <summary>Signs in and checks that it worked.</summary>
    public static async Task SignInAsync(this HttpClient browser, string username = Admin, string password = Password)
    {
        var response = await browser.SubmitLoginAsync(username, password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    public static async Task<string> GetHtmlAsync(this HttpClient browser, string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await browser.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The Set-Cookie headers of a response, by cookie name.</summary>
    public static Dictionary<string, string> SetCookies(this HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.ToDictionary(cookie => cookie[..cookie.IndexOf('=')], cookie => cookie)
            : [];

    /// <summary>The labels of the navigation entries on a page, in order.</summary>
    public static List<string> NavigationOf(string html) =>
        NavigationLink().Matches(html).Select(match => match.Groups[1].Value.Trim()).ToList();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    [GeneratedRegex("<a class=\"nav-link\"[^>]*>\\s*<svg.*?</svg>\\s*([^<]+)</a>", RegexOptions.Singleline)]
    private static partial Regex NavigationLink();
}
