using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Upshift.App.Services;
using Upshift.Core.Catalog;
using Upshift.Core.Components;
using Upshift.Core.Install;
using Upshift.Core.Models;

namespace Upshift.App.ViewModels;

/// <summary>The Install dialog's choices about earlier settings.</summary>
public sealed record InstallRestore(string SettingsFolder, bool RemoveLeftovers, string? RestoreFrom, bool RestoreFromLeftover, string? RestoreDefaultsFrom);

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly List<GameCardViewModel> _all = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _artCts;
    private Task? _artTask;
    private bool _initialized;
    private int _filterIndex;

    public ObservableCollection<GameCardViewModel> Games { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(HasNoSelection), nameof(CanPlaySelected))]
    private GameCardViewModel? selected;

    /// <summary>The game this page is installing, changing or updating right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlaySelected))]
    private string? busyGameId;

    /// <summary>Play is off while Upshift is installing or updating the selected game (here or on the Updates page).</summary>
    public bool CanPlaySelected => Selected is { } s && !IsGameBusy(s.Info.Id);

    private bool IsGameBusy(string gameId) => BusyGameId == gameId || GameUpdates.BusyGameId == gameId;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoGames), nameof(IsIdle))]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private string searchPlaceholder = "Search games";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoGames))]
    private int gameCount;

    public bool HasSelection => Selected is not null;
    public bool HasNoSelection => Selected is null;
    public bool HasNoGames => GameCount == 0 && !IsBusy;
    public bool IsIdle => !IsBusy;

    /// <summary>"DLSS 4.5 can be added to 3 of your games", after a new DLSS file finished downloading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDlssNotice))]
    private string dlssNotice = "";

    public bool HasDlssNotice => DlssNotice.Length > 0;

    /// <summary>The filter's label, naming what's out of date across the library: "DLSS + XeSS updates (7)".</summary>
    [ObservableProperty]
    private string upscalerFilterLabel = "Upscaler updates";

    /// <summary>The filter's tooltip: each technology with the games that have an update for it.</summary>
    [ObservableProperty]
    private string upscalerFilterTip = "No DLSS, FSR or XeSS file in your games has a newer version";

    private void RefreshUpscalerFilter()
    {
        var withUpdates = _all.Where(c => c.HasUpscalerUpdate).ToList();
        var families = withUpdates.SelectMany(c => c.UpscalerUpdateFamilies).Distinct()
            .OrderBy(f => f switch { "DLSS" => 0, "FSR" => 1, _ => 2 }).ToList();
        // Compact, so it fits the filter bar: "XeSS updates", "DLSS+XeSS updates"; the tooltip has the count and games.
        UpscalerFilterLabel = withUpdates.Count == 0 ? "Upscaler updates" : $"{string.Join("+", families)} updates";
        UpscalerFilterTip = withUpdates.Count == 0
            ? "No DLSS, FSR or XeSS file in your games has a newer version"
            : $"Upscaler update available in {(withUpdates.Count == 1 ? "1 game" : $"{withUpdates.Count} games")}:\n"
              + string.Join("\n", families.Select(f =>
                  $"{f}: {string.Join(", ", withUpdates.Where(c => c.UpscalerUpdateFamilies.Contains(f)).Select(c => c.Name))}"));
    }

    /// <summary>The DLSS name in the notice ("DLSS 4.5"), kept so the count can be refreshed after updates.</summary>
    private string? _dlssNoticeName;

    public void DismissDlssNotice()
    {
        _dlssNoticeName = null;
        DlssNotice = "";
    }

    private void RefreshDlssNotice()
    {
        if (_dlssNoticeName is null) return;
        var count = UpscalerUpdates.DlssCandidates().Count(c => !IsHidden(c.Game.Id));
        DlssNotice = count == 0 ? "" : $"{_dlssNoticeName} can be added to {count} of your games";
    }

    /// <summary>0 = All, 1 = Has upscalers, 2 = OptiScaler found, 3 = Blocked, 4 = Has suggestions, 5 = Updates available, 6 = Upscaler update available.</summary>
    public int FilterIndex
    {
        get => _filterIndex;
        set
        {
            if (SetProperty(ref _filterIndex, value)) ApplyFilter();
        }
    }

    public LibraryViewModel()
    {
        AppServices.ArtworkSettingsChanged += () => _dispatcher.TryEnqueue(StartBackgroundLookups);
        // A finished update check (or a new channel) can change which games have an update.
        AppServices.Updates.Changed += () => _dispatcher.TryEnqueue(() =>
        {
            foreach (var card in _all) card.RefreshUpdate();
            if (FilterIndex == 5) ReapplyFilterInPlace();
        });
        GameUpdates.BusyChanged += () => _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(CanPlaySelected)));
        // A game updated, undone or repaired from the Updates page.
        GameUpdates.LibraryChanged += gameId => _dispatcher.TryEnqueue(() => RefreshCard(gameId));
        AppServices.HiddenGamesChanged += () => _dispatcher.TryEnqueue(() =>
        {
            SetGames(AppServices.Library.Current);
            if (StatusText.Contains(" is hidden.", StringComparison.Ordinal)) StatusText = string.Empty;
        });
        // A newer DLSS file finished downloading: say how many games could use it.
        UpscalerUpdates.DlssDownloaded += source => _dispatcher.TryEnqueue(() =>
        {
            _dlssNoticeName = Core.Catalog.DlssNames.Name(AppServices.Catalog.DlssVersionNames, source.Version) ?? $"DLSS {source.Version}";
            RefreshDlssNotice();
        });
    }

    // ---------------- Hidden games ----------------

    private static bool IsHidden(string gameId) => AppServices.Settings.Current.HiddenGames.ContainsKey(gameId);

    /// <summary>Takes the game out of the Library; Settings > "Show hidden games" brings it back.</summary>
    public void HideGame(GameCardViewModel card)
    {
        try
        {
            var settings = AppServices.Settings.Current;
            settings.HiddenGames[card.Info.Id] = card.Name;
            AppServices.Settings.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Couldn't hide {card.Name}: {ex.Message}";
            return;
        }
        if (Selected == card) Selected = null;
        // Only this card goes; the rest of the list (and its scroll position) stays as it is.
        _all.Remove(card);
        Games.Remove(card);
        GameCount = Games.Count;
        SearchPlaceholder = _all.Count == 1 ? "Search 1 game" : $"Search {_all.Count} games";
        RefreshDlssNotice();
        RefreshUpscalerFilter();
        StatusText = $"{card.Name} is hidden. Settings > Hidden games brings it back.";
    }

    // ---------------- Upscaler files ----------------

    /// <summary>Updates the listed files (all that can be when null), downloading them first if needed.</summary>
    public Task<InstallResult?> UpdateUpscalerFilesAsync(GameCardViewModel card, IReadOnlyCollection<string>? paths) =>
        RunGameUpdateAsync(card, p => UpscalerUpdates.UpdateAsync(card.Info, paths, p));

    /// <summary>"Update DLSS": the game's DLSS Super Resolution and Ray Reconstruction files.</summary>
    public Task<InstallResult?> UpdateDlssAsync(GameCardViewModel card) =>
        UpdateUpscalerFilesAsync(card, UpscalerUpdates.DlssPaths(card.Info));

    // ---------------- DLSS 5 ----------------

    /// <summary>Replaces the game's OptiScaler with the DLSS 5 build for this card (see GameUpdates.SwitchToDlss5Async).</summary>
    public Task<InstallResult?> SwitchToDlss5Async(GameCardViewModel card, Core.Catalog.NeuralBackend backend, Core.Catalog.NeuralRuntime? runtime) =>
        RunGameUpdateAsync(card, p => GameUpdates.SwitchToDlss5Async(card.Info, backend, runtime, p));

    /// <summary>Back to the regular OptiScaler the game had before the switch.</summary>
    public Task<InstallResult?> SwitchBackAsync(GameCardViewModel card) =>
        RunGameUpdateAsync(card, p => GameUpdates.SwitchBackAsync(card.Info, p));

    /// <summary>Puts back every original Upshift replaced in this game.</summary>
    public Task<InstallResult?> RestoreUpscalerFilesAsync(GameCardViewModel card, IReadOnlyCollection<string>? paths = null) =>
        RunGameUpdateAsync(card, p => UpscalerUpdates.RestoreAsync(card.Info, paths, p));

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        var gpuTask = AppServices.GetGpuAsync();
        SetGames(await AppServices.Library.LoadCachedAsync());

        await gpuTask;
        // The recommendation lines and the DLSS 5 section need the graphics card, which is known from here on.
        foreach (var card in _all) card.RefreshSuggestions();
        AppServices.RaiseEffectiveGpuChanged();

        if (_all.Count == 0) await RunScanAsync(includeDriveScan: false);
        else StartBackgroundLookups();
    }

    [RelayCommand]
    private Task ScanAsync() => RunScanAsync(includeDriveScan: false);

    [RelayCommand]
    private Task DeepScanAsync() => RunScanAsync(includeDriveScan: true);

    [RelayCommand]
    private void CancelScan() => _cts?.Cancel();

    public async Task AddFolderAsync(string folder)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Checking the folder…";
        try
        {
            await StopBackgroundLookupsAsync();
            var info = await AppServices.Library.AddManualAsync(folder);
            SetGames(AppServices.Library.Current);
            Selected = _all.FirstOrDefault(g => g.Info.Id == info.Id);
            StatusText = string.Empty;
            StartBackgroundLookups();
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't add that folder: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunScanAsync(bool includeDriveScan)
    {
        if (IsBusy) return;
        IsBusy = true;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var progress = new Progress<string>(s => StatusText = s);

        try
        {
            await StopBackgroundLookupsAsync();
            SetGames(await AppServices.Library.ScanAsync(includeDriveScan, progress, cts.Token));
            ScrollToSelectedRequested?.Invoke();
            StatusText = string.Empty;
            StartBackgroundLookups();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled. Showing the games found so far last time.";
        }
        catch (Exception ex)
        {
            StatusText = $"The scan stopped with an error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cts = null;
            cts.Dispose();
        }
    }

    /// <summary>Uses a picture the user chose for this game.</summary>
    public async Task ChoosePictureAsync(GameCardViewModel card, string imageFile)
    {
        try
        {
            if (await AppServices.Library.SetCustomArtworkAsync(card.Info.Id, imageFile) is not null) card.RefreshArtwork();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Couldn't use that picture: {ex.Message}";
        }
    }

    /// <summary>Drops the user's picture; automatic cover art comes back, or is looked up if there is none.</summary>
    public async Task ResetPictureAsync(GameCardViewModel card)
    {
        if (await AppServices.Library.ResetArtworkAsync(card.Info.Id) is null) return;
        card.RefreshArtwork();
        if (!card.HasArtwork) StartBackgroundLookups();
    }

    /// <summary>
    /// Makes sure an OptiScaler release (the pinned Stable one unless another version is given) is downloaded and
    /// unpacked. Null (with a status message) if it couldn't be.
    /// </summary>
    public async Task<CachedComponent?> PrepareOptiScalerAsync(string? version = null)
    {
        if (IsBusy) return null;
        version ??= AppServices.OptiScaler.PinnedVersion!;
        if (AppServices.Components.TryGetCached(AppServices.OptiScaler, version) is { } cached) return cached;

        IsBusy = true;
        try
        {
            var component = await AppServices.Components.EnsureAsync(AppServices.OptiScaler, version, new Progress<string>(s => StatusText = s), CancellationToken.None);
            StatusText = string.Empty;
            return component;
        }
        catch (ComponentDownloadException ex)
        {
            StatusText = ex.Message;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = $"Couldn't get OptiScaler: {ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task<InstallResult?> InstallOptiScalerAsync(GameCardViewModel card, CachedComponent component, string proxyName, InstallRestore? restore = null) =>
        RunInstallAsync(card, new InstallPlan
        {
            Operation = InstallOperation.Install,
            GameId = card.Info.Id,
            GameName = card.Name,
            TargetDir = card.Info.TargetDir!,
            SourceDir = component.Folder,
            ComponentId = component.Id,
            Version = component.Version,
            ProxyName = proxyName,
            // The upscaler dropdown starts on the suggestion, so the fresh OptiScaler.ini gets it too.
            IniSettings = SuggestedUpscalerSettings(card),
            SettingsFolder = restore?.SettingsFolder ?? SavedSettings.FolderFor(AppServices.DataDir, card.Name),
            RemoveLeftovers = restore?.RemoveLeftovers ?? false,
            RestoreSettingsFrom = restore?.RestoreFrom,
            RestoreFromLeftover = restore?.RestoreFromLeftover ?? false,
            RestoreDefaultsFrom = restore?.RestoreDefaultsFrom
        }, "Installing OptiScaler…");

    private static List<IniSetting> SuggestedUpscalerSettings(GameCardViewModel card)
    {
        var choiceId = card.Recommendation?.ChoiceId;
        if (choiceId is null) return new();
        var choices = OptiScalerOptions.Upscalers(AppServices.Gpu, card.Info.Api, AppServices.Catalog.Fsr4, int8On: false);
        return choices.FirstOrDefault(c => c.Id == choiceId)?.Settings.ToList() ?? new();
    }

    /// <summary>Changes OptiScaler's settings (and/or a user-supplied file) for an installed game.</summary>
    public Task<InstallResult?> ConfigureOptiScalerAsync(GameCardViewModel card, IReadOnlyList<IniSetting> settings,
        string? addFileFrom = null, string? addFileAs = null, string? removeFile = null,
        string? overrideFrom = null, string? overrideAs = null, string? overrideKind = null, string? removeOverride = null) =>
        RunInstallAsync(card, new InstallPlan
        {
            Operation = InstallOperation.Configure,
            GameId = card.Info.Id,
            GameName = card.Name,
            TargetDir = card.Info.TargetDir!,
            IniSettings = settings.ToList(),
            AddFileFrom = addFileFrom,
            AddFileAs = addFileAs,
            RemoveFile = removeFile,
            OverrideFrom = overrideFrom,
            OverrideAs = overrideAs,
            OverrideKind = overrideKind,
            RemoveOverride = removeOverride
        }, "Saving OptiScaler settings…");

    /// <summary>Called after the user changes which wiki entry a game matches (or turns suggestions off).</summary>
    public void RefreshSuggestions(GameCardViewModel card)
    {
        card.RefreshSuggestions();
        ReapplyFilterInPlace();
    }

    public Task<InstallResult?> UpdateOptiScalerAsync(GameCardViewModel card, string version) =>
        RunGameUpdateAsync(card, p => GameUpdates.UpdateAsync(card.Info, version, p));

    public Task<InstallResult?> UndoUpdateAsync(GameCardViewModel card) =>
        RunGameUpdateAsync(card, p => GameUpdates.UndoAsync(card.Info, p));

    public Task<InstallResult?> RepairOptiScalerAsync(GameCardViewModel card) =>
        RunGameUpdateAsync(card, p => GameUpdates.RepairAsync(card.Info, p));

    // ---------------- Play ----------------

    /// <summary>Starts the game through its store (never as admin). A failure is shown on the game's details.</summary>
    public async Task PlayAsync(GameCardViewModel card)
    {
        if (IsGameBusy(card.Info.Id))
        {
            card.LaunchError = "Upshift is installing or updating this game. Play is available again when that's done.";
            return;
        }
        var options = card.LaunchOptions;
        var result = await Task.Run(() => Core.Launch.GameLauncher.Launch(card.Info, options));
        card.LaunchError = result.Success ? null : result.Message;
        StatusText = result.Success ? result.Message : string.Empty;
    }

    /// <summary>Saves the game's launch options (empty clears them).</summary>
    public void SetLaunchOptions(GameCardViewModel card, string options)
    {
        options = options.Trim();
        if (options == card.LaunchOptions) return;
        try
        {
            AppServices.LaunchOptions.Set(card.Info.Id, options);
            card.LaunchOptions = options;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Couldn't save the launch options: {ex.Message}";
        }
    }

    /// <summary>Adds one option (e.g. from a suggestion) to the game's launch options, if it isn't there yet.</summary>
    public void AddLaunchOption(GameCardViewModel card, string option)
    {
        var current = card.LaunchOptions.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (current.Contains(option, StringComparer.OrdinalIgnoreCase)) return;
        SetLaunchOptions(card, string.Join(' ', current.Append(option)));
    }

    private async Task<InstallResult?> RunGameUpdateAsync(GameCardViewModel card, Func<IProgress<string>, Task<InstallResult>> work)
    {
        if (IsBusy) return null;
        IsBusy = true;
        BusyGameId = card.Info.Id;
        try
        {
            var result = await work(new Progress<string>(s => StatusText = s));
            RefreshCard(card.Info.Id);
            StatusText = result.Success ? result.Message : string.Empty;
            return result;
        }
        finally
        {
            IsBusy = false;
            BusyGameId = null;
        }
    }

    public Task<InstallResult?> UninstallOptiScalerAsync(GameCardViewModel card) =>
        RunInstallAsync(card, new InstallPlan
        {
            Operation = InstallOperation.Uninstall,
            GameId = card.Info.Id,
            GameName = card.Name,
            TargetDir = card.Info.TargetDir!,
            // OptiScaler.ini is saved here before it's removed, for "Restore my previous OptiScaler settings".
            SettingsFolder = SavedSettings.FolderFor(AppServices.DataDir, card.Name)
        }, "Removing OptiScaler…");

    private async Task<InstallResult?> RunInstallAsync(GameCardViewModel card, InstallPlan plan, string busyText)
    {
        if (IsBusy || card.Info.TargetDir is null) return null;
        IsBusy = true;
        BusyGameId = card.Info.Id;
        StatusText = busyText;
        try
        {
            var result = await InstallRunner.RunAsync(plan);
            // Badge, filter and details pick up the change straight away.
            await AppServices.Library.ReanalyzeAsync(card.Info.Id);
            RefreshCard(card.Info.Id);
            StatusText = result.Success ? result.Message : string.Empty;
            return result;
        }
        finally
        {
            IsBusy = false;
            BusyGameId = null;
        }
    }

    /// <summary>Uses the exe the user picked as the game's main exe. It has to be inside the game's folder.</summary>
    public async Task ChangeExeAsync(GameCardViewModel card, string exePath)
    {
        var inside = Core.Util.PathUtil.IsSameOrInside(Core.Util.PathUtil.Normalize(exePath), Core.Util.PathUtil.Normalize(card.Info.InstallDir));
        if (!inside)
        {
            StatusText = "Pick an .exe inside the game's own folder.";
            return;
        }
        await ApplyExeAsync(card, exePath);
    }

    /// <summary>Goes back to the automatically picked exe.</summary>
    public Task UseAutomaticExeAsync(GameCardViewModel card) => ApplyExeAsync(card, null);

    private async Task ApplyExeAsync(GameCardViewModel card, string? exePath)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Checking the game again…";
        try
        {
            await StopBackgroundLookupsAsync();
            if (await AppServices.Library.SetExeOverrideAsync(card.Info.Id, exePath) is not null)
                RefreshCard(card.Info.Id); // keeps the same game selected
            StatusText = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Couldn't save that choice: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            StartBackgroundLookups();
        }
    }

    /// <summary>
    /// Fills in missing cover art, then gets each game's PCGamingWiki entry (cached for 30 days).
    /// Results appear on their cards as they arrive; being offline just means nothing happens.
    /// </summary>
    private void StartBackgroundLookups()
    {
        _artCts?.Cancel();
        var cts = new CancellationTokenSource();
        _artCts = cts;
        var previous = _artTask;

        _artTask = Task.Run(async () =>
        {
            if (previous is not null)
            {
                try { await previous; } catch { /* already reported nothing; it's only cover art */ }
            }
            try
            {
                await AppServices.Library.FillArtworkAsync(AppServices.Artwork, AppServices.Catalog.ArtworkSources,
                    game => _dispatcher.TryEnqueue(() => _all.FirstOrDefault(c => c.Info.Id == game.Id)?.RefreshArtwork()),
                    cts.Token);
                // Cards without a cached answer say "Checking…" until theirs arrives.
                _dispatcher.TryEnqueue(() =>
                {
                    foreach (var card in _all.Where(c => AppServices.WikiCache.Get(c.Info.Id) is null)) card.ApplyWiki(null, checking: true);
                });
                await AppServices.Library.FillWikiAsync(AppServices.WikiClient, AppServices.WikiCache, AppServices.Catalog.PcGamingWiki,
                    (game, entry) => _dispatcher.TryEnqueue(() =>
                    {
                        var card = _all.FirstOrDefault(c => c.Info.Id == game.Id);
                        card?.ApplyWiki(entry);
                        card?.RefreshEngine();
                    }),
                    cts.Token);

                // OptiScaler wiki: the Compatibility List and matched games' pages, refreshed weekly.
                await AppServices.Suggestions.RefreshAsync(AppServices.Library.Current, cts.Token);
                _dispatcher.TryEnqueue(() =>
                {
                    foreach (var card in _all) card.RefreshSuggestions();
                    if (FilterIndex == 4) ReapplyFilterInPlace();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* cover art, wiki data and suggestions are nice-to-haves; never let them disturb the Library */ }
        });
    }

    private async Task StopBackgroundLookupsAsync()
    {
        _artCts?.Cancel();
        if (_artTask is not null)
        {
            try { await _artTask; } catch { }
        }
    }

    private void SetGames(IReadOnlyList<GameInfo> games)
    {
        var selectedId = Selected?.Info.Id;
        _all.Clear();
        _all.AddRange(games.Where(g => !IsHidden(g.Id)).Select(g => new GameCardViewModel(g)));
        SearchPlaceholder = _all.Count == 1 ? "Search 1 game" : $"Search {_all.Count} games";
        ApplyFilter();
        Selected = selectedId is null ? null : _all.FirstOrDefault(g => g.Info.Id == selectedId);
        RefreshDlssNotice();
        RefreshUpscalerFilter();
    }

    private void ApplyFilter()
    {
        var visible = _all.Where(Matches);
        Games.Clear();
        foreach (var g in visible) Games.Add(g);
        GameCount = Games.Count;
    }

    /// <summary>
    /// Brings the shown cards in line with the filter without clearing the list: cards that stopped matching leave,
    /// cards that started matching come in at their place, and everything else (and the scroll position) stays.
    /// </summary>
    private void ReapplyFilterInPlace()
    {
        var wanted = _all.Where(Matches).ToList();
        for (var i = Games.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Games[i])) Games.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
            if (!Games.Contains(wanted[i])) Games.Insert(Math.Min(i, Games.Count), wanted[i]);
        GameCount = Games.Count;
    }

    /// <summary>True when the card passes the search box and the selected filter.</summary>
    private bool Matches(GameCardViewModel g)
    {
        var query = SearchText.Trim();
        return (query.Length == 0 || g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
               && FilterIndex switch
               {
                   1 => g.HasUpscalers,
                   2 => g.Info.HasOptiScaler,
                   3 => g.HasAntiCheat,
                   4 => g.HasWikiSuggestions,
                   5 => g.HasUpdate,
                   6 => g.HasUpscalerUpdate,
                   _ => true
               };
    }

    /// <summary>
    /// Rebuilds one game's card from the library after something changed that game (an install, uninstall, settings
    /// change or update) and swaps it in at the same place. The rest of the list, its scroll position and the
    /// selection stay as they are; the card only leaves the list if it no longer passes the filter.
    /// </summary>
    private void RefreshCard(string gameId)
    {
        var index = _all.FindIndex(c => c.Info.Id == gameId);
        var info = AppServices.Library.Current.FirstOrDefault(g => g.Id == gameId);
        if (index < 0 || info is null) return;

        var wasSelected = Selected?.Info.Id == gameId;
        var old = _all[index];
        var card = new GameCardViewModel(info);
        _all[index] = card;

        var shown = Games.IndexOf(old);
        if (shown >= 0)
        {
            if (Matches(card)) Games[shown] = card;
            else Games.RemoveAt(shown);
        }
        else if (Matches(card))
        {
            // Back into the filtered list, in library order.
            var position = _all.Take(index).Count(c => Games.Contains(c));
            Games.Insert(position, card);
        }
        GameCount = Games.Count;
        if (wasSelected) Selected = card;
        RefreshDlssNotice();
        RefreshUpscalerFilter();
    }

    /// <summary>Raised after a full rescan rebuilt the list, so the page can scroll back to the selected game.</summary>
    public event Action? ScrollToSelectedRequested;
}
