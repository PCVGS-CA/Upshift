using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Upshift.App.Services;
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
            ?? (AppServices.UsingRemoteCatalog ? "Using the catalog from this address." : "Using the built-in catalog.");
        BuildUserFileRows();

        AboutLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(
            new Uri(Path.Combine(AppInfo.AssetsDir, "Upshift.svg")));
        AboutVersionText.Text = $"Version {AppInfo.Version}";
        ReleasesLink.NavigateUri = new Uri(AppInfo.RepoUrl + "/releases");
        BuildHiddenRows();
        ShowAppUpdate();
        AppUpdates.Changed += AppUpdates_Changed;
        Unloaded += (_, _) => AppUpdates.Changed -= AppUpdates_Changed;
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

    // ---------------- App updates (About) ----------------

    private void AppUpdates_Changed() => DispatcherQueue.TryEnqueue(ShowAppUpdate);

    private void ShowAppUpdate()
    {
        var state = AppUpdates.State;
        AppUpdateText.Text = AppUpdates.Message;
        AppUpdateText.Visibility = AppUpdates.Message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CheckAppUpdateButton.IsEnabled = state is not (AppUpdateState.Checking or AppUpdateState.Downloading);
        RestartToUpdateButton.Visibility = state is AppUpdateState.Available or AppUpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        RestartToUpdateButton.IsEnabled = state == AppUpdateState.Available;
        ReleasesLink.Visibility = state == AppUpdateState.NotInstalled ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CheckAppUpdate_Click(object sender, RoutedEventArgs e) => await AppUpdates.CheckAsync();

    private async void RestartToUpdate_Click(object sender, RoutedEventArgs e) => await AppUpdates.DownloadAndRestartAsync();

    // ---------------- Optional files you supply ----------------

    private static readonly (UserFileKind Kind, string Title, string Use)[] UserFileKinds =
    {
        (UserFileKind.DlssNr, "DLSS 5 (nvngx_dlssnr.dll)", "Used by a later version of this app."),
        (UserFileKind.Fsr4Int8, "FSR 4.0.2c INT8 (amdxcffx64.dll)", "Used by the \"FSR 4 with your own 4.0.2c file\" option in a game's OptiScaler options.")
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
                    : $"Version {VersionText(kind, info.Version)} · SHA-256 {info.Sha256} · Community build, can't be verified. {use}"
            });

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
            remove.Click += (_, _) =>
            {
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
        var result = await AppServices.UserFiles.FindOnDrivesAsync(name, games,
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
                ? "No copy found on this PC. Download the file first, then use Select file…"
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
            CatalogStatusText.Text = "Enter an https:// address, or leave it empty to use the built-in catalog.";
            return;
        }
        var settings = AppServices.Settings.Current;
        settings.CatalogUrl = url;
        AppServices.Settings.Save(settings);
        if (url.Length == 0)
        {
            AppServices.CatalogStatus = null;
            CatalogStatusText.Text = AppServices.UsingRemoteCatalog
                ? "Saved. The built-in catalog is used from the next start."
                : "Saved. Using the built-in catalog.";
            return;
        }
        CatalogStatusText.Text = "Downloading the catalog…";
        AppServices.CatalogStatus = await Core.Catalog.RemoteCatalog.RefreshAsync(AppServices.DataDir, url, AppServices.BuiltInCatalog, AppInfo.UserAgent, CancellationToken.None);
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
