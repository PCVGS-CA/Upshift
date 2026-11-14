using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Upshift.App.Helpers;
using Upshift.App.Services;
using Upshift.Core.Install;
using Upshift.Core.Models;
using Upshift.Core.Store;
using Windows.Storage.Pickers;

namespace Upshift.App.Views;

/// <summary>
/// The Upscaler Store: cards from store.json (name, one line, status, GPU/API tags, type), filtered by "Works on my PC" (the
/// detected card), status, type and a search; the detail panel shows the description, notes, licence and link, and "Add to a
/// game" with only the games it can work in. Built-in entries open Upshift's own page; link-only entries only open the project.
/// </summary>
public sealed partial class StorePage : Page
{
    private StoreEntry? _selected;
    private bool _busy;

    public StorePage()
    {
        InitializeComponent();
        Loaded += (_, _) => { Fill(); };
    }

    private void Filter_Changed(object sender, object e) => Fill();
    private void Search_Changed(object sender, TextChangedEventArgs e) => Fill();

    private IEnumerable<StoreEntry> Visible()
    {
        var store = StoreService.Store;
        var status = (StatusBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var type = (TypeBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var search = SearchBox?.Text?.Trim() ?? "";
        return store.Entries.Where(e =>
            (FitsSwitch?.IsOn != true || StoreService.WorksOnThisPc(e))
            && (status.Length == 0 || e.Status == status)
            && (type.Length == 0 || e.Type == type)
            && (search.Length == 0 || $"{e.Name} {e.Author} {e.Description} {e.Notes}".Contains(search, StringComparison.CurrentCultureIgnoreCase)));
    }

    private void Fill()
    {
        if (Cards is null) return;
        var list = Visible().ToList();
        Cards.Items.Clear();
        foreach (var entry in list) Cards.Items.Add(CardFor(entry));
        var all = StoreService.Store.Entries.Count;
        CountText.Text = list.Count == all ? $"{all} entries" : $"{list.Count} of {all} entries"
                         + (FitsSwitch.IsOn && list.Count < all ? " (Works on my PC is on)" : "");
        if (_selected is null || !list.Contains(_selected)) _selected = list.FirstOrDefault();
        ShowDetail();
    }

    private static FrameworkElement CardFor(StoreEntry e)
    {
        var panel = new StackPanel { Spacing = 6, Width = 300, Padding = new Thickness(14), Tag = e };
        var title = new Grid { ColumnSpacing = 8 };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock { Text = e.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var badge = Badge(e.Status);
        Grid.SetColumn(badge, 1);
        title.Children.Add(badge);
        panel.Children.Add(title);
        panel.Children.Add(new TextBlock { Text = e.Description, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 });
        panel.Children.Add(Tags(e));
        panel.Children.Add(new TextBlock { Text = StoreTypes.Label(e.Type), FontSize = 12, Foreground = Subtle() });
        AutomationProperties.SetName(panel, e.Name);
        return new Border
        {
            Child = panel, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], Tag = e
        };
    }

    private static Brush Subtle() => (Brush)Application.Current.Resources["SubtleTextBrush"];

    private static Border Badge(string status) => new()
    {
        CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Top,
        Background = Ui.Brush(status switch { "Stable" => "#1F6F43", "Beta" => "#7A5A00", _ => "#8A2B2B" }),
        Child = new TextBlock { Text = status, FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }
    };

    private static FrameworkElement Tags(StoreEntry e)
    {
        var tags = new List<string>();
        foreach (var gpu in e.Gpus)
            tags.Add(gpu.Generations.Count == 0 ? Vendor(gpu.Vendor) : $"{Vendor(gpu.Vendor)} {string.Join("/", gpu.Generations)}");
        if (e.Gpus.Count == 0) tags.Add("Any card");
        tags.AddRange(e.Apis.Count == 0 ? new[] { "Any API" } : e.Apis);
        var wrap = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal };
        foreach (var tag in tags)
            wrap.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 4),
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                Child = new TextBlock { Text = tag, FontSize = 11 }
            });
        return wrap;
    }

    private static string Vendor(string v) => v switch { "nvidia" => "NVIDIA", "amd" => "AMD", "intel" => "Intel", _ => v };

    private void Card_Click(object sender, ItemClickEventArgs e)
    {
        _selected = (e.ClickedItem as FrameworkElement)?.Tag as StoreEntry;
        ShowDetail();
    }

    // ---------------- detail ----------------

    private void ShowDetail()
    {
        Detail.Children.Clear();
        if (_selected is not { } e)
        {
            Detail.Children.Add(new TextBlock { Text = "Nothing matches. Turn off Works on my PC to see everything.", TextWrapping = TextWrapping.Wrap });
            return;
        }
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock { Text = e.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        head.Children.Add(Badge(e.Status));
        Detail.Children.Add(head);
        Detail.Children.Add(new TextBlock { Text = $"by {e.Author} · {StoreTypes.Label(e.Type)}", Foreground = Subtle() });
        Detail.Children.Add(new TextBlock { Text = e.Description, TextWrapping = TextWrapping.Wrap });
        Detail.Children.Add(Tags(e));
        if (e.Notes is { Length: > 0 } notes) Detail.Children.Add(new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        Detail.Children.Add(new TextBlock { Text = $"Licence: {e.License}", Foreground = Subtle() });
        if (!StoreService.WorksOnThisPc(e)) Detail.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = "Not for the graphics card in this PC." });

        var link = new HyperlinkButton { Content = "Open project page", NavigateUri = new Uri(e.Repo), Padding = new Thickness(0) };
        AutomationProperties.SetName(link, "Open project page");
        Detail.Children.Add(link);

        switch (e.Type)
        {
            case StoreTypes.Builtin:
                var open = new Button { Content = e.Builtin switch { "helixsr" => "Open HelixSR (Updates page)", "testbuilds" => "Open Settings > OptiScaler", _ => "Open the Library" } };
                AutomationProperties.SetName(open, "Open in Upshift");
                open.Click += (_, _) => (App.MainAppWindow as MainWindow)?.ShowPage(e.Builtin switch { "helixsr" => "updates", "testbuilds" => "settings", _ => "library" });
                Detail.Children.Add(open);
                if (e.Builtin == "dlss5") Detail.Children.Add(Line("Pick a game, then Upscaling > More options > DLSS 5."));
                break;
            case StoreTypes.LinkOnly:
                Detail.Children.Add(Line("Upshift doesn't download or install this one."));
                break;
            default:
                AddToGame(e);
                break;
        }
        InstalledIn(e);
    }

    private static TextBlock Line(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Subtle() };

    /// <summary>"Add to a game": only the games it can work in; the others counted with their reasons in a tooltip.</summary>
    private void AddToGame(StoreEntry e)
    {
        var games = AppServices.Library.Current.Where(g => StoreService.InGame(g, e.Id) is null).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var fits = games.Where(g => StoreService.WhyNot(e, g) is null).ToList();
        var not = games.Except(fits).Select(g => $"{g.Name}: {StoreService.WhyNot(e, g)}").ToList();
        Detail.Children.Add(new TextBlock { Text = "Add to a game", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        if (fits.Count == 0)
        {
            Detail.Children.Add(Line(not.Count == 0 ? "No games in the Library." : "None of your games can take it now."));
        }
        else
        {
            var box = new ComboBox { PlaceholderText = "Choose a game", MinWidth = 300 };
            AutomationProperties.SetName(box, "Game to add it to");
            foreach (var g in fits) box.Items.Add(new ComboBoxItem { Content = g.Name, Tag = g });
            var add = new Button { Content = e.Type == StoreTypes.AuthorSetup ? "Run the setup in this game" : e.Type == StoreTypes.UserSupplied ? "Choose file and add" : "Add", IsEnabled = false };
            AutomationProperties.SetName(add, "Add to the game");
            box.SelectionChanged += (_, _) => add.IsEnabled = box.SelectedItem is not null && !_busy;
            var status = Line("");
            add.Click += async (_, _) =>
            {
                if (box.SelectedItem is not ComboBoxItem { Tag: GameInfo game }) return;
                add.IsEnabled = false;
                await AddAsync(e, game, status);
                ShowDetail();
            };
            Detail.Children.Add(box);
            Detail.Children.Add(add);
            Detail.Children.Add(status);
        }
        if (not.Count > 0)
        {
            var more = Line($"{not.Count} other game{(not.Count == 1 ? "" : "s")} can't take it (hover for why).");
            ToolTipService.SetToolTip(more, string.Join("\n", not.Take(25)));
            Detail.Children.Add(more);
        }
    }

    private async Task AddAsync(StoreEntry e, GameInfo game, TextBlock status)
    {
        // Experimental entries: their notes once, confirmed. An author's setup: exactly what runs, every time.
        if (e.Type == StoreTypes.AuthorSetup)
        {
            if (!await Confirm($"Run {e.Name}'s setup?",
                    $"Upshift will download {e.Name}'s setup from {e.Repo} (checked against GitHub's checksum) and run the author's own program, without administrator rights, in {game.Name}'s folder. " +
                    "It snapshots the game folder and Upshift's folder for it before and after, so Remove can undo every change. If the setup changes anything outside those two folders " +
                    "(the registry, Windows, Program Files…), Upshift undoes what it did and won't run it again on this PC. Changes to things that already existed outside can't be seen.\n\n" +
                    $"What runs: {e.Setup!.Run} {string.Join(" ", e.Setup.Args)}", "Run setup")) return;
        }
        else if (e.Status == "Experimental" && !AppServices.Settings.Current.StoreNoticesShown.Contains(e.Id))
        {
            if (!await Confirm(e.Name, $"{e.Name} is experimental and from a third party ({e.Author}). It may crash games. {e.Notes}", "Continue")) return;
            var settings = AppServices.Settings.Current;
            settings.StoreNoticesShown.Add(e.Id);
            try { AppServices.Settings.Save(settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        string? file = null;
        if (e.Type == StoreTypes.UserSupplied)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add(Path.GetExtension(e.UserFile!.FileName) is { Length: > 0 } ext ? ext : "*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow));
            if (await picker.PickSingleFileAsync() is not { } picked) return;
            file = picked.Path;
        }
        _busy = true;
        try
        {
            var result = await StoreService.AddAsync(e, game, new Progress<string>(s => status.Text = s), userFile: file);
            status.Text = "";
            await Message(result.Success ? $"{e.Name} added to {game.Name}" : $"{e.Name} wasn't added", Helpers.ScreenText.Clean(result.Message));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await Message($"{e.Name} wasn't added", Helpers.ScreenText.Clean(ex.Message));
        }
        finally { _busy = false; }
    }

    /// <summary>The games it's in, each with Remove.</summary>
    private void InstalledIn(StoreEntry e)
    {
        var games = StoreService.Games(e.Id);
        if (games.Count == 0) return;
        Detail.Children.Add(new TextBlock { Text = "In your games", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        foreach (var (game, installed) in games)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = $"{game.Name} · {installed.Version}", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            var remove = new Button { Content = "Remove" };
            AutomationProperties.SetName(remove, $"Remove from {game.Name}");
            Grid.SetColumn(remove, 1);
            remove.Click += async (_, _) =>
            {
                remove.IsEnabled = false;
                var result = await StoreService.RemoveAsync(game, e.Id, null);
                if (!result.Success) await Message($"{e.Name} wasn't removed", Helpers.ScreenText.Clean(result.Message));
                ShowDetail();
            };
            row.Children.Add(remove);
            Detail.Children.Add(row);
        }
    }

    private async Task<bool> Confirm(string title, string text, string yes) =>
        await new ContentDialog
        {
            XamlRoot = XamlRoot, Title = title, PrimaryButtonText = yes, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 420 }
        }.ShowAsync() == ContentDialogResult.Primary;

    private async Task Message(string title, string text) =>
        await new ContentDialog
        {
            XamlRoot = XamlRoot, Title = title, CloseButtonText = "OK",
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
        }.ShowAsync();
}
