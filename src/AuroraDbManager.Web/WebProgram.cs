using AuroraDbManager.Api;
using AuroraDbManager.Api.Infrastructure.Security;
using AuroraDbManager.Web.Authentication;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AuroraDbManager.Web;

/// <summary>
/// The UI host: the Razor Pages administration UI, and with it the whole of Aurora. It is built
/// from the same pieces as the API host (<see cref="AuroraHost"/>): the application services run
/// in this process, the pages call them directly, and the REST API is served next to the pages,
/// so that one process is a complete installation. Pages are signed in to with a cookie, the API
/// with a bearer token; see <see cref="WebAuthentication"/>.
/// </summary>
public sealed class WebProgram
{
    /// <summary>
    /// What a page may load: styles, scripts, images and fonts from this host and nowhere else,
    /// no inline script or style, forms that post only here, and no framing.
    /// </summary>
    public const string PageContentSecurityPolicy =
        "default-src 'none'; style-src 'self'; script-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; "
        + "form-action 'self'; base-uri 'none'; frame-ancestors 'none'";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAuroraCore(builder.Configuration);
        builder.Services.AddAuroraApi(defaultScheme: WebAuthentication.Scheme).AddAuroraCookie();

        builder.Services.AddOptions<WebOptions>()
            .Bind(builder.Configuration.GetSection(WebOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<WebOptions>, WebOptionsValidator>();

        builder.Services.AddRazorPages();
        builder.Services.AddScoped<DashboardReader>();
        builder.Services.AddScoped<BackupActivity>();
        builder.Services.AddScoped<MonitoringReader>();
        builder.Services.AddOptions<AntiforgeryOptions>()
            .Configure<IOptions<SecurityOptions>, IHostEnvironment>((antiforgery, security, environment) =>
            {
                var httpsOnly = security.Value.HttpsRedirection && !environment.IsDevelopment();
                antiforgery.Cookie.Name = httpsOnly ? "__Host-aurora.antiforgery" : "aurora.antiforgery";
                antiforgery.Cookie.HttpOnly = true;
                antiforgery.Cookie.SecurePolicy = httpsOnly ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                antiforgery.Cookie.SameSite = SameSiteMode.Strict;
                antiforgery.FormFieldName = "__RequestVerificationToken";
            });

        // The message a change leaves for the page it leads to (see Flash) travels in this cookie.
        builder.Services.AddOptions<CookieTempDataProviderOptions>()
            .Configure<IOptions<SecurityOptions>, IHostEnvironment>((tempData, security, environment) =>
            {
                var httpsOnly = security.Value.HttpsRedirection && !environment.IsDevelopment();
                tempData.Cookie.Name = httpsOnly ? "__Host-aurora.flash" : "aurora.flash";
                tempData.Cookie.HttpOnly = true;
                tempData.Cookie.SecurePolicy = httpsOnly ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                tempData.Cookie.SameSite = SameSiteMode.Strict;
                tempData.Cookie.IsEssential = true;
                tempData.Cookie.Path = "/";
            });

        var app = builder.Build();

        // The API's responses allow nothing, as before; a page gets a policy that fits a page.
        app.UseAuroraEdge(context => WebAuthentication.IsApiRequest(context) ? SecurityHardening.ApiContentSecurityPolicy : PageContentSecurityPolicy);

        // Failures are answered in the language of whoever asked: JSON for the API, a page for a browser.
        app.UseWhen(WebAuthentication.IsApiRequest, api => api.UseAuroraApiErrors());
        app.UseWhen(context => !WebAuthentication.IsApiRequest(context), pages =>
        {
            pages.UseExceptionHandler("/error/500");
            pages.UseStatusCodePagesWithReExecute("/error/{0}");
        });

        // Styles and scripts: public, and cacheable because their addresses change when they do.
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = file => file.Context.Response.Headers.CacheControl =
                file.Context.Request.Query.ContainsKey("v") ? "public, max-age=31536000, immutable" : "public, max-age=3600"
        });

        // Routing is placed here, after the error handling above, on purpose: an error page is
        // shown by running the rest of the pipeline again for another address, and that only
        // finds the page if the address is routed again.
        app.UseRouting();

        app.UseAuroraRequestPipeline();
        app.MapAuroraApi();
        app.MapRazorPages();

        app.Run();
    }
}
