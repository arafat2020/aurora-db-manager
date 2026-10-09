using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Credentials;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances;

/// <summary>
/// Asking for the password of an instance's database administrator to be rotated. Operators and
/// administrators, by the policy the API's endpoint has. Opening the page asks the question and
/// changes nothing; only its form, a POST with an antiforgery token, does, and all it does is ask
/// <see cref="CredentialRotationService"/>, which records a job. The form has no fields: the
/// password is Aurora's to generate, and this page never has it. The one page that does is
/// <see cref="RotatePasswordResultModel"/>, once, after the job has completed.
/// </summary>
[Authorize(Policy = AuroraPolicies.Operator)]
public sealed class RotatePasswordModel(InstanceService instances, CredentialRotationService credentials) : ResourcePageModel
{
    public InstanceResponse Instance { get; private set; } = null!;

    public InstanceCredentialResponse Credential { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken) =>
        await LoadAsync(id, cancellationToken) ? Page() : Missing("Instance");

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await credentials.RequestAsync(id, cancellationToken);
        if (result.Status == RotateCredentialStatus.NotFound || !await LoadAsync(id, cancellationToken))
        {
            return Missing("Instance");
        }

        if (result.Status != RotateCredentialStatus.Accepted)
        {
            return Rejected(Rejections.For(result.Status));
        }

        // Started, not done: the job's page says how it went.
        Announce(StatusTone.Progress, "Password rotation started. The new password can be shown here once it has finished.");
        return RedirectToPage("/Jobs/Details", new { id = result.Operation!.Job.Id });
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var instance = await instances.GetAsync(id, cancellationToken);
        var credential = instance is null ? null : await credentials.GetAsync(id, cancellationToken);
        if (instance is null || credential is null)
        {
            return false;
        }

        Instance = instance;
        Credential = credential;
        return true;
    }
}
