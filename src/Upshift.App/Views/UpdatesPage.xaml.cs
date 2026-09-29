using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Upshift.App.Services;
using Upshift.Core.Catalog;
using Upshift.Core.Components;
using Upshift.Core.Install;
using Upshift.Core.Services;

namespace Upshift.App.Views;

/// <summary>
/// Games that can be updated, then one compact row per component: "Used now" (OptiScaler and what ships inside it)
/// and, collapsed, "Used in a later version". Each row has its versions and one main button; channel, "Keep updated",
/// the version picker, "Remove downloaded copy" and "What's new" sit in the row's "More options". Built in code.
/// </summary>
public sealed partial class UpdatesPage : Page
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    /// <summary>What a row shows beyond the saved settings: the picked version, a status line, busy, More options open.</summary>
    private sealed class RowState
    {
        public string? Picked { get; set; }
        public string? Message { get; set; }
        public bool Busy { get; set; }
        public bool MoreOpen { get; set; }
    }

    // Kept for the session, so coming back to the page shows it as it was left.
    private static readonly Dictionary<string, RowState> Rows = new();
    private static bool _laterOpen;

    private readonly HashSet<string> _unchecked = new();
    private string? _gamesMessage;
    private bool _updatingGames;

    private const double NameWidth = 230, OnPcWidth = 230, LatestWidth = 190;

    public UpdatesPage()
    {
        InitializeComponent();
        LaterExpander.IsExpanded = _laterOpen;
        LaterExpander.Expanding += (_, _) => _laterOpen = true;
        LaterExpander.Collapsed += (_, _) => _laterOpen = false;
        Rebuild();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.Updates.Changed += OnUpdatesChanged;
        GameUpdates.LibraryChanged += OnUpdatesChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        AppServices.Updates.Changed -= OnUpdatesChanged;
        GameUpdates.LibraryChanged -= OnUpdatesChanged;
    }

    private void OnUpdatesChanged() => _dispatcher.TryEnqueue(Rebuild);

    private static Microsoft.UI.Xaml.Media.Brush Brush(string key) => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];

    private static RowState StateFor(string id) => Rows.TryGetValue(id, out var s) ? s : Rows[id] = new RowState();

    private void Rebuild()
    {
        var updates = AppServices.Updates;
        var settings = AppServices.Settings.Current;
        var all = AppServices.Catalog.Components;

        var candidates = GameUpdates.Candidates();
        var rows = all.Where(c => c.GroupWith is null && c.BundledIn is null)
            .Select(c => (Primary: c, Members: all.Where(m => m == c || m.GroupWith == c.Id).ToList()))
            .ToList();
        var componentUpdates = rows.Count(r => MainAction(r.Primary, r.Members).Kind == ActionKind.Update);

        // ---- one status line ----
        CheckNowButton.IsEnabled = !updates.IsChecking;
        CheckingRing.IsActive = updates.IsChecking;
        CheckingRing.Visibility = updates.IsChecking ? Visibility.Visible : Visibility.Collapsed;
        var available = componentUpdates + candidates.Count;
        CheckStatusText.Text = updates.IsChecking
            ? "Checking GitHub…"
            : (settings.LastUpdateCheckUtc is { } last ? $"Checked {Checked(last.ToLocalTime())}" : "Not checked yet")
              + $" · {(available == 0 ? "no updates available" : available == 1 ? "1 update available" : $"{available} updates available")}"
              + (settings.AutoCheckUpdates ? "" : " · automatic checks are off");
        ToolTipService.SetToolTip(CheckStatusText, updates.LastSummary);

        var limited = AppServices.Components.GitHub.RateLimitedUntil;
        RateLimitBar.IsOpen = limited is not null;
        RateLimitBar.Visibility = limited is null ? Visibility.Collapsed : Visibility.Visible;
        RateLimitBar.Message = limited is null ? "" : GitHubRateLimitException.MessageFor(limited);

        CatalogText.Text = AppServices.CatalogStatus
                           ?? (AppServices.UsingRemoteCatalog
                               ? $"Using the online catalog (updated {AppServices.Catalog.Updated})."
                               : $"Using the built-in catalog (updated {AppServices.Catalog.Updated}).");

        BuildGames(candidates);
        BuildDlss();

        // ---- Used now: what the app installs today, each followed by what ships inside it ----
        UsedNowPanel.Children.Clear();
        LaterPanel.Children.Clear();
        foreach (var (primary, members) in rows)
        {
            var panel = primary.Installable ? UsedNowPanel : LaterPanel;
            panel.Children.Add(BuildRow(primary, members));
            foreach (var bundled in all.Where(c => c.BundledIn == primary.Id))
                panel.Children.Add(BuildBundledRow(bundled, primary));
        }
    }

    private static string Checked(DateTime local) =>
        local.Date == DateTime.Today ? $"today at {local:t}" : local.Date == DateTime.Today.AddDays(-1) ? $"yesterday at {local:t}" : $"{local:d MMM} at {local:t}";

    // ---------------- games ----------------

    private void BuildGames(List<GameUpdateCandidate> candidates)
    {
        GamesPanel.Children.Clear();
        var installedByUs = AppServices.Library.Current.Count(g =>
            g.TargetDir is not null && OptiScalerInstaller.ReadManifest(g.TargetDir) is { Removed: false });
        GamesBorder.Visibility = installedByUs == 0 ? Visibility.Collapsed : Visibility.Visible;

        GamesPanel.Children.Add(new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            Text = candidates.Count switch
            {
                0 => $"All {installedByUs} game{(installedByUs == 1 ? "" : "s")} with OptiScaler from this app are up to date",
                1 => "1 game can be updated",
                _ => $"{candidates.Count} games can be updated"
            }
        });

        var boxes = new List<(CheckBox Box, GameUpdateCandidate Candidate)>();
        foreach (var c in candidates)
        {
            var box = new CheckBox
            {
                Content = $"{c.Game.Name}: {c.Installed} → {c.Target}",
                IsChecked = !_unchecked.Contains(c.Game.Id),
                IsEnabled = !_updatingGames
            };
            box.Checked += (_, _) => _unchecked.Remove(c.Game.Id);
            box.Unchecked += (_, _) => _unchecked.Add(c.Game.Id);
            boxes.Add((box, c));
            GamesPanel.Children.Add(box);
        }

        if (candidates.Count > 0)
        {
            var update = new Button
            {
                Content = "Update selected",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                IsEnabled = !_updatingGames && !GameUpdates.IsBusy
            };
            update.Click += async (_, _) => await UpdateGamesAsync(boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Candidate).ToList());
            GamesPanel.Children.Add(update);
            GamesPanel.Children.Add(Subtle("Each game's files are saved first, so its update can be undone from the Library. OptiScaler settings are kept. Games with anti-cheat are never updated."));
        }
        if (_gamesMessage is not null)
            GamesPanel.Children.Add(new TextBlock { Text = _gamesMessage, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
    }

    private async Task UpdateGamesAsync(List<GameUpdateCandidate> selected)
    {
        if (_updatingGames || selected.Count == 0) return;
        _updatingGames = true;
        var lines = new List<string>();
        try
        {
            foreach (var c in selected)
            {
                _gamesMessage = string.Join("\n", lines.Append($"{c.Game.Name}: updating to {c.Target}…"));
                Rebuild();
                var result = await GameUpdates.UpdateAsync(c.Game, c.Target, new Progress<string>(s =>
                {
                    _gamesMessage = string.Join("\n", lines.Append($"{c.Game.Name}: {s}"));
                    Rebuild();
                }));
                lines.Add($"{c.Game.Name}: {result.Message}");
            }
        }
        finally
        {
            _updatingGames = false;
            _gamesMessage = string.Join("\n", lines);
            Rebuild();
        }
    }

    // ---------------- Update DLSS in all games ----------------

    private readonly HashSet<string> _dlssUnchecked = new();
    private string? _dlssMessage;
    private bool _updatingDlss;

    private void BuildDlss()
    {
        DlssPanel.Children.Clear();
        var candidates = UpscalerUpdates.DlssCandidates()
            .Where(c => !AppServices.Settings.Current.HiddenGames.ContainsKey(c.Game.Id)).ToList();
        DlssBorder.Visibility = candidates.Count == 0 && _dlssMessage is null ? Visibility.Collapsed : Visibility.Visible;

        DlssPanel.Children.Add(new TextBlock { Text = "Update DLSS in all games", FontWeight = FontWeights.SemiBold });
        if (candidates.Count > 0)
        {
            var target = candidates[0].Target;
            var name = Core.Catalog.DlssNames.Name(AppServices.Catalog.DlssVersionNames, target) ?? "The newest DLSS";
            DlssPanel.Children.Add(Subtle(
                $"{name} ({target}) can replace the older DLSS file in {(candidates.Count == 1 ? "1 game" : $"{candidates.Count} games")}. " +
                "Games with anti-cheat are left out."));
        }

        var boxes = new List<(CheckBox Box, DlssCandidate Candidate)>();
        foreach (var c in candidates)
        {
            var box = new CheckBox
            {
                Content = $"{c.Game.Name}: {Helpers.Ui.DlssLabel(c.Current)} → {Helpers.Ui.DlssLabel(c.Target)}",
                IsChecked = !_dlssUnchecked.Contains(c.Game.Id),
                IsEnabled = !_updatingDlss
            };
            box.Checked += (_, _) => _dlssUnchecked.Remove(c.Game.Id);
            box.Unchecked += (_, _) => _dlssUnchecked.Add(c.Game.Id);
            boxes.Add((box, c));
            DlssPanel.Children.Add(box);
        }

        if (candidates.Count > 0)
        {
            var update = new Button
            {
                Content = "Update selected",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                IsEnabled = !_updatingDlss && !GameUpdates.IsBusy
            };
            AutomationName(update, "Update selected DLSS");
            update.Click += async (_, _) => await UpdateDlssAsync(boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Candidate).ToList());
            DlssPanel.Children.Add(update);
            DlssPanel.Children.Add(Subtle("Each game's original DLSS files are backed up first; \"Restore original files\" in the Library puts them back."));
        }
        if (_dlssMessage is not null)
            DlssPanel.Children.Add(new TextBlock { Text = _dlssMessage, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
    }

    private async Task UpdateDlssAsync(List<DlssCandidate> selected)
    {
        if (_updatingDlss || selected.Count == 0) return;
        _updatingDlss = true;
        var lines = new List<string>();
        try
        {
            foreach (var c in selected)
            {
                _dlssMessage = string.Join("\n", lines.Append($"{c.Game.Name}: updating DLSS…"));
                Rebuild();
                var result = await UpscalerUpdates.UpdateAsync(c.Game, UpscalerUpdates.DlssPaths(c.Game), new Progress<string>(s =>
                {
                    _dlssMessage = string.Join("\n", lines.Append($"{c.Game.Name}: {s}"));
                    Rebuild();
                }));
                lines.Add($"{c.Game.Name}: {result.Message}");
            }
        }
        finally
        {
            _updatingDlss = false;
            _dlssMessage = string.Join("\n", lines);
            Rebuild();
        }
    }

    // ---------------- component rows ----------------

    private enum ActionKind { Download, Update, UpToDate, NoDownload, NotChecked }

    /// <summary>The row's main button: download or update to the channel's version, or why there's nothing to do.</summary>
    private static (ActionKind Kind, ReleaseInfo? Target) MainAction(CatalogComponent primary, List<CatalogComponent> members)
    {
        var target = AppServices.Updates.Target(primary);
        if (target is null) return (ActionKind.NotChecked, null);
        var downloadable = members.Where(m => ComponentStore.IsDownloadable(m, target)).ToList();
        if (downloadable.Count == 0) return (ActionKind.NoDownload, target);
        if (downloadable.All(m => AppServices.Components.TryGetCached(m, target.Tag) is not null)) return (ActionKind.UpToDate, target);
        var onPc = NewestOnPc(primary);
        return onPc is not null && AppServices.Updates.IsNewer(primary, target.Tag, onPc) ? (ActionKind.Update, target) : (ActionKind.Download, target);
    }

    /// <summary>The newest downloaded version (by publish date), or null.</summary>
    private static string? NewestOnPc(CatalogComponent component)
    {
        string? newest = null;
        foreach (var version in AppServices.Components.Downloaded(component).Select(d => d.Version))
            if (newest is null || AppServices.Updates.IsNewer(component, version, newest)) newest = version;
        return newest;
    }

    private Expander BuildRow(CatalogComponent primary, List<CatalogComponent> members)
    {
        var state = StateFor(primary.Id);
        var status = AppServices.Updates.Status(primary.Id);
        var (kind, target) = MainAction(primary, members);

        var downloaded = AppServices.Components.Downloaded(primary);
        var onPc = NewestOnPc(primary);
        var onPcText = onPc is null ? "Not downloaded" : downloaded.Count > 1 ? $"{V(primary, onPc)} (+{downloaded.Count - 1} older)" : V(primary, onPc);
        var beta = AppServices.Settings.Current.For(primary.Id).Channel == UpdateChannel.Beta;

        var main = new Button
        {
            MinWidth = 150,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsEnabled = kind is ActionKind.Download or ActionKind.Update && !state.Busy,
            Content = kind switch
            {
                ActionKind.Download => "Download",
                ActionKind.Update => $"Update to {V(primary, target!.Tag)}",
                ActionKind.UpToDate => "Up to date",
                ActionKind.NoDownload => "No download",
                _ => "Not checked yet"
            }
        };
        if (kind == ActionKind.Update) main.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        if (kind == ActionKind.NoDownload)
            ToolTipService.SetToolTip(main, $"{target!.Tag} has no file on GitHub that this app can download.");
        main.Click += async (_, _) => await DownloadAsync(members, target!, state);

        var header = RowGrid(
            NameCell(members.Count > 1 ? $"{primary.Name} plus {string.Join(", ", members.Skip(1).Select(m => m.Kind))}" : primary.Name),
            Cell($"On this PC: {onPcText}"),
            Cell($"Latest: {(target is null ? "not checked yet" : V(primary, target.Tag))}{(beta && target is not null ? " (Beta)" : "")}"),
            state.Busy ? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { new ProgressRing { IsActive = true, Width = 16, Height = 16 }, main } } : main);

        var expander = new Expander
        {
            Header = header,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = state.MoreOpen,
            Content = MoreOptions(primary, members, status, state)
        };
        AutomationName(expander, primary.Name);
        expander.Expanding += (_, _) => state.MoreOpen = true;
        expander.Collapsed += (_, _) => state.MoreOpen = false;
        return expander;
    }

    /// <summary>"More options": channel, Keep updated, the version picker with Download and Remove, and What's new.</summary>
    private StackPanel MoreOptions(CatalogComponent primary, List<CatalogComponent> members, ComponentStatus status, RowState state)
    {
        var store = AppServices.Components;
        var pref = AppServices.Settings.Current.For(primary.Id);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Subtle(primary.Description));
        if (status.Problem is not null) panel.Children.Add(Subtle(status.Problem));
        if (status.CheckedUtc is { } at)
            panel.Children.Add(Subtle($"GitHub checked {Checked(at.ToLocalTime().DateTime)}{(status.Status == FetchStatus.NotModified ? " (not modified)" : "")}."));

        var channel = new ComboBox { Header = "Channel", MinWidth = 260 };
        var stableKind = primary.PinnedVersion is null ? "newest full release" : "pinned";
        channel.Items.Add(new ComboBoxItem { Content = $"Stable ({stableKind}{(status.Stable is { } s ? ": " + V(primary, s.Tag) : "")})" });
        channel.Items.Add(new ComboBoxItem { Content = $"Beta (newest, including pre-releases{(status.Beta is { } b ? ": " + V(primary, b.Tag) : "")})" });
        channel.SelectedIndex = pref.Channel == UpdateChannel.Beta ? 1 : 0;
        channel.SelectionChanged += (_, _) =>
        {
            var settings = AppServices.Settings.Current;
            settings.For(primary.Id).Channel = channel.SelectedIndex == 1 ? UpdateChannel.Beta : UpdateChannel.Stable;
            AppServices.Settings.Save(settings);
            state.Picked = null;
            AppServices.Updates.NotifyChanged(); // Library cards and the games list follow the channel
            if (settings.For(primary.Id).KeepUpdated) _ = DownloadKeptAsync();
        };
        var keep = new ToggleSwitch { Header = "Keep updated", IsOn = pref.KeepUpdated, OnContent = "Download new releases", OffContent = "Off" };
        keep.Toggled += (_, _) =>
        {
            var settings = AppServices.Settings.Current;
            settings.For(primary.Id).KeepUpdated = keep.IsOn;
            AppServices.Settings.Save(settings);
            if (keep.IsOn) _ = DownloadKeptAsync();
        };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children = { channel, keep } });

        // Version picker: every listed release, newest published first.
        var releases = status.Releases.ToList();
        if (status.Stable is { } stable && releases.All(r => r.Tag != stable.Tag)) releases.Add(stable); // pinned but older than the listed ones
        var picked = releases.FirstOrDefault(r => r.Tag == state.Picked) ?? status.For(pref.Channel) ?? releases.FirstOrDefault();
        var versions = new ComboBox { Header = "Version", MinWidth = 260, IsEnabled = releases.Count > 0 };
        foreach (var r in releases)
        {
            var notes = new List<string>();
            if (r.Tag == status.Stable?.Tag) notes.Add("Stable");
            if (r.Prerelease) notes.Add("pre-release");
            if (r.Published is { } p) notes.Add(p.LocalDateTime.ToString("d MMM yyyy"));
            if (!members.Any(m => ComponentStore.IsDownloadable(m, r))) notes.Add("no download");
            if (members.Any(m => store.TryGetCached(m, r.Tag) is not null)) notes.Add("on this PC");
            versions.Items.Add(new ComboBoxItem { Content = $"{V(primary, r.Tag)} · {string.Join(", ", notes)}", Tag = r.Tag });
        }
        if (picked is not null) versions.SelectedIndex = releases.IndexOf(picked);
        versions.SelectionChanged += (_, _) =>
        {
            state.Picked = (versions.SelectedItem as ComboBoxItem)?.Tag as string;
            Rebuild();
        };

        var downloadable = picked is not null && members.Any(m => ComponentStore.IsDownloadable(m, picked));
        var allCached = picked is not null && members.Where(m => ComponentStore.IsDownloadable(m, picked)).All(m => store.TryGetCached(m, picked.Tag) is not null);
        var anyCached = picked is not null && members.Any(m => store.TryGetCached(m, picked.Tag) is not null);
        var download = new Button
        {
            Content = picked is null ? "Download" : $"Download {V(primary, picked.Tag)}",
            IsEnabled = downloadable && !allCached && !state.Busy,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        download.Click += async (_, _) => await DownloadAsync(members, picked!, state);
        var remove = new Button { Content = "Remove downloaded copy", IsEnabled = anyCached && !state.Busy, VerticalAlignment = VerticalAlignment.Bottom };
        remove.Click += (_, _) =>
        {
            state.Message = null;
            foreach (var m in members)
            {
                try { store.Remove(m, picked!.Tag); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { state.Message = $"Couldn't remove it: {ex.Message}"; }
            }
            state.Message ??= $"Removed the downloaded copy of {picked!.Tag}. Games it's installed in are not affected.";
            Rebuild();
        };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { versions, download, remove } });
        if (state.Message is not null)
            panel.Children.Add(new TextBlock { Text = state.Message, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });

        // What's new in the picked version: plain-text notes and its GitHub page.
        if (picked is not null)
        {
            panel.Children.Add(new TextBlock { Text = $"What's new in {V(primary, picked.Tag)}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = 280,
                Content = new TextBlock
                {
                    Text = PlainText(picked.Notes) is { Length: > 0 } text ? text : "This release has no notes.",
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13
                }
            });
            panel.Children.Add(new HyperlinkButton
            {
                Content = $"{picked.Tag} on GitHub",
                Padding = new Thickness(0),
                NavigateUri = new Uri(picked.HtmlUrl.Length > 0 ? picked.HtmlUrl : $"https://github.com/{primary.Repo}/releases")
            });
        }
        return panel;
    }

    /// <summary>
    /// A component that ships inside another's release (fakenvapi, dlssg-to-fsr3 in OptiScaler): its version comes from
    /// the host's release notes, for the newest host version downloaded or installed. No download controls.
    /// </summary>
    private Border BuildBundledRow(CatalogComponent bundled, CatalogComponent host)
    {
        var hostVersion = NewestHostVersion(host);
        var version = hostVersion is null ? null : AppServices.Updates.BundledVersion(bundled, hostVersion);
        var release = version is null ? null
            : AppServices.Updates.Status(bundled.Id).Releases.FirstOrDefault(r => r.Tag.TrimStart('v', 'V') == version.TrimStart('v', 'V'));

        var link = new HyperlinkButton
        {
            Content = "What's new",
            HorizontalAlignment = HorizontalAlignment.Right,
            NavigateUri = new Uri(release?.HtmlUrl is { Length: > 0 } url ? url : $"https://github.com/{bundled.Repo}/releases")
        };
        ToolTipService.SetToolTip(link, release is null ? $"{bundled.Name} releases on GitHub" : $"{bundled.Name} {release.Tag} on GitHub");

        var grid = RowGrid(
            NameCell(bundled.Name, indent: true),
            Cell(hostVersion is null ? $"Included with {host.Name} (not downloaded yet)" : $"Included with {host.Name} {hostVersion}"),
            Cell(version is null ? "Version: not stated in the notes" : $"Version: {version}"),
            link);
        AutomationName(grid, bundled.Name);
        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 0, 0),
            Background = Brush("LayerFillColorDefaultBrush"),
            BorderBrush = Brush("HairlineBrush"),
            BorderThickness = new Thickness(1),
            Child = grid
        };
    }

    /// <summary>The newest host release (by publish date) that is downloaded or installed in a game.</summary>
    private static string? NewestHostVersion(CatalogComponent host)
    {
        var versions = AppServices.Components.Downloaded(host).Select(d => d.Version)
            .Concat(AppServices.Library.Current
                .Where(g => g.TargetDir is not null)
                .Select(g => OptiScalerInstaller.ReadManifest(g.TargetDir!))
                .Where(m => m is { Removed: false } && m.ComponentId == host.Id && m.Version is not null)
                .Select(m => m!.Version!))
            .Distinct();
        string? newest = null;
        foreach (var v in versions)
            if (newest is null || AppServices.Updates.IsNewer(host, v, newest)) newest = v;
        return newest;
    }

    /// <summary>A release tag as shown; NVIDIA DLSS tags get their marketing name, "v310.9.1 (DLSS 4.5)".</summary>
    private static string V(CatalogComponent component, string tag) =>
        component.RepoFilePath is { } file ? Helpers.Ui.VersionLabel(tag, file) : tag;

    private static Grid RowGrid(FrameworkElement name, FrameworkElement onPc, FrameworkElement latest, FrameworkElement action)
    {
        var grid = new Grid { ColumnSpacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NameWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(OnPcWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LatestWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (element, column) in new[] { (name, 0), (onPc, 1), (latest, 2), (action, 3) })
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }
        return grid;
    }

    private static TextBlock NameCell(string text, bool indent = false) => new()
    {
        Text = text, FontWeight = indent ? FontWeights.Normal : FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(indent ? 16 : 0, 0, 0, 0)
    };

    private static TextBlock Cell(string text) => new() { Text = text, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };

    private static TextBlock Subtle(string text) => new()
    {
        Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("SubtleTextBrush")
    };

    private static void AutomationName(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);

    private async Task DownloadAsync(List<CatalogComponent> members, ReleaseInfo release, RowState state)
    {
        state.Busy = true;
        state.Message = $"Downloading {release.Tag}…";
        Rebuild();
        var done = new List<string>();
        try
        {
            foreach (var member in members.Where(m => ComponentStore.IsDownloadable(m, release)))
            {
                var cached = await AppServices.Components.EnsureAsync(member, release.Tag, new Progress<string>(s =>
                {
                    state.Message = s;
                    Rebuild();
                }), CancellationToken.None);
                done.Add($"{member.Name} {cached.Version} ({cached.AssetName})");
            }
            state.Message = $"Downloaded and verified: {string.Join(", ", done)}.";
        }
        catch (ComponentDownloadException ex)
        {
            state.Message = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            state.Message = $"The download failed: {ex.Message}";
        }
        finally
        {
            state.Busy = false;
            Rebuild();
        }
    }

    private async Task DownloadKeptAsync()
    {
        await Task.Run(() => AppServices.Updates.DownloadKeptUpdatedAsync(CancellationToken.None));
        Rebuild();
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        try
        {
            await Task.Run(() => AppServices.Updates.CheckAsync(CancellationToken.None));
            await Task.Run(() => AppServices.Updates.DownloadKeptUpdatedAsync(CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            CheckNowButton.IsEnabled = true;
            Rebuild();
        }
    }

    /// <summary>Release notes (GitHub Markdown) as readable plain text.</summary>
    private static string PlainText(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n");
        text = Regex.Replace(text, @"<!--.*?-->", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", "");                  // images
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");               // links → their text
        text = Regex.Replace(text, @"<[^>]+>", "");                               // HTML tags
        text = Regex.Replace(text, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline); // headings
        text = Regex.Replace(text, @"^\s*[-*+]\s+", "• ", RegexOptions.Multiline);    // bullets
        text = Regex.Replace(text, @"(\*\*|__|`{1,3})", "");                      // bold, code
        text = Regex.Replace(text, @"(?<!\w)[_*](\S(?:[^_*\n]*\S)?)[_*](?!\w)", "$1"); // italics
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        text = text.Trim();
        return text.Length > 6000 ? text[..6000] + "…" : text;
    }
}
