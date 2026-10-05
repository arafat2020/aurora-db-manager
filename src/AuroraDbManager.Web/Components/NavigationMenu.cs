using System.Security.Claims;

namespace AuroraDbManager.Web.Components;

/// <param name="Label">What the entry is called.</param>
/// <param name="Path">Where it leads.</param>
/// <param name="Icon">The name of its icon in <c>_Icon.cshtml</c>.</param>
public sealed record NavigationItem(string Label, string Path, string Icon)
{
    /// <summary>Whether <paramref name="currentPath"/> is this entry or something under it.</summary>
    public bool IsCurrent(string currentPath) =>
        Path == "/"
            ? currentPath == "/"
            : currentPath.Equals(Path, StringComparison.OrdinalIgnoreCase) || currentPath.StartsWith(Path + "/", StringComparison.OrdinalIgnoreCase);
}

/// <param name="Title">The group's heading; null for the first group, which needs none.</param>
/// <param name="Items">Its entries.</param>
public sealed record NavigationGroup(string? Title, IReadOnlyList<NavigationItem> Items);

/// <summary>The sections of the UI, and which of them a user is shown.</summary>
public static class NavigationMenu
{
    public static readonly NavigationItem Overview = new("Overview", "/", "overview");
    public static readonly NavigationItem Instances = new("Instances", "/instances", "instances");
    public static readonly NavigationItem Schedules = new("Schedules", "/schedules", "schedules");
    public static readonly NavigationItem Jobs = new("Jobs", "/jobs", "jobs");
    public static readonly NavigationItem Monitoring = new("Monitoring", "/monitoring", "monitoring");
    public static readonly NavigationItem Users = new("Users", "/users", "users");

    /// <summary>
    /// The menu for a user. Databases have no entry of their own: a database is in an instance,
    /// and is found there. Nor have backups: a backup is of a database, and is found there. Every signed-in user may read everything operational; only an
    /// administrator is shown the administration group, because only an administrator's request
    /// for it would be answered.
    /// </summary>
    public static IReadOnlyList<NavigationGroup> For(ClaimsPrincipal user)
    {
        var groups = new List<NavigationGroup>
        {
            new(null, [Overview, Instances, Schedules, Jobs, Monitoring])
        };

        if (user.IsAdmin())
        {
            groups.Add(new NavigationGroup("Administration", [Users]));
        }

        return groups;
    }
}
