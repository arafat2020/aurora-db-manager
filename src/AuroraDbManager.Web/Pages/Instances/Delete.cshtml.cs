using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances;

/// <summary>
/// Deleting an instance. Administrators only. Opening the page asks the question and changes
/// nothing; only its form, a POST with an antiforgery token, deletes. Whether the instance can be
/// deleted now is <see cref="InstanceService.DeleteAsync"/>'s to say, and what it says is shown.
/// </summary>
[Authorize(Policy = AuroraPolicies.Admin)]
public sealed class DeleteModel(InstanceService instances, DatabaseService databases) : ResourcePageModel
{
    public InstanceResponse Instance { get; private set; } = null!;

    /// <summary>How many databases go with the instance.</summary>
    public int DatabaseCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken) =>
        await LoadAsync(id, cancellationToken) ? Page() : Missing("Instance");

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return Missing("Instance");
        }

        DeleteInstanceResult result;
        try
        {
            result = await instances.DeleteAsync(id, cancellationToken);
        }
        catch (InstanceProvisioningException exception)
        {
            return Rejected(Rejections.For(exception));
        }

        if (result == DeleteInstanceResult.NotFound)
        {
            return Missing("Instance");
        }

        if (Rejections.For(result) is { } rejection)
        {
            return Rejected(rejection);
        }

        Announce(StatusTone.Success, $"Instance “{Instance.Name}” was deleted.");
        return RedirectToPage("/Instances/Index");
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var instance = await instances.GetAsync(id, cancellationToken);
        var list = instance is null ? null : await databases.ListAsync(id, new ListDatabasesQuery { PageSize = 1 }, cancellationToken);
        if (instance is null || list is null)
        {
            return false;
        }

        Instance = instance;
        DatabaseCount = list.TotalCount;
        return true;
    }
}
