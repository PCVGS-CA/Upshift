using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Upshift.App.Helpers;
using Upshift.Core.Detection;
using Upshift.Core.Install;
using Upshift.Core.Wiki;
using Upshift.Core.Models;

namespace Upshift.App.ViewModels;

public sealed class ChipViewModel
{
    public ChipViewModel(string text, (string Background, string Foreground) colors, bool updated = false)
    {
        Text = text;
        Updated = updated;
        Background = Ui.Brush(colors.Background);
        Foreground = Ui.Brush(colors.Foreground);
    }

    public string Text { get; }

    /// <summary>Upshift updated one of this technology's files in the game: the chip shows a small up-arrow.</summary>
    public bool Updated { get; }
    public SolidColorBrush Background { get; }
    public SolidColorBrush Foreground { get; }
}

/// <summary>One line of "Changes Upshift made to this game".</summary>
public sealed class ChangeViewModel
{
    public ChangeViewModel(ChangeEntry entry)
    {
        Date = entry.Utc is { } utc ? utc.ToLocalTime().ToString("d MMM yyyy, HH:mm") : "Date not recorded";
        Text = entry.Text;
        Detail = entry.Detail ?? "";
    }

    public string Date { get; }
    public string Text { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>One game card in the Library grid, plus everything the details pane shows for it.</summary>
public sealed partial class GameCardViewModel : ObservableObject
{
    private static readonly string[] TilePalette =
    {
        "#3B2F5C", "#2A3F5F", "#5C2A36", "#2F4D6B", "#1F4D4A",
        "#4A3B2A", "#3D3D52", "#5A3E1C", "#28465A", "#4B2A4F"
    };

    public GameCardViewModel(GameInfo info)
    {
        // Fresh from the game folder, not the last scan: versions, OptiScaler and other mods, changed files.
        Core.Detection.GameAnalyzer.RefreshQuick(info);
        Info = info;
        Name = info.Name;
        SourceText = SourceName(info.Source);
        InstallDir = info.InstallDir;
        TargetText = info.TargetDir ?? info.InstallDir;
        ExeText = info.ExePath is null ? "Main game file not found" : Path.GetRelativePath(info.InstallDir, info.ExePath);
        ExeChosenByUser = info.ExeChosenByUser;
        ExeNote = info.ExePath is null ? "" : info.ExeChosenByUser ? "Chosen by you" : "Picked automatically";

        ApiText = ApiName(info.Api);
        RefreshEngine();
        BuildText = info.Is64Bit switch { true => "64-bit", false => "32-bit", null => "Unknown" };

        HasAntiCheat = info.HasAntiCheat;
        AntiCheatText = info.HasAntiCheat ? string.Join(", ", info.AntiCheat) : "None found";
        AntiCheatMessage = info.HasAntiCheat
            ? $"It uses {AntiCheatText}. Adding DLL files to online games can get your account banned, so nothing will be installed here."
            : "";

        TileBrush = Ui.Brush(TilePalette[(int)((uint)StableHash(info.Name) % TilePalette.Length)]);
        TileOpacity = info.HasAntiCheat ? 0.55 : 1.0;

        RefreshArtwork();

        // "Upscaler files": each DLSS, FSR and XeSS file this game has, and any newer one on offer. Versions come from
        // the files on disk, not the last scan, so the bar, badge, filter and buttons match what's really there.
        _fileItems = Services.UpscalerUpdates.Items(info);
        // One row per file that is the game's (or Upshift's update of it). OptiScaler's own copies get one line:
        // OptiScaler keeps them up to date itself, so Upshift doesn't compete with it.
        UpscalerFiles = _fileItems.Where(i => i.State != UpscalerFileState.OptiScalerCopy).Select(i => new UpscalerFileRowViewModel(i, this)).ToList();
        var optiCopies = _fileItems.Where(i => i.State == UpscalerFileState.OptiScalerCopy).Select(i => i.FileName).Distinct().ToList();
        OptiScalerFilesNote = optiCopies.Count == 0 ? ""
            : $"OptiScaler uses its own {(optiCopies.Count == 1 ? "copy" : "copies")} of {string.Join(", ", optiCopies)}, so Upshift leaves {(optiCopies.Count == 1 ? "it" : "them")} to OptiScaler's updates.";
        CanUpdateAnyFile = !info.HasAntiCheat && _fileItems.Any(i => i.CanUpdate);
        UpdatableFileCount = _fileItems.Count(i => i.CanUpdate);
        CanRestoreFiles = Core.Install.UpscalerFiles.ReadRecord(info.InstallDir) is { Files.Count: > 0 };
        if (Services.UpscalerUpdates.DlssUpdate(info) is { } dlss && dlss.CurrentVersion is { } dlssNow)
        {
            HasDlssBar = true;
            DlssBarText = UpscalerFileText.DlssBar(dlssNow, dlss.Target!.Version, Services.UpscalerUpdates.IsOnThisPc(dlss.Target));
        }
        var restored = _fileItems.Where(i => i.State == UpscalerFileState.GameRestoredOld).ToList();
        if (restored.Count > 0)
        {
            HasRestoredByGame = true;
            RestoredTitle = restored.All(r => r.Family == UpscalerFamily.Dlss) ? "Game restored its old DLSS file" : "Game restored its old upscaler files";
            RestoredText = string.Join(" ", restored.Select(r =>
                $"{r.FileName} is back to {(r.CurrentVersion is null ? "the game's old version" : Ui.VersionLabel(r.CurrentVersion, r.FileName))}, probably after a game update."));
            _restoredPaths = restored.Where(r => r.CanUpdate).Select(r => r.RelativePath).ToList();
        }

        ApplyWiki(AppServices.WikiCache.Get(info.Id));

        if (info.HasAntiCheat)
        {
            BadgeText = "Blocked";
            BadgeForeground = Ui.Brush("#8A2B12");
        }
        else if (info.HasOptiScaler)
        {
            BadgeText = "OptiScaler";
            BadgeForeground = Ui.Brush("#0D5A44");
        }
        HasBadge = BadgeText.Length > 0;

        // OptiScaler installed by this app (it has our manifest).
        var manifest = info.TargetDir is null ? null : OptiScalerInstaller.ReadManifest(info.TargetDir);
        IsInstalledByUs = manifest is { Removed: false };
        // "OptiScaler v0.9.4", or the DLSS 5 build switched in ("OptiScaler DLSSNR v0.2.0-dlssnr").
        InstalledComponentId = IsInstalledByUs ? manifest!.ComponentId : null;
        InstalledText = IsInstalledByUs
            ? $"{Services.GameUpdates.ComponentFor(manifest!.ComponentId).Name} {manifest.Version} installed as {manifest.ProxyName}" : "";
        InstalledVersion = IsInstalledByUs ? manifest!.Version : null;
        CanUndo = IsInstalledByUs && info.TargetDir is not null && OptiScalerInstaller.CanUndo(info.TargetDir);
        UndoText = CanUndo ? $"Undo last update (back to {manifest!.LastUpdate!.FromVersion})" : "";
        // Found by the last scan: files of our install that are missing or were changed.
        NeedsRepair = IsInstalledByUs && !info.HasAntiCheat && info.RepairProblems.Count > 0;
        RepairText = NeedsRepair ? string.Join(". ", info.RepairProblems) + "." : "";
        IniPath = info.TargetDir is null ? "" : Path.Combine(info.TargetDir, "OptiScaler.ini");
        // OptiScaler is 64-bit only, so 32-bit games can't use it.
        CanInstall = !info.HasAntiCheat && info.TargetDir is not null && !IsInstalledByUs && info.Is64Bit != false;

        Mods = info.ExistingMods
            .Where(m => !(IsInstalledByUs && m.Kind == ModKind.OptiScaler)) // shown on its own card instead
            .Select(m => m.FileName is null
                ? $"{m.Name}{(m.Version is null ? "" : " " + m.Version)}"
                : $"{m.Name}{(m.Version is null ? "" : " " + m.Version)} (installed as {m.FileName})")
            .ToList();
        HasMods = Mods.Count > 0;
        RefreshUpdate();

        // "Changes Upshift made to this game", newest first: OptiScaler's record and the upscaler files Upshift updated.
        var optiChanges = manifest is null || info.TargetDir is null
            ? new List<ChangeEntry>()
            : OptiScalerInstaller.Changes(info.TargetDir, manifest, Ui.VersionLabel);
        Changes = optiChanges.Concat(Core.Install.UpscalerFiles.Changes(info.InstallDir, Ui.VersionLabel))
            .OrderByDescending(c => c.Utc ?? DateTime.MinValue)
            .Select(c => new ChangeViewModel(c))
            .ToList();

        launchOptions = AppServices.LaunchOptions.Get(info.Id);
        (TakesLaunchOptions, LaunchOptionsNote) = Core.Launch.GameLauncher.LaunchOptionsSupport(info);
        PlayTip = Core.Launch.GameLauncher.Plan(info, null, out var launchProblem) is { } plan
            ? $"Starts {info.Name} through {plan.Via}"
            : launchProblem ?? "";
    }

    public List<ChangeViewModel> Changes { get; }
    public bool HasChanges => Changes.Count > 0;

    // ---------------- Upscaler files ----------------

    private readonly List<UpscalerFileItem> _fileItems;
    private readonly List<string> _restoredPaths = new();

    public List<UpscalerFileRowViewModel> UpscalerFiles { get; }
    public bool HasUpscalerFileRows => UpscalerFiles.Count > 0;
    public bool HasUpscalerFiles => UpscalerFiles.Count > 0 || OptiScalerFilesNote.Length > 0;

    /// <summary>"OptiScaler uses its own copies of libxess.dll, …, so Upshift leaves them to OptiScaler's updates."</summary>
    public string OptiScalerFilesNote { get; } = "";
    public bool HasOptiScalerFilesNote => OptiScalerFilesNote.Length > 0;

    /// <summary>The game ships DLSS Frame Generation, which Upshift deliberately doesn't update.</summary>
    public bool HasDlssFrameGenFile => Info.Upscalers.Any(u => u.FileName.Equals("nvngx_dlssg.dll", StringComparison.OrdinalIgnoreCase));

    /// <summary>At least one file has a newer version on offer (never for games with anti-cheat).</summary>
    public bool CanUpdateAnyFile { get; }
    public int UpdatableFileCount { get; }
    public string UpdateAllText => UpdatableFileCount > 1 ? $"Update all ({UpdatableFileCount})" : "Update all";

    /// <summary>Upshift has updated files here whose originals it can put back.</summary>
    public bool CanRestoreFiles { get; }

    /// <summary>Nothing newer for any of the game's own files: said quietly in the section, never as an error.</summary>
    public bool IsAlreadyUpToDate => HasUpscalerFileRows && UpdatableFileCount == 0;

    /// <summary>
    /// The collapsed "Upscaler files (advanced)" header's summary: "1 update available", "DLSS 310.9.1 (DLSS 4.5) ·
    /// updated by Upshift", "Game restored its old file", or "Already up to date".
    /// </summary>
    public string UpscalerFilesSummary
    {
        get
        {
            if (UpdatableFileCount > 0) return UpdatableFileCount == 1 ? "1 update available" : $"{UpdatableFileCount} updates available";
            if (HasRestoredByGame) return RestoredTitle;
            var updated = _fileItems.Where(i => i.State == UpscalerFileState.UpdatedByUpshift)
                .OrderBy(i => i.Family).ThenBy(i => i.FileName.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            if (updated?.CurrentVersion is { } v)
                return $"{Ui.FamilyLabel(updated.Family)} {Ui.VersionLabel(v, updated.FileName)} · updated by Upshift";
            if (_fileItems.Any(i => i.State == UpscalerFileState.ChangedSince)) return "Changed outside Upshift";
            return HasUpscalerFileRows ? "Already up to date" : "OptiScaler's files only";
        }
    }

    /// <summary>The bar above "Upscalers in this game" when a newer DLSS file is on offer.</summary>
    public bool HasDlssBar { get; }
    public string DlssBarText { get; } = "";

    /// <summary>The game put its own old file back over Upshift's update.</summary>
    public bool HasRestoredByGame { get; }
    public string RestoredTitle { get; } = "";
    public string RestoredText { get; } = "";
    public IReadOnlyList<string> RestoredPaths => _restoredPaths;
    public bool CanReapply => _restoredPaths.Count > 0 && !Info.HasAntiCheat;

    /// <summary>For the card badge and the "Upscaler update available" filter.</summary>
    public bool HasUpscalerUpdate => CanUpdateAnyFile;

    /// <summary>The files that can be updated, for "Update all".</summary>
    public IReadOnlyList<string> UpdatablePaths => _fileItems.Where(i => i.CanUpdate).Select(i => i.RelativePath).ToList();

    /// <summary>Launch options for Play (saved per game).</summary>
    [ObservableProperty]
    private string launchOptions = "";

    /// <summary>False for stores whose launch link can't carry launch options (Epic, Xbox…); LaunchOptionsNote says where they go instead.</summary>
    public bool TakesLaunchOptions { get; }
    public string LaunchOptionsNote { get; }

    /// <summary>How Play starts this game, e.g. "Starts … through Steam".</summary>
    public string PlayTip { get; }

    /// <summary>Why the last Play didn't work; shown in the details pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLaunchError))]
    private string? launchError;

    public bool HasLaunchError => LaunchError is not null;

    public GameInfo Info { get; }

    /// <summary>The OptiScaler version this app installed here, if any.</summary>
    public string? InstalledVersion { get; }

    /// <summary>The newer release on the user's channel this install can be updated to; null when up to date.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateButtonText), nameof(StatusBadgeText), nameof(HasStatusBadge), nameof(StatusBadgeTip))]
    private string? updateVersion;

    public bool HasUpdate => UpdateVersion is not null;
    public string UpdateButtonText => UpdateVersion is null ? "" : $"Update to {UpdateVersion}";

    public bool CanUndo { get; }
    public string UndoText { get; }
    public bool NeedsRepair { get; }
    public string RepairText { get; }

    /// <summary>
    /// The card's second badge: "Needs repair", "Update" (OptiScaler), or what's out of date among the game's own
    /// upscaler files: "DLSS update", "XeSS update", "DLSS + XeSS updates".
    /// </summary>
    public string StatusBadgeText => NeedsRepair ? "Needs repair" : HasUpdate ? "Update" : UpscalerBadgeText;

    /// <summary>The badge's tooltip; for upscaler updates, each file with its current and new version.</summary>
    public string StatusBadgeTip => NeedsRepair ? "Some OptiScaler files are missing or changed"
        : HasUpdate ? $"OptiScaler {UpdateVersion} is available" + (HasUpscalerUpdate ? $"\n\n{UpscalerBadgeText}:\n{UpscalerUpdateList}" : "")
        : HasUpscalerUpdate ? UpscalerUpdateList : "";

    /// <summary>"DLSS update", "XeSS update", "DLSS + XeSS updates" (empty when nothing is out of date).</summary>
    public string UpscalerBadgeText => UpscalerFileText.BadgeText(_fileItems);

    /// <summary>The technologies with a file to update, for the Library filter's label.</summary>
    public IReadOnlyList<string> UpscalerUpdateFamilies => UpscalerFileText.FamiliesWithUpdates(_fileItems);

    /// <summary>"libxess.dll: 2.0.0.18 → 2.0.2.68 (downloads when you update)", one line per file.</summary>
    public string UpscalerUpdateList => UpscalerFileText.UpdateList(_fileItems);
    public bool HasStatusBadge => StatusBadgeText.Length > 0;

    /// <summary>Works out UpdateVersion again after an update check or a change of channel. Call on the UI thread.</summary>
    public void RefreshUpdate() =>
        UpdateVersion = IsInstalledByUs && !Info.HasAntiCheat ? Services.GameUpdates.TargetFor(InstalledVersion, InstalledComponentId) : null;

    /// <summary>The build installed here by Upshift ("optiscaler", "optiscaler-dlssnr", "amd-nr").</summary>
    public string? InstalledComponentId { get; }

    public bool IsInstalledByUs { get; }
    public bool IsNotInstalledByUs => !IsInstalledByUs;
    public string InstalledText { get; }
    public string IniPath { get; }
    /// <summary>False for games with anti-cheat (installs stay blocked) and when there's no main exe folder.</summary>
    public bool CanInstall { get; }
    public string Name { get; }
    public string SourceText { get; }
    public string InstallDir { get; }
    public string TargetText { get; }
    public string ExeText { get; }
    public bool ExeChosenByUser { get; }
    public string ExeNote { get; }
    public string ApiText { get; }

    [ObservableProperty]
    private string engineText = "";

    /// <summary>"Upscalers in this game": files and PCGamingWiki merged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpscalers))]
    private UpscalerSectionViewModel upscalers = null!;

    /// <summary>The card's chips: DLSS, FSR, XeSS, TSR from the merged list.</summary>
    [ObservableProperty]
    private List<ChipViewModel> tags = new();

    public bool HasUpscalers => Upscalers.HasRows;

    /// <summary>Rebuilds the merged list with what PCGamingWiki said (or why it couldn't say). Call on the UI thread.</summary>
    public void ApplyWiki(Core.Wiki.WikiEntry? entry, bool checking = false)
    {
        Upscalers = new UpscalerSectionViewModel(Info, entry, checking, _fileItems);
        Tags = Upscalers.Chips;
        RefreshSuggestions();
    }

    /// <summary>"Suggestions for this game" (OptiScaler wiki) or "General advice".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWikiSuggestions))]
    private GameSuggestions suggestions = null!;

    /// <summary>The "Suggested for your …" lines; null until the graphics card is known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecommendation))]
    private RecommendationViewModel? recommendation;

    public bool HasRecommendation => Recommendation is not null;

    /// <summary>For the "Has suggestions" filter: a matched OptiScaler wiki entry with at least one suggestion.</summary>
    public bool HasWikiSuggestions => Suggestions is { FromWiki: true, Items.Count: > 0 };

    /// <summary>Rebuilds the suggestions and the recommendation from cached data (wiki, graphics card). Call on the UI thread.</summary>
    public void RefreshSuggestions()
    {
        var gpu = AppServices.Gpu;
        Suggestions = AppServices.Suggestions.For(Info, gpu);
        var rec = Recommender.For(gpu, Upscalers.CoreRows, AppServices.Catalog, Suggestions.InGameInput);
        Recommendation = rec is null ? null : new RecommendationViewModel(rec);
    }

    /// <summary>Picks up an engine found online after the card was made. Call on the UI thread.</summary>
    public void RefreshEngine() =>
        EngineText = Info.EngineSource is null ? Info.Engine : $"{Info.Engine} (from {Info.EngineSource})";

    public string BuildText { get; }
    public bool HasAntiCheat { get; }
    public string AntiCheatText { get; }
    public string AntiCheatMessage { get; }
    public SolidColorBrush TileBrush { get; }
    public double TileOpacity { get; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArtwork))]
    private BitmapImage? artwork;

    [ObservableProperty]
    private bool hasCustomArtwork;

    public bool HasArtwork => Artwork is not null;

    /// <summary>Picks up a newly downloaded, chosen or reset picture. Call on the UI thread.</summary>
    public void RefreshArtwork()
    {
        var path = Info.DisplayArtworkPath;
        HasCustomArtwork = Info.CustomArtworkPath is not null;
        if (path == _artworkPath) return;
        _artworkPath = path;
        Artwork = path is null ? null : new BitmapImage(new Uri(path)) { DecodePixelWidth = 300 };
    }

    private string? _artworkPath;
    public bool HasBadge { get; }
    public string BadgeText { get; } = "";
    public SolidColorBrush BadgeForeground { get; } = Ui.Brush("#5B5F67");
    public List<string> Mods { get; }
    public bool HasMods { get; }

    private static string SourceName(GameSourceKind source) => source switch
    {
        GameSourceKind.Steam => "Steam",
        GameSourceKind.Epic => "Epic Games",
        GameSourceKind.Gog => "GOG",
        GameSourceKind.Xbox => "Xbox app",
        GameSourceKind.EA => "EA app",
        GameSourceKind.Ubisoft => "Ubisoft Connect",
        GameSourceKind.BattleNet => "Battle.net",
        GameSourceKind.Heroic => "Heroic",
        GameSourceKind.Itch => "itch.io",
        GameSourceKind.Manual => "Added by you",
        GameSourceKind.InstalledProgram => "Installed program",
        GameSourceKind.DriveScan => "Found by drive scan",
        _ => "Unknown"
    };

    private static string ApiName(GraphicsApi api)
    {
        var names = new List<string>();
        if (api.HasFlag(GraphicsApi.D3D12)) names.Add("DirectX 12");
        if (api.HasFlag(GraphicsApi.Vulkan)) names.Add("Vulkan");
        if (api.HasFlag(GraphicsApi.D3D11)) names.Add("DirectX 11");
        if (api.HasFlag(GraphicsApi.D3D10)) names.Add("DirectX 10");
        if (api.HasFlag(GraphicsApi.D3D9)) names.Add("DirectX 9");
        if (api.HasFlag(GraphicsApi.OpenGL)) names.Add("OpenGL");
        return names.Count == 0 ? "Unknown" : string.Join(" / ", names.Take(2));
    }

    /// <summary>string.GetHashCode changes every run; this doesn't, so tile colours stay put.</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 23;
            foreach (var c in s) h = h * 31 + c;
            return h;
        }
    }
}
