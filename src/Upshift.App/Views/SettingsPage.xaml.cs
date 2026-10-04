using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Upshift.App.Services;
using Upshift.Core.Catalog;
using Upshift.Core.Hardware;
using Upshift.Core.Install;
using Upshift.Core.Services;
using Upshift.Core.UserFiles;
using Windows.Storage.Pickers;

namespace Upshift.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataDirText.Text = AppServices.DataDir;
        SteamGridDbKeyBox.Password = AppServices.Settings.Current.SteamGridDbKey ?? "";
        AutoCheckSwitch.IsOn = AppServices.Settings.Current.AutoCheckUpdates;
        AutoCheckSwitch.Toggled += AutoCheck_Toggled;
        CatalogUrlBox.Text = AppServices.Settings.Current.CatalogUrl;
        CatalogStatusText.Text = AppServices.CatalogStatus
            ?? (AppServices.UsingRemoteCatalog
                ? string.IsNullOrWhiteSpace(AppServices.Settings.Current.CatalogUrl) ? "Using Upshift's online catalog." : "Using the catalog from this address."
                : "Using the built-in catalog (the online one isn't newer or couldn't be reached).");
        BuildUserFileRows();

        BuildHiddenRows();
        Helpers.Sections.Bind(HiddenExpander, "settings.hiddenGames");
        Helpers.AutoScroll.Attach(PageScroller);
        BuildDeveloperOptions();
    }

    // ---------------- Hidden games ----------------

    private void BuildHiddenRows()
    {
        HiddenRows.Children.Clear();
        var hidden = AppServices.Settings.Current.HiddenGames.OrderBy(h => h.Value, StringComparer.CurrentCultureIgnoreCase).ToList();
        HiddenExpander.Header = hidden.Count == 0 ? "Show hidden games (none)" : $"Show hidden games ({hidden.Count})";
        if (hidden.Count == 0)
        {
            HiddenRows.Children.Add(new TextBlock { Text = "No games are hidden.", FontSize = 12, Foreground = Brush("SubtleTextBrush") });
            return;
        }
        foreach (var (id, name) in hidden)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            var show = new Button { Content = "Show in Library" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(show, $"Show {name} in Library");
            show.Click += (_, _) =>
            {
                var settings = AppServices.Settings.Current;
                settings.HiddenGames.Remove(id);
                AppServices.Settings.Save(settings);
                AppServices.RaiseHiddenGamesChanged();
                BuildHiddenRows();
            };
            Grid.SetColumn(show, 1);
            row.Children.Add(show);
            HiddenRows.Children.Add(row);
        }
    }

    // ---------------- Optional files you supply ----------------

    private static readonly (UserFileKind Kind, string Title, string Use)[] UserFileKinds =
    {
        (UserFileKind.DlssNr, "DLSS 5 (nvngx_dlssnr.dll)", "Used by a later version of this app."),
        (UserFileKind.Fsr4Int8, "FSR 4.0.2c INT8 (amdxcffx64.dll)", "Used by the \"Your 4.0.2c file\" FSR 4 source in a game's OptiScaler options."),
        (UserFileKind.Fsr411bInt8, "FSR 4.1.1b INT8 for RX 6000 (amd_fidelityfx_upscaler_dx12.dll)",
            "The newer modified FSR 4 file for RX 6000 cards (4.0.2c is the older one). Used by the \"Your 4.1.1b file\" FSR 4 source in a game's OptiScaler options, " +
            "where it takes the place of OptiScaler's own amd_fidelityfx_upscaler_dx12.dll.")
    };

    private bool _dialogOpen;

    /// <summary>A running or finished "Find it for me" search for one file.</summary>
    private sealed class SearchState
    {
        public CancellationTokenSource Cts { get; } = new();
        public bool Running { get; set; } = true;
        public string Status { get; set; } = "";
        public List<FoundFile> Found { get; set; } = new();
    }

    private readonly Dictionary<UserFileKind, SearchState> _searches = new();
    private readonly Dictionary<UserFileKind, StackPanel> _resultPanels = new();

    private static Microsoft.UI.Xaml.Media.Brush Brush(string key) => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];

    private void BuildUserFileRows()
    {
        UserFileRows.Children.Clear();
        _resultPanels.Clear();
        foreach (var (kind, title, use) in UserFileKinds)
        {
            var info = AppServices.UserFiles.Get(kind);
            var texts = new StackPanel { Spacing = 2 };
            texts.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            texts.Children.Add(new TextBlock
            {
                FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                Foreground = Brush("SubtleTextBrush"),
                Text = info is null
                    ? $"Not added. {use}"
                    : kind == UserFileKind.DlssNr
                        ? $"{Dlss5.CheckFile(info.Path, AppServices.Catalog).Label} · SHA-256 {info.Sha256}"
                        : kind == UserFileKind.Fsr411bInt8
                            ? $"Modified file: not signed by AMD, can't be verified as safe · Version {VersionText(kind, info.Version)} · SHA-256 {info.Sha256}"
                            : $"Version {VersionText(kind, info.Version)} · SHA-256 {info.Sha256} · Community build, can't be verified. {use}"
            });
            if (kind == UserFileKind.Fsr411bInt8 && info is not null)
                texts.Children.Add(new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = use });
            if (kind == UserFileKind.DlssNr)
            {
                // The catalog's rule for this card (or the pretend one), in one sentence.
                var option = NeuralSelector.Evaluate(AppServices.EffectiveGpu, AppServices.Catalog);
                texts.Children.Add(new TextBlock
                {
                    FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    Text = Dlss5.FileRule(option) + (AppServices.PretendGpu is { } p ? $" (preview: {p.Name})" : "")
                });
            }

            var running = _searches.TryGetValue(kind, out var search) && search.Running;
            var select = new Button { Content = "Select file…" };
            select.Click += async (_, _) => await SelectUserFileAsync(kind);
            var find = new Button { Content = running ? "Stop searching" : "Find it for me" };
            find.Click += async (_, _) =>
            {
                if (_searches.TryGetValue(kind, out var s) && s.Running) s.Cts.Cancel();
                else await FindUserFileAsync(kind);
            };
            var remove = new Button { Content = "Remove", IsEnabled = info is not null };
            remove.Click += async (_, _) =>
            {
                if (kind == UserFileKind.DlssNr && !await AskAboutGamesUsingDlss5Async()) return;
                AppServices.UserFiles.Remove(kind);
                BuildUserFileRows();
            };

            var results = new StackPanel { Spacing = 6 };
            _resultPanels[kind] = results;

            var row = new StackPanel { Spacing = 6 };
            row.Children.Add(texts);
            row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { select, find, remove } });
            row.Children.Add(results);
            UserFileRows.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
                Background = Brush("CardSurfaceBrush"),
                BorderBrush = Brush("HairlineBrush"),
                BorderThickness = new Thickness(1),
                Child = row
            });
            RenderSearch(kind);
        }
    }

    /// <summary>
    /// Before the stored DLSS 5 file is removed: when games still have it (a DLSS 5 build switched in), asks "Also
    /// remove DLSS 5 from these games?". Yes switches each back to regular OptiScaler, keeping its settings; No leaves
    /// them as they are. Returns false when the user cancels (nothing is removed then).
    /// </summary>
    private async Task<bool> AskAboutGamesUsingDlss5Async()
    {
        var games = GameUpdates.GamesUsingDlss5File();
        if (games.Count == 0) return true;
        if (_dialogOpen) return false;

        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "These games still use your DLSS 5 file. Yes switches each back to regular OptiScaler (your settings are kept); " +
                   "No leaves them as they are. Either way, Upshift's stored copy is deleted."
        });
        foreach (var game in games) list.Children.Add(new TextBlock { Text = "• " + game.Name, TextWrapping = TextWrapping.Wrap });

        ContentDialogResult answer;
        _dialogOpen = true;
        try
        {
            answer = await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Also remove DLSS 5 from these games?", Content = list,
                PrimaryButtonText = "Yes", SecondaryButtonText = "No", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            }.ShowAsync();
        }
        finally { _dialogOpen = false; }
        if (answer == ContentDialogResult.None) return false;
        if (answer == ContentDialogResult.Secondary) return true;

        var problems = new List<string>();
        foreach (var game in games)
        {
            var result = await GameUpdates.SwitchBackAsync(game, null);
            if (!result.Success) problems.Add($"{game.Name}: {result.Message}");
        }
        if (problems.Count > 0)
        {
            _dialogOpen = true;
            try
            {
                await new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "Some games weren't switched back", CloseButtonText = "OK",
                    Content = new TextBlock { Text = string.Join("\n", problems), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
                }.ShowAsync();
            }
            finally { _dialogOpen = false; }
        }
        return true;
    }

    // ---------------- Developer options ----------------

    /// <summary>Shown when Settings is opened with Shift held, or while a pretend card is set (so it can be turned off).</summary>
    private void BuildDeveloperOptions()
    {
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!shift && AppServices.PretendGpu is null) return;
        DeveloperPanel.Visibility = Visibility.Visible;
        _building = true;
        PretendGpuBox.Items.Clear();
        PretendGpuBox.Items.Add(new ComboBoxItem { Content = $"Off (this PC's card: {AppServices.Gpu?.Name ?? "not detected"})" });
        foreach (var gpu in AppServices.PretendGpus)
            PretendGpuBox.Items.Add(new ComboBoxItem { Content = $"{gpu.Name} ({gpu.Generation})", Tag = gpu });
        PretendGpuBox.SelectedItem = PretendGpuBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is GpuInfo g && g == AppServices.PretendGpu)
                                     ?? PretendGpuBox.Items[0];
        _building = false;
    }

    private bool _building;

    private void PretendGpu_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_building || PretendGpuBox.SelectedItem is not ComboBoxItem item) return;
        AppServices.SetPretendGpu(item.Tag as GpuInfo);
        BuildUserFileRows();
    }

    /// <summary>The file's version; the DLSS 5 file's with its name, "310.8.0.0 (DLSS 5)".</summary>
    private static string VersionText(UserFileKind kind, string? version) =>
        version is null ? "unknown" : kind == UserFileKind.DlssNr ? Helpers.Ui.VersionLabel(version, UserFileStore.FileNameFor(kind)) : version;

    private async Task SelectUserFileAsync(UserFileKind kind)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".dll");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await ImportAsync(kind, file.Path);
    }

    /// <summary>
    /// Searches Downloads, Desktop, Documents, the games' folders and then the drives (read-only). Each file shows up
    /// under the row as soon as it's found, so one can be used without waiting; the button stops the search.
    /// </summary>
    private async Task FindUserFileAsync(UserFileKind kind)
    {
        var name = UserFileStore.FileNameFor(kind);
        var state = new SearchState { Status = $"Looking for {name}…" };
        _searches[kind] = state;
        BuildUserFileRows();

        var games = AppServices.Library.Current.Select(g => g.InstallDir).ToList();
        // For the 4.1.1b file only Downloads, Desktop and Documents, never AMD-signed copies or OptiScaler packages.
        if (UserFileStore.MustBeModified(kind)) state.Status = $"Looking for a modified {name} in Downloads, Desktop and Documents…";
        var result = await AppServices.UserFiles.FindAsync(kind, games,
            new Progress<string>(s => { if (state.Running) { state.Status = s; RenderSearch(kind); } }),
            new Progress<FoundFile>(f =>
            {
                if (state.Found.Any(x => x.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))) return;
                state.Found.Add(f);
                RenderSearch(kind);
            }),
            state.Cts.Token);

        // The search may finish before its last progress reports arrive, so its own list is the complete one.
        state.Found = result.Found;
        state.Running = false;
        state.Status = result.Cancelled
            ? state.Found.Count == 0 ? "Search cancelled." : $"Search cancelled. Found {state.Found.Count} so far:"
            : state.Found.Count == 0
                ? UserFileStore.MustBeModified(kind)
                    ? "No modified copy found in Downloads, Desktop or Documents (AMD's ordinary signed copies are skipped). Use Select file… if it's elsewhere."
                    : "No copy found on this PC. Download the file first, then use Select file…"
                : $"Search finished. Found {state.Found.Count}:";
        state.Cts.Dispose();
        if (_searches.TryGetValue(kind, out var current) && current == state) BuildUserFileRows();
    }

    /// <summary>The status line and the found files under a row, each with "Use this one".</summary>
    private void RenderSearch(UserFileKind kind)
    {
        if (!_resultPanels.TryGetValue(kind, out var panel)) return;
        panel.Children.Clear();
        if (!_searches.TryGetValue(kind, out var state)) return;

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (state.Running) statusRow.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
        statusRow.Children.Add(new TextBlock { Text = state.Status, FontSize = 12, Foreground = Brush("SubtleTextBrush"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(statusRow);

        foreach (var file in state.Found)
        {
            var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(6), Background = Brush("LayerFillColorDefaultBrush") };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var texts = new StackPanel { Spacing = 1 };
            texts.Children.Add(new TextBlock { Text = file.Path, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            texts.Children.Add(new TextBlock
            {
                Text = $"Version {VersionText(kind, file.Version)} · Modified {file.Modified:d MMM yyyy HH:mm}",
                FontSize = 12, Foreground = Brush("SubtleTextBrush")
            });
            grid.Children.Add(texts);
            var use = new Button { Content = "Use this one", VerticalAlignment = VerticalAlignment.Center };
            use.Click += async (_, _) => await UseFoundAsync(kind, file.Path);
            Grid.SetColumn(use, 1);
            grid.Children.Add(use);
            panel.Children.Add(grid);
        }
    }

    private async Task UseFoundAsync(UserFileKind kind, string path)
    {
        if (_searches.TryGetValue(kind, out var state))
        {
            if (state.Running) state.Cts.Cancel();
            _searches.Remove(kind);
        }
        await ImportAsync(kind, path);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        foreach (var s in _searches.Values.Where(s => s.Running)) s.Cts.Cancel();
    }

    private async Task ImportAsync(UserFileKind kind, string path)
    {
        try
        {
            await AppServices.UserFiles.ImportAsync(kind, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (_dialogOpen) return;
            _dialogOpen = true;
            try
            {
                await new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "That file wasn't added",
                    Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK"
                }.ShowAsync();
            }
            finally { _dialogOpen = false; }
        }
        BuildUserFileRows();
    }

    // ---------------- Updates ----------------

    private void AutoCheck_Toggled(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.Settings.Current;
        settings.AutoCheckUpdates = AutoCheckSwitch.IsOn;
        AppServices.Settings.Save(settings);
    }

    /// <summary>Saves the address and fetches it straight away; a valid, newer catalog is used from the next start.</summary>
    private async void SaveCatalogUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = CatalogUrlBox.Text.Trim();
        if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
        {
            CatalogStatusText.Text = "Enter an https:// address, or leave it empty to use Upshift's online catalog.";
            return;
        }
        var settings = AppServices.Settings.Current;
        settings.CatalogUrl = url;
        AppServices.Settings.Save(settings);
        CatalogStatusText.Text = "Downloading the catalog…";
        AppServices.CatalogStatus = await Core.Catalog.RemoteCatalog.RefreshAsync(AppServices.DataDir, settings.EffectiveCatalogUrl, AppServices.BuiltInCatalog, AppInfo.UserAgent, CancellationToken.None);
        CatalogStatusText.Text = AppServices.CatalogStatus;
    }

    private async void SaveSteamGridDbKey_Click(object sender, RoutedEventArgs e)
    {
        var key = SteamGridDbKeyBox.Password.Trim();
        try
        {
            var settings = AppServices.Settings.Current;
            settings.SteamGridDbKey = key.Length == 0 ? null : key;
            AppServices.Settings.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SteamGridDbStatusText.Text = $"Couldn't save: {ex.Message}";
            return;
        }

        // Games that found nothing before get another try with the new key.
        await AppServices.Library.ForgetArtworkMissesAsync();
        AppServices.RaiseArtworkSettingsChanged();
        SteamGridDbStatusText.Text = key.Length == 0
            ? "Saved. SteamGridDB won't be used."
            : "Saved. Games without a cover will be looked up again in the background.";
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        GpuList.ItemsSource = await AppServices.GetAllGpusAsync();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppServices.DataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppServices.DataDir}\"") { UseShellExecute = true });
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var cache = Path.Combine(AppServices.DataDir, "library.json");
        if (File.Exists(cache)) File.Delete(cache);
        CacheStatusText.Text = "Done. The next scan starts fresh. Folders you added yourself are kept.";
    }
}
