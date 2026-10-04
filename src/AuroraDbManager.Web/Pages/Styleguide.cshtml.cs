using System.ComponentModel.DataAnnotations;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Web.Components;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AuroraDbManager.Web.Pages;

/// <summary>
/// The UI's building blocks on one page, for whoever builds the next page. It exists in the
/// development environment only; anywhere else the address is not found. Its form changes
/// nothing: it is there to show a form's validation, confirmation and submitted states.
/// </summary>
public sealed class StyleguideModel(IHostEnvironment environment) : PageModel
{
    [BindProperty]
    public SampleInput Sample { get; set; } = new();

    public bool Submitted { get; private set; }

    public IReadOnlyList<StatusBadge> Badges { get; } =
    [
        StatusBadge.For(InstanceStatus.Running),
        StatusBadge.For(InstanceStatus.Provisioning),
        StatusBadge.For(InstanceStatus.Stopped),
        StatusBadge.For(InstanceStatus.Failed),
        StatusBadge.For(DatabaseStatus.Ready),
        StatusBadge.For(DatabaseStatus.Creating),
        StatusBadge.For(DatabaseStatus.Deleting),
        StatusBadge.For(JobStatus.Pending),
        StatusBadge.For(JobStatus.Completed),
        StatusBadge.For(HealthStatus.Degraded),
        StatusBadge.ForEnabled(false)
    ];

    public IActionResult OnGet() => environment.IsDevelopment() ? Page() : NotFound();

    public IActionResult OnPost()
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        Submitted = ModelState.IsValid;
        return Page();
    }

    public sealed class SampleInput
    {
        [Required(ErrorMessage = "Enter a name.")]
        [RegularExpression("^[a-z][a-z0-9_]*$", ErrorMessage = "Use lowercase letters, digits and underscores, starting with a letter.")]
        [MaxLength(DatabaseName.MaxLength, ErrorMessage = "A name is at most {1} characters.")]
        public string? Name { get; set; }

        public string Engine { get; set; } = "postgres";
    }
}
