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
        InstalledText = IsInstalledByUs ? $"OptiScaler {manifest!.Version} installed as {manifest.ProxyName}" : "";
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

        // "Changes Upshift made to this game", newest first.
        Changes = manifest is null || info.TargetDir is null
            ? new List<ChangeViewModel>()
            : OptiScalerInstaller.Changes(info.TargetDir, manifest, Ui.VersionLabel).Select(c => new ChangeViewModel(c)).ToList();

        launchOptions = AppServices.LaunchOptions.Get(info.Id);
        (TakesLaunchOptions, LaunchOptionsNote) = Core.Launch.GameLauncher.LaunchOptionsSupport(info);
        PlayTip = Core.Launch.GameLauncher.Plan(info, null, out var launchProblem) is { } plan
            ? $"Starts {info.Name} through {plan.Via}"
            : launchProblem ?? "";
    }

    public List<ChangeViewModel> Changes { get; }
    public bool HasChanges => Changes.Count > 0;

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
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateButtonText), nameof(StatusBadgeText), nameof(HasStatusBadge))]
    private string? updateVersion;

    public bool HasUpdate => UpdateVersion is not null;
    public string UpdateButtonText => UpdateVersion is null ? "" : $"Update to {UpdateVersion}";

    public bool CanUndo { get; }
    public string UndoText { get; }
    public bool NeedsRepair { get; }
    public string RepairText { get; }

    /// <summary>The card's second badge: "Needs repair" or "Update".</summary>
    public string StatusBadgeText => NeedsRepair ? "Needs repair" : HasUpdate ? "Update" : "";
    public bool HasStatusBadge => StatusBadgeText.Length > 0;

    /// <summary>Works out UpdateVersion again after an update check or a change of channel. Call on the UI thread.</summary>
    public void RefreshUpdate() =>
        UpdateVersion = IsInstalledByUs && !Info.HasAntiCheat ? Services.GameUpdates.TargetFor(InstalledVersion) : null;

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
        Upscalers = new UpscalerSectionViewModel(Info, entry, checking);
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
