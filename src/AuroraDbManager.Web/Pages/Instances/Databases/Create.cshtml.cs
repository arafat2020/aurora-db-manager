using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Databases;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances.Databases;

/// <summary>
/// Creating a database in an instance. Operators and administrators, by the policy the API's
/// endpoint has. The name is judged by <see cref="DatabaseService"/>, which is also what says
/// whether the instance can take a database now and whether the name is free.
/// </summary>
[Authorize(Policy = AuroraPolicies.Operator)]
public sealed class CreateModel(InstanceService instances, DatabaseService databases) : ResourcePageModel
{
    public InstanceResponse Instance { get; private set; } = null!;

    [BindProperty]
    public DatabaseInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken) =>
        await LoadAsync(id, cancellationToken) ? Page() : Missing("Instance");

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken))
        {
            return Missing("Instance");
        }

        var result = await databases.CreateAsync(id, new CreateDatabaseRequest { Name = Input.Name }, cancellationToken);
        switch (result.Status)
        {
            case CreateDatabaseStatus.Accepted:
                // Accepted, not done: a job creates it, and the database says "creating" until then.
                Announce(StatusTone.Progress, "Database creation started.");
                return RedirectToPage("/Instances/Databases/Details", new { id, databaseId = result.Operation!.Database.Id });

            case CreateDatabaseStatus.NameInvalid:
                ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Name)}", result.NameError!);
                return Invalid();

            case CreateDatabaseStatus.InstanceNotFound:
                return Missing("Instance");

            default:
                return Rejected(Rejections.For(result.Status)!);
        }
    }

    private async Task<bool> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await instances.GetAsync(id, cancellationToken) is not { } instance)
        {
            return false;
        }

        Instance = instance;
        return true;
    }

    /// <summary>What the form holds: the one field of <see cref="CreateDatabaseRequest"/>.</summary>
    public sealed class DatabaseInput
    {
        public string? Name { get; set; }
    }
}
