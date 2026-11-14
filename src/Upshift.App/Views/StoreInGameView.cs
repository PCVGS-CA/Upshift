using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Upshift.App.Services;
using Upshift.Core.Install;
using Upshift.Core.Store;

namespace Upshift.App.Views;

/// <summary>
/// "From the Upscaler Store" on the game's Upscaling tab: each add-on in this game with its version and date, "Remove" (the
/// game exactly as before), "Go back to the previous version" and "Use the newest" for recipes that follow the author's newest
/// release, and "Browse the Store". Hidden when the game has none.
/// </summary>
public sealed class StoreInGameView : CardPart
{
    protected override void Build()
    {
        var card = Card;
        var added = card is null ? Array.Empty<StoreInstalled>() : StoreService.InGame(card.Info);
        if (card is null || added.Count == 0) { Content = null; Visibility = Visibility.Collapsed; return; }
        Visibility = Visibility.Visible;
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(Text("From the Upscaler Store", 16, bold: true));
        foreach (var installed in added)
        {
            var entry = StoreService.Store.Find(installed.EntryId) ?? installed.Entry;
            var row = new StackPanel { Spacing = 4 };
            var date = installed.Released is { } r ? $" · released {r.LocalDateTime:d MMM yyyy}" : "";
            row.Children.Add(Text($"{installed.Name} {installed.Version}{date}" + (StoreService.IsHeld(card.Info, installed.EntryId) ? " · kept on this version" : ""), bold: true));
            if (installed.Message is { Length: > 0 } message) row.Children.Add(Text(message, subtle: true));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var pretend = AppServices.PretendGpu is not null;
            if (entry is not null && StoreService.PreviousOf(entry, installed.Version) is { } previous)
                buttons.Children.Add(Act("Go back to the previous version", $"{installed.Name} {previous.Version}, kept in Upshift's folder", !pretend,
                    () => StoreService.GoBackAsync(entry, card.Info, null)));
            if (entry is not null && (StoreService.IsHeld(card.Info, installed.EntryId)
                                      || (StoreService.NewestKnown(installed.EntryId) is { } n && n.Tag != installed.Version)))
                buttons.Children.Add(Act("Use the newest", null, !pretend, () => StoreService.UseNewestAsync(entry, card.Info, null)));
            buttons.Children.Add(Act("Remove", "Takes it out: the game is exactly as before it was added", !pretend,
                () => StoreService.RemoveAsync(card.Info, installed.EntryId, null), $"Remove {installed.Name}"));
            row.Children.Add(buttons);
            if (entry is not null && Uri.TryCreate(entry.Repo + "/issues", UriKind.Absolute, out var issues))
                row.Children.Add(new HyperlinkButton { Content = $"Problems with {installed.Name}? Its issues page", NavigateUri = issues, Padding = new Thickness(0) });
            body.Children.Add(row);
        }
        var browse = new HyperlinkButton { Content = "Browse the Upscaler Store", Padding = new Thickness(0) };
        browse.Click += (_, _) => (App.MainAppWindow as MainWindow)?.ShowPage("store");
        body.Children.Add(browse);
        Content = body;
    }

    private Button Act(string label, string? tip, bool enabled, Func<Task<InstallResult>> work, string? name = null)
    {
        var button = new Button { Content = label, IsEnabled = enabled };
        if (tip is not null) ToolTipService.SetToolTip(button, tip);
        AutomationProperties.SetName(button, name ?? label);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            var result = await work();
            if (!result.Success && XamlRoot is not null)
                await new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "Nothing was changed", CloseButtonText = "OK",
                    Content = new TextBlock { Text = Helpers.ScreenText.Clean(result.Message), TextWrapping = TextWrapping.Wrap }
                }.ShowAsync();
            Rebuild();
        };
        return button;
    }
}
