using Microsoft.UI.Xaml.Controls;

namespace Upshift.App.Helpers;

/// <summary>
/// Remembers whether each collapsible section was left open or closed, across restarts (settings.json,
/// OpenSections). Keys are fixed names ("game.changes"), so a section opens the same way for every game.
/// </summary>
public static class Sections
{
    public static bool IsOpen(string key, bool fallback = false) =>
        AppServices.Settings.Current.OpenSections.TryGetValue(key, out var open) ? open : fallback;

    public static void Set(string key, bool open)
    {
        var settings = AppServices.Settings.Current;
        if (settings.OpenSections.TryGetValue(key, out var was) && was == open) return;
        settings.OpenSections[key] = open;
        try { AppServices.Settings.Save(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* remembered for this session only */ }
    }

    /// <summary>Opens the expander as it was last left, and remembers each change.</summary>
    public static void Bind(Expander expander, string key, bool fallback = false)
    {
        expander.IsExpanded = IsOpen(key, fallback);
        expander.Expanding += (_, _) => Set(key, true);
        expander.Collapsed += (_, _) => Set(key, false);
    }
}
