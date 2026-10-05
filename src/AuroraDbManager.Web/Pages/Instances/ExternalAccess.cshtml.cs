using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Connectivity;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances;

/// <summary>
/// Turning an instance's external access on or off. Administrators only, by the policy the API's
/// endpoints have. Opening the page asks the question and changes nothing; only its form, a POST
/// with an antiforgery token, changes anything, and <see cref="InstanceConnectivityService"/> is
/// what does it: it picks the port, has the server restarted, and says whether that worked. The
/// page reports a change only after the service has.
/// </summary>
[Authorize(Policy = AuroraPolicies.Admin)]
public sealed class ExternalAccessModel(InstanceService instances, InstanceConnectivityService connectivity) : ResourcePageModel
{
    public InstanceResponse Instance { get; private set; } = null!;

    public InstanceConnectionResponse Connection { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken) =>
        await LoadAsync(id, cancellationToken) ? Page() : Missing("Instance");

    public Task<IActionResult> OnPostEnableAsync(Guid id, CancellationToken cancellationToken) =>
        ChangeAsync(id, enable: true, cancellationToken);

    public Task<IActionResult> OnPostDisableAsync(Guid id, CancellationToken cancellationToken) =>
        ChangeAsync(id, enable: false, cancellationToken);

    // A POST that names neither is not a request to change anything.
    public IActionResult OnPost() => BadRequest();

    private async Task<IActionResult> ChangeAsync(Guid id, bool enable, CancellationToken cancellationToken)
    {
        var result = enable
            ? await connectivity.EnableAsync(id, cancellationToken)
            : await connectivity.DisableAsync(id, cancellationToken);

        if (result.Status == ExternalAccessStatus.NotFound || !await LoadAsync(id, cancellationToken))
        {
            return Missing("Instance");
        }

        if (result.Status != ExternalAccessStatus.Changed)
        {
            return Rejected(Rejections.For(result));
        }

        // Said only now: the service has returned, so the server is running with the new configuration.
        Announce(
            StatusTone.Success,
            enable
                ? $"External access enabled on host port {result.Connection!.External.Port}. The database server was restarted."
                : "External access disabled. The database server was restarted.");
        return RedirectToPage("/Instances/Details", new { id });
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var instance = await instances.GetAsync(id, cancellationToken);
        var connection = instance is null ? null : await connectivity.GetAsync(id, cancellationToken);
        if (instance is null || connection is null)
        {
            return false;
        }

        Instance = instance;
        Connection = connection;
        return true;
    }
}
