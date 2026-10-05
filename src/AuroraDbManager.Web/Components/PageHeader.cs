namespace AuroraDbManager.Web.Components;

/// <summary>
/// The top of every page: what it is, a sentence of context, and at most one primary action.
/// </summary>
/// <param name="Title">The page's title.</param>
/// <param name="Description">One sentence saying what the page is for.</param>
/// <param name="Action">The page's primary action; null if it has none, or the user may not take it.</param>
public sealed record PageHeader(string Title, string? Description = null, PageAction? Action = null)
{
    /// <summary>Where the page is: what it is under, outermost first, ending with the page itself.</summary>
    public IReadOnlyList<Crumb> Breadcrumbs { get; init; } = [];

    /// <summary>The status of what the page is about, shown next to its title.</summary>
    public StatusBadge? Status { get; init; }
}

/// <param name="Label">What the action is called.</param>
/// <param name="Path">Where it leads.</param>
/// <param name="Tone">What kind of action it is.</param>
public sealed record PageAction(string Label, string Path, ActionTone Tone = ActionTone.Primary)
{
    public string CssClass => Tone switch
    {
        ActionTone.Primary => "button button-primary",
        ActionTone.Danger => "button button-danger",
        _ => "button"
    };
}

public enum ActionTone
{
    /// <summary>The thing most people come to the page to do, usually creating something.</summary>
    Primary,

    /// <summary>Something else that can be done.</summary>
    Plain,

    /// <summary>Leads to something that cannot be undone. The link only leads to the question; it changes nothing.</summary>
    Danger
}

/// <summary>One step of the way to a page.</summary>
/// <param name="Label">What the step is called.</param>
/// <param name="Path">Where it leads; null for the page itself.</param>
public sealed record Crumb(string Label, string? Path = null);

/// <summary>Which part of a list a page shows, and how to get to the rest.</summary>
/// <param name="Path">The address of the list.</param>
/// <param name="Page">The 1-based page shown.</param>
/// <param name="PageSize">How many items a page holds.</param>
/// <param name="ShownCount">How many items this page shows.</param>
/// <param name="TotalCount">How many items there are.</param>
public sealed record Pager(string Path, int Page, int PageSize, int ShownCount, int TotalCount)
{
    public int First => ShownCount == 0 ? 0 : ((Page - 1) * PageSize) + 1;

    public int Last => First == 0 ? 0 : First + ShownCount - 1;

    public string? PreviousPath => Page > 1 ? PathOf(Page - 1) : null;

    public string? NextPath => (long)Page * PageSize < TotalCount ? PathOf(Page + 1) : null;

    private string PathOf(int page) => page == 1 ? Path : $"{Path}?page={page}";
}

/// <summary>What a page shows where its content would be when there is none.</summary>
/// <param name="Title">What is not there.</param>
/// <param name="Message">Why, and what to do about it.</param>
/// <param name="Icon">The name of an icon in <c>_Icon.cshtml</c>.</param>
/// <param name="Action">A way forward; null if there is none for this user.</param>
public sealed record EmptyState(string Title, string Message, string Icon = "empty", PageAction? Action = null);

/// <summary>A message across the top of a page's content.</summary>
/// <param name="Tone">What kind of message it is.</param>
/// <param name="Message">The message.</param>
/// <param name="Title">An optional heading.</param>
public sealed record Alert(StatusTone Tone, string Message, string? Title = null)
{
    public string ToneName => Tone.ToString().ToLowerInvariant();

    /// <summary>An error interrupts; anything else is told politely.</summary>
    public string Role => Tone is StatusTone.Danger or StatusTone.Warning ? "alert" : "status";
}

/// <summary>The databases of an instance, as a table.</summary>
/// <param name="Instance">The instance they are in.</param>
/// <param name="Databases">The ones to show.</param>
public sealed record DatabaseTable(
    AuroraDbManager.Api.Application.Instances.InstanceResponse Instance,
    IReadOnlyList<AuroraDbManager.Api.Application.Databases.DatabaseResponse> Databases);
