using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace AuroraDbManager.Web.Components;

/// <summary>
/// A message for the page a change leads to: said by the page that made the change, shown once
/// by the next page, and gone after that. It travels in the encrypted, <c>HttpOnly</c> cookie of
/// TempData, never in an address, and holds a sentence, never a secret.
/// </summary>
public static class Flash
{
    private const string ToneKey = "Flash.Tone";
    private const string MessageKey = "Flash.Message";

    public static void Set(ITempDataDictionary tempData, StatusTone tone, string message)
    {
        tempData[ToneKey] = tone.ToString();
        tempData[MessageKey] = message;
    }

    /// <summary>The message waiting to be shown, which is thereby shown; null if there is none.</summary>
    public static Alert? Take(ITempDataDictionary tempData)
    {
        var message = tempData[MessageKey] as string;
        var tone = Enum.TryParse<StatusTone>(tempData[ToneKey] as string, out var parsed) ? parsed : StatusTone.Neutral;
        return string.IsNullOrEmpty(message) ? null : new Alert(tone, message);
    }
}
