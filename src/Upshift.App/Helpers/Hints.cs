using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Upshift.App.Helpers;

/// <summary>
/// One short line per note: up to 12 words are shown as they are. A longer note shows its first sentence when that is
/// short enough, otherwise "Details", with "ⓘ" and the whole text in a tooltip (and in the name screen readers read).
/// </summary>
public static class Hints
{
    public const int MaxWords = 12;

    public static TextBlock Note(string text, double fontSize = 12)
    {
        var block = new TextBlock
        {
            FontSize = fontSize, TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleTextBrush"]
        };
        Set(block, text);
        return block;
    }

    /// <summary>Puts a note into an existing TextBlock by the same rule (for notes that change while shown).</summary>
    public static void Set(TextBlock block, string text)
    {
        text = text.Trim();
        if (Words(text) <= MaxWords)
        {
            block.Text = text;
            ToolTipService.SetToolTip(block, null);
            AutomationProperties.SetName(block, "");
            return;
        }
        var first = FirstSentence(text);
        block.Text = first is not null && first.Length < text.Length && Words(first) <= MaxWords ? $"{first} ⓘ" : "ⓘ Details";
        ToolTipService.SetToolTip(block, new ToolTip { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 } });
        AutomationProperties.SetName(block, text);
    }

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string? FirstSentence(string text)
    {
        var end = text.IndexOfAny(new[] { '.', ':', ';' });
        return end > 0 ? text[..(end + 1)].TrimEnd(':', ';').Trim() + (text[end] == '.' ? "" : ".") : null;
    }
}
