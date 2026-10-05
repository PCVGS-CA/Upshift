using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Upshift.App.Services;
using Upshift.App.ViewModels;
using Upshift.Core.Services;

namespace Upshift.App.Views;

/// <summary>
/// "Report a problem": gathers the report, shows what's in it, saves one zip to the Desktop, shows it in File Explorer
/// and offers "Open GitHub Issues". The user attaches the file themselves; nothing is sent.
/// </summary>
public static class ReportDialog
{
    public static async Task ShowAsync(XamlRoot root, GameCardViewModel? card, Func<ContentDialog, Task<ContentDialogResult>>? show = null)
    {
        show ??= d => d.ShowAsync().AsTask();
        var items = await ProblemReports.BuildAsync(card);

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Text(card is null
            ? "One zip file is saved to your Desktop with:"
            : $"One zip file about {card.Name} is saved to your Desktop with:"));
        foreach (var item in items)
            panel.Children.Add(Text($"• {item.Description} ({item.Name}, {Size(item.Text.Length)})"));
        panel.Children.Add(Text("Your Windows user name is taken out of every path, and your SteamGridDB key and other games are left out" +
                                (card is null ? " (today's log does name the games Upshift changed today)." : ".") +
                                " Nothing is sent anywhere: you attach the file to a GitHub issue yourself."));
        var answer = await show(new ContentDialog
        {
            XamlRoot = root, Title = card is null ? "Report a problem" : "Report a problem with this game",
            Content = new ScrollViewer { Content = panel, MaxHeight = 440 },
            PrimaryButtonText = "Save to Desktop", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
        });
        if (answer != ContentDialogResult.Primary) return;

        var name = "Upshift-report-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + (card is null ? "" : "-" + Safe(card.Name)) + ".zip";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name);
        try
        {
            await Task.Run(() => ProblemReport.Save(path, items));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await show(new ContentDialog { XamlRoot = root, Title = "The report wasn't saved", Content = Text(ex.Message), CloseButtonText = "OK" });
            return;
        }
        try { Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose(); } catch (Exception) { }

        var done = await show(new ContentDialog
        {
            XamlRoot = root, Title = "Report saved",
            Content = Text($"Saved to your Desktop as {name}. Open a GitHub issue, describe what happened, and attach this file."),
            PrimaryButtonText = "Open GitHub Issues", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Primary
        });
        if (done == ContentDialogResult.Primary) await Windows.System.Launcher.LaunchUriAsync(new Uri(ProblemReports.IssuesUrl));
    }

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static string Size(int chars) => chars < 1024 ? $"{chars} B" : chars < 1024 * 1024 ? $"{chars / 1024} KB" : $"{chars / 1024 / 1024.0:0.0} MB";

    private static string Safe(string name)
    {
        var s = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-').ToArray()).Trim().Replace(' ', '-');
        return s.Length > 40 ? s[..40] : s;
    }
}
