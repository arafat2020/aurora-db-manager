namespace AuroraDbManager.Web.Components;

/// <summary>
/// The top of every page: what it is, a sentence of context, and at most one primary action.
/// </summary>
/// <param name="Title">The page's title.</param>
/// <param name="Description">One sentence saying what the page is for.</param>
/// <param name="Action">The page's primary action; null if it has none, or the user may not take it.</param>
public sealed record PageHeader(string Title, string? Description = null, PageAction? Action = null);

/// <param name="Label">What the action is called.</param>
/// <param name="Path">Where it leads.</param>
public sealed record PageAction(string Label, string Path);

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
