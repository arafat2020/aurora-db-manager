using System.ComponentModel.DataAnnotations;
using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Instances;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Docker;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Web.Pages.Instances;

/// <summary>
/// Creating an instance. Administrators only, by the policy the API's endpoint has. The form
/// collects what <see cref="CreateInstanceRequest"/> holds, the request is checked by that
/// class's own rules, and <see cref="InstanceService"/> does the rest: this page has no rule of
/// its own about what an instance may be.
/// </summary>
[Authorize(Policy = AuroraPolicies.Admin)]
public sealed class CreateModel(InstanceService instances) : ResourcePageModel
{
    [BindProperty]
    public InstanceInput Input { get; set; } = new();

    /// <summary>
    /// The engines, each with the versions that can be run: the image catalog's, which is where
    /// provisioning looks an instance's engine and version up.
    /// </summary>
    public IReadOnlyList<(InstanceEngine Engine, IReadOnlyList<string> Versions)> Engines { get; } =
        Enum.GetValues<InstanceEngine>().Select(engine => (engine, DockerImageResolver.SupportedVersions(engine))).ToList();

    /// <summary>An engine and a version as one value of the form's one choice.</summary>
    public static string Choice(InstanceEngine engine, string version) => $"{EngineValue(engine)}:{version}";

    public void OnGet()
    {
        // A small server to start from; every number can be changed.
        Input = new InstanceInput { Cpu = 1, MemoryMb = 1024, StorageGb = 10 };
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var choice = Engines
            .SelectMany(engine => engine.Versions.Select(version => (engine.Engine, Version: version)))
            .Where(candidate => Choice(candidate.Engine, candidate.Version) == Input.EngineVersion)
            .Select(candidate => ((InstanceEngine Engine, string Version)?)candidate)
            .FirstOrDefault();
        if (choice is null)
        {
            ModelState.AddModelError(Field(nameof(Input.EngineVersion)), "Choose one of the listed engines and versions.");
        }

        var request = new CreateInstanceRequest
        {
            Name = Input.Name,
            Engine = choice is null ? null : EngineValue(choice.Value.Engine),
            Version = choice?.Version,
            Cpu = Input.Cpu,
            MemoryMb = Input.MemoryMb,
            StorageGb = Input.StorageGb
        };

        // The request's own rules, the ones the API applies to it, reported at the fields of this form.
        var problems = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), problems, validateAllProperties: true);
        foreach (var problem in problems)
        {
            var member = problem.MemberNames.FirstOrDefault();
            if (member is nameof(CreateInstanceRequest.Engine) or nameof(CreateInstanceRequest.Version))
            {
                // Both come from the one choice, and what is wrong with it has been said.
                continue;
            }

            // A value that could not even be read, "abc" for a number, already has its message.
            if (member is null || ModelState[Field(member)] is not { Errors.Count: > 0 })
            {
                ModelState.AddModelError(member is null ? string.Empty : Field(member), problem.ErrorMessage ?? "The value is not valid.");
            }
        }

        if (!ModelState.IsValid)
        {
            return Invalid();
        }

        var created = await instances.CreateAsync(request, cancellationToken);

        Announce(StatusTone.Progress, "Instance creation started. The instance is being provisioned.");
        return RedirectToPage("/Instances/Details", new { id = created.Instance.Id });
    }

    private static string Field(string member) => $"{nameof(Input)}.{member}";

    // The engine as the request names it: "postgres", "mysql".
    private static string EngineValue(InstanceEngine engine) => engine.ToString().ToLowerInvariant();

    /// <summary>What the form holds. Its fields are those of <see cref="CreateInstanceRequest"/>, which has the rules.</summary>
    public sealed class InstanceInput
    {
        public string? Name { get; set; }

        /// <summary>The engine and its version, chosen together: see <see cref="Choice"/>.</summary>
        public string? EngineVersion { get; set; }

        public int? Cpu { get; set; }

        public int? MemoryMb { get; set; }

        public int? StorageGb { get; set; }
    }
}
