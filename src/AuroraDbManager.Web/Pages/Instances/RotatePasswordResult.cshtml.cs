using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Credentials;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances;

/// <summary>
/// The one page that shows an instance's password: the new one, of a rotation that has completed,
/// once. Operators and administrators, by the policy the API's endpoint has. It is shown in the
/// answer to a POST with an antiforgery token and in nothing else: opening the address shows
/// nothing and leads back to the job, and sending the form again is told that the password was
/// retrieved. <see cref="CredentialRotationService.RetrieveResultAsync"/> decides, and records
/// that it was handed out; this page only puts what it is given on the screen.
/// </summary>
/// <remarks>
/// The password is in the page's text and nowhere else: not in the address, a redirect, a cookie,
/// a hidden field or a script. The answer is marked not to be stored, so the browser's back
/// button and its cache do not bring it back.
/// </remarks>
[Authorize(Policy = AuroraPolicies.Operator)]
public sealed class RotatePasswordResultModel(InstanceService instances, CredentialRotationService credentials) : ResourcePageModel
{
    public InstanceResponse Instance { get; private set; } = null!;

    public Guid JobId { get; private set; }

    /// <summary>The credential, in the one response that has it; null otherwise.</summary>
    public CredentialRotationResultResponse? Result { get; private set; }

    // Looking is not asking: the address by itself never shows a password, and never uses the one time up.
    public IActionResult OnGet(Guid jobId) => RedirectToPage("/Jobs/Details", new { id = jobId });

    public async Task<IActionResult> OnPostAsync(Guid id, Guid jobId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        JobId = jobId;

        if (await instances.GetAsync(id, cancellationToken) is not { } instance)
        {
            return Missing("Instance");
        }

        Instance = instance;
        var result = await credentials.RetrieveResultAsync(id, jobId, cancellationToken);
        if (result.Status == RetrieveCredentialStatus.NotFound)
        {
            return Missing("Job");
        }

        if (result.Status != RetrieveCredentialStatus.Retrieved)
        {
            return Rejected(Rejections.For(result.Status));
        }

        Result = result.Result;
        return Page();
    }
}
