using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Upshift.App.ViewModels;
using Upshift.Core.Install;
using Upshift.Core.Wiki;
using Windows.ApplicationModel.DataTransfer;

namespace Upshift.App.Views;

/// <summary>
/// The "Suggestions for this game" card (from the OptiScaler wiki) or "General advice" (from engine and API),
/// with an action button wherever the app can carry the advice out.
/// </summary>
public sealed class SuggestionsView : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(GameCardViewModel), typeof(SuggestionsView),
        new PropertyMetadata(null, (d, e) => ((SuggestionsView)d).OnCardChanged((GameCardViewModel?)e.OldValue, (GameCardViewModel?)e.NewValue)));

    public GameCardViewModel? Card
    {
        get => (GameCardViewModel?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    /// <summary>Set by the page: carries out the actions (apply, reinstall, refresh).</summary>
    public LibraryViewModel? Library { get; set; }

    /// <summary>Set by the page: shows a message dialog (one at a time).</summary>
    public Func<ContentDialog, Task<ContentDialogResult>>? ShowDialog { get; set; }

    private void OnCardChanged(GameCardViewModel? old, GameCardViewModel? card)
    {
        if (old is not null) old.PropertyChanged -= CardPropertyChanged;
        if (card is not null) card.PropertyChanged += CardPropertyChanged;
        Build();
    }

    private void CardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameCardViewModel.Suggestions)) Build();
    }

    private void Build()
    {
        var card = Card;
        if (card?.Suggestions is not { } s) { Content = null; return; }

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = s.Title, FontWeight = FontWeights.SemiBold });

        var body = new StackPanel { Spacing = 10 };
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14),
            Background = Res("CardSurfaceBrush"),
            BorderBrush = Res("HairlineBrush"),
            BorderThickness = new Thickness(1),
            Child = body
        };
        panel.Children.Add(border);

        var suggestionsOff = AppServices.Suggestions.SuggestionsOff(card.Info.Id);
        body.Children.Add(SourceLine(card, s, suggestionsOff));

        if (!suggestionsOff)
            foreach (var item in s.Items) body.Children.Add(ItemRow(card, s, item));

        Content = panel;
    }

    /// <summary>"From the OptiScaler wiki: <entry> · Last tested with OptiScaler X · Open wiki page · Wrong game?"</summary>
    private FrameworkElement SourceLine(GameCardViewModel card, GameSuggestions s, bool off)
    {
        var text = new TextBlock { FontSize = 12, Foreground = Res("SubtleTextBrush"), TextWrapping = TextWrapping.Wrap };
        if (off)
        {
            text.Inlines.Add(new Run { Text = "Suggestions are turned off for this game. " });
        }
        else if (s.FromWiki)
        {
            text.Inlines.Add(new Run { Text = $"From the OptiScaler wiki entry \"{s.MatchedName}\" · " });
            text.Inlines.Add(new Run { Text = s.LastTested is null ? "No tested version given" : $"Last tested with OptiScaler {s.LastTested}" });
            text.Inlines.Add(new Run { Text = " · " });
            if (s.WebUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var link = new Hyperlink { NavigateUri = uri };
                link.Inlines.Add(new Run { Text = s.Page is null ? "Open the Compatibility List" : "Open the wiki page" });
                text.Inlines.Add(link);
                text.Inlines.Add(new Run { Text = " · " });
            }
        }
        else
        {
            text.Inlines.Add(new Run { Text = "Based on the game's engine and graphics API; the OptiScaler wiki has no entry for this game. " });
        }

        var wrong = new Hyperlink();
        wrong.Inlines.Add(new Run { Text = off ? "Turn suggestions back on" : s.FromWiki ? "Wrong game?" : "Pick a wiki entry" });
        wrong.Click += async (_, _) => await PickEntryAsync(card, off);
        text.Inlines.Add(wrong);
        return text;
    }

    private FrameworkElement ItemRow(GameCardViewModel card, GameSuggestions s, Suggestion item)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = item.Kind switch
        {
            SuggestionKind.Warning => "",
            SuggestionKind.Proxy => "",
            SuggestionKind.FrameGen => "",
            SuggestionKind.LaunchOption => "",
            SuggestionKind.IniFix => "",
            SuggestionKind.InGameUpscaler => "",
            _ => ""
        };
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });

        var texts = new StackPanel { Spacing = 2 };
        texts.Children.Add(new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        void Detail(string t) => texts.Children.Add(new TextBlock
        {
            Text = t, FontSize = 12, Foreground = Res("SubtleTextBrush"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
        });
        if (item.Source is { } source) Detail($"OptiScaler wiki: {source}");

        Button? action = null;
        var manifest = card.Info.TargetDir is { } dir ? OptiScalerInstaller.ReadManifest(dir) : null;
        var installed = manifest is { Removed: false };

        switch (item.Kind)
        {
            case SuggestionKind.Proxy:
                if (installed && manifest!.ProxyName.Equals(item.ProxyName, StringComparison.OrdinalIgnoreCase))
                    Detail($"OptiScaler is already loaded as {item.ProxyName}.");
                else if (installed)
                {
                    action = new Button { Content = $"Reinstall as {item.ProxyName}" };
                    action.Click += async (_, _) => await ReinstallAsAsync(card, item.ProxyName!);
                }
                else
                {
                    var chosen = AppServices.InstallPrefs.ProxyFor(card.Info.Id);
                    if (string.Equals(chosen, item.ProxyName, StringComparison.OrdinalIgnoreCase))
                        Detail($"The next install will use {item.ProxyName}.");
                    else
                    {
                        action = new Button { Content = "Use for next install" };
                        action.Click += (_, _) =>
                        {
                            AppServices.InstallPrefs.SetProxy(card.Info.Id, item.ProxyName);
                            Build();
                        };
                    }
                }
                break;

            case SuggestionKind.FrameGen:
                var modes = OptiScalerOptions.FrameGenModes(AppServices.Gpu, card.Info, s.HiddenFrameGen);
                var mode = modes.FirstOrDefault(m => m.Id == item.FrameGenModeId);
                if (mode is null) Detail("Not available on this graphics card or for this game's files.");
                else if (!installed) Detail("Install OptiScaler to apply this.");
                else if (IsFrameGenActive(card, mode)) Detail("In use.");
                else
                {
                    action = new Button { Content = "Apply" };
                    action.Click += async (_, _) => await ConfigureAsync(card, OptiScalerOptions.FrameGenSettings(mode));
                }
                if (mode?.Note is { } note) Detail(note);
                break;

            case SuggestionKind.LaunchOption when card.TakesLaunchOptions && item.LaunchOption is { } option:
                Detail($"Launch option: {option}");
                if (card.LaunchOptions.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(option, StringComparer.OrdinalIgnoreCase))
                    Detail("In the game's launch options; used when you press Play.");
                else
                {
                    action = new Button { Content = "Apply" };
                    action.Click += (_, _) =>
                    {
                        Library?.AddLaunchOption(card, option);
                        Build();
                    };
                    Detail("Apply adds it to the launch options under Main game file, used when you press Play.");
                }
                break;

            case SuggestionKind.LaunchOption:
                Detail($"Launch option: {item.LaunchOption}");
                Detail(card.LaunchOptionsNote);
                action = new Button { Content = "Copy" };
                action.Click += (_, _) =>
                {
                    var package = new DataPackage();
                    package.SetText(item.LaunchOption);
                    Clipboard.SetContent(package);
                    action.Content = "Copied";
                };
                break;

            case SuggestionKind.IniFix when item.Ini is { } ini:
                Detail($"OptiScaler.ini: [{ini.Section}] {ini.Key}={ini.Value}");
                if (!installed) Detail("Install OptiScaler to apply this.");
                else if (IniValue(card, ini) == ini.Value) Detail("Applied.");
                else
                {
                    action = new Button { Content = "Apply" };
                    action.Click += async (_, _) => await ConfigureAsync(card, new[] { ini });
                }
                break;
        }

        if (action is not null)
        {
            action.VerticalAlignment = VerticalAlignment.Top;
            action.IsEnabled = Library?.IsIdle ?? false;
            Grid.SetColumn(action, 2);
            grid.Children.Add(action);
        }
        return grid;
    }

    private static string? IniValue(GameCardViewModel card, IniSetting setting)
    {
        var path = card.Info.TargetDir is { } d ? Path.Combine(d, "OptiScaler.ini") : null;
        return path is not null && File.Exists(path) ? IniFile.Load(path).Get(setting.Section, setting.Key) : null;
    }

    private static bool IsFrameGenActive(GameCardViewModel card, FrameGenMode mode)
    {
        var path = card.Info.TargetDir is { } d ? Path.Combine(d, "OptiScaler.ini") : null;
        return path is not null && File.Exists(path) && OptiScalerOptions.CurrentFrameGen(IniFile.Load(path))?.Id == mode.Id;
    }

    private async Task ConfigureAsync(GameCardViewModel card, IReadOnlyList<IniSetting> settings)
    {
        if (Library is null) return;
        var result = await Library.ConfigureOptiScalerAsync(card, settings);
        if (result is { Success: false }) await Message("The setting wasn't applied", result.Message);
    }

    private async Task ReinstallAsAsync(GameCardViewModel card, string proxy)
    {
        if (Library is null || ShowDialog is null) return;
        var confirm = new ContentDialog
        {
            Title = $"Reinstall OptiScaler as {proxy}?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "OptiScaler is removed (its settings go back to the defaults) and installed again under the new name."
            },
            PrimaryButtonText = "Reinstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await ShowDialog(confirm) != ContentDialogResult.Primary) return;

        var uninstall = await Library.UninstallOptiScalerAsync(card);
        if (uninstall is not { Success: true } || uninstall.KeptChanged.Count > 0)
        {
            await Message("OptiScaler wasn't reinstalled", uninstall?.Message ?? "The old copy couldn't be removed.");
            return;
        }
        AppServices.InstallPrefs.SetProxy(card.Info.Id, proxy);
        var fresh = Library.Games.FirstOrDefault(g => g.Info.Id == card.Info.Id) ?? card;
        var component = await Library.PrepareOptiScalerAsync();
        if (component is null) return;
        var install = await Library.InstallOptiScalerAsync(fresh, component, proxy);
        if (install is { Success: false }) await Message("OptiScaler wasn't reinstalled", install.Message);
    }

    /// <summary>"Wrong game?": pick another Compatibility List entry, go back to automatic, or turn suggestions off.</summary>
    private async Task PickEntryAsync(GameCardViewModel card, bool currentlyOff)
    {
        if (ShowDialog is null) return;
        if (currentlyOff)
        {
            AppServices.Suggestions.SetChoice(card.Info.Id, null);
            Library?.RefreshSuggestions(card);
            return;
        }

        const string automatic = "(Match automatically)";
        var entries = AppServices.Suggestions.Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        var list = new ListView { Height = 320, SelectionMode = ListViewSelectionMode.Single };
        var search = new TextBox { PlaceholderText = "Search the OptiScaler wiki's list", Text = WikiMatcher.Key(card.Name).Split(' ').FirstOrDefault() ?? "" };

        void Filter()
        {
            var q = search.Text.Trim();
            list.ItemsSource = new[] { automatic }
                .Concat(entries.Where(n => q.Length == 0 || n.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                .ToList();
        }
        search.TextChanged += (_, _) => Filter();
        Filter();

        var dialog = new ContentDialog
        {
            Title = $"Which wiki entry is {card.Name}?",
            Content = new StackPanel { Spacing = 10, MinWidth = 420, Children = { search, list } },
            PrimaryButtonText = "Use this entry",
            SecondaryButtonText = "Turn off suggestions",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await ShowDialog(dialog);
        if (result == ContentDialogResult.Secondary)
            AppServices.Suggestions.SetChoice(card.Info.Id, new WikiMatchChoice { Off = true });
        else if (result == ContentDialogResult.Primary && list.SelectedItem is string name)
            AppServices.Suggestions.SetChoice(card.Info.Id, name == automatic ? null : new WikiMatchChoice { EntryName = name });
        else return;

        // A newly chosen entry's page may not be cached yet.
        if (AppServices.Suggestions.EntryFor(card.Info)?.Page is { } page)
            await AppServices.OptiWiki.GetPageAsync(page, CancellationToken.None);
        Library?.RefreshSuggestions(card);
    }

    private async Task Message(string title, string text)
    {
        if (ShowDialog is null) return;
        await ShowDialog(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        });
    }

    private static Microsoft.UI.Xaml.Media.Brush Res(string key) => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];
}
