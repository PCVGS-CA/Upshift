using System.Text.Json.Serialization;
using Upshift.Core.Models;

namespace Upshift.Core.Catalog;

/// <summary>The list of every download source and DLSS 5 backend the app knows about.</summary>
public sealed class UpscalerCatalog
{
    public int Schema { get; set; }
    public string? Updated { get; set; }
    public List<CatalogComponent> Components { get; set; } = new();
    public List<NeuralBackend> NeuralBackends { get; set; } = new();
    public List<KnownFile> KnownNvidiaNrFiles { get; set; } = new();
    public ArtworkSources ArtworkSources { get; set; } = new();
    public PcGamingWikiSource PcGamingWiki { get; set; } = new();
    public RecommendationRules Recommendations { get; set; } = new();
    public List<DlssPreset> DlssPresets { get; set; } = new();
    public Fsr4Support Fsr4 { get; set; } = new();
    public OptiScalerWikiSource OptiScalerWiki { get; set; } = new();
    public Guides Guides { get; set; } = new();

    /// <summary>DLSS DLL version → marketing name ("310.9.1" is DLSS 4.5), checked in order.</summary>
    public List<DlssVersionName> DlssVersionNames { get; set; } = new();

    /// <summary>Newer copies of the DLSS, FSR and XeSS files games ship, and which game files each may replace.</summary>
    public UpscalerFileCatalog UpscalerFiles { get; set; } = new();
}

public sealed class UpscalerFileCatalog
{
    /// <summary>Per family, the signer names (certificate O or CN) a new file must be signed by.</summary>
    public Dictionary<UpscalerFamily, List<string>> Signers { get; set; } = new();
    public List<UpscalerFileSource> Sources { get; set; } = new();
}

/// <summary>
/// One DLL a game's file of the same name can be updated to. It replaces a game file whose version is at least
/// MinVersion and below BelowVersion (when set), and older than Version.
/// </summary>
public sealed class UpscalerFileSource
{
    public string File { get; set; } = "";
    public UpscalerFamily Family { get; set; }
    /// <summary>The file's own version (not the SDK's), e.g. "310.9.1" or "2.0.2.68".</summary>
    public string Version { get; set; } = "";
    public string? MinVersion { get; set; }
    public string? BelowVersion { get; set; }
    /// <summary>The catalog component (repo) it comes from.</summary>
    public string ComponentId { get; set; } = "";
    /// <summary>The release tag (or git tag) it's read at.</summary>
    public string Tag { get; set; } = "";
    /// <summary>Read straight from the repository at Tag (checked against GitHub's git blob hash).</summary>
    public string? RepoPath { get; set; }
    /// <summary>Or: the path inside the release's zip (the component's assetPattern picks the zip).</summary>
    public string? ArchivePath { get; set; }

    [JsonIgnore] public string Key => $"{ComponentId}/{Tag}/{File}".ToLowerInvariant();
}

/// <summary>"Which one should I pick?" text for the option dropdowns, kept in the catalog so it can change without a new app.</summary>
public sealed class Guides
{
    public Guide DlssModel { get; set; } = new();
    public Guide Upscaler { get; set; } = new();
    public Guide FrameGen { get; set; } = new();
}

public sealed class Guide
{
    public string Title { get; set; } = "Which one should I pick?";
    /// <summary>One per dropdown option (matched by the option's id); only options the dropdown offers are shown.</summary>
    public List<GuideRow> Rows { get; set; } = new();
    /// <summary>Lines about the user's graphics card; the first that matches is shown.</summary>
    public List<GuideNote> CardNotes { get; set; } = new();
    /// <summary>A closing line shown for every card.</summary>
    public string? Footer { get; set; }
}

public sealed class GuideRow
{
    public string Id { get; set; } = "";
    /// <summary>Shown in the table; the dropdown's own label when empty.</summary>
    public string? Name { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Replaces Text for a vendor ("Nvidia", "Amd", "Intel") or a generation ("RDNA 4").</summary>
    public Dictionary<string, string> ForCard { get; set; } = new();
    public string? BestFor { get; set; }

    public string TextFor(GpuVendor? vendor, string? generation) =>
        generation is not null && ForCard.TryGetValue(generation, out var g) ? g
        : vendor is not null && ForCard.TryGetValue(vendor.Value.ToString(), out var v) ? v
        : Text;
}

public sealed class GuideNote
{
    /// <summary>Matches when the card's vendor is listed (empty: any vendor).</summary>
    public List<GpuVendor> Vendors { get; set; } = new();
    /// <summary>Matches when the card's generation is listed (empty: any generation).</summary>
    public List<string> Generations { get; set; } = new();
    public string Text { get; set; } = "";

    public bool Matches(GpuVendor? vendor, string? generation) =>
        (Vendors.Count == 0 || (vendor is not null && Vendors.Contains(vendor.Value)))
        && (Generations.Count == 0 || (generation is not null && Generations.Contains(generation)));
}

public sealed class CatalogComponent
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public string Repo { get; set; } = "";
    public string? AssetPattern { get; set; }
    public string? RepoFilePath { get; set; }
    public string? PinnedVersion { get; set; }
    public string? VerifyBeforeRelease { get; set; }

    /// <summary>True when the app can install this component into games (only OptiScaler so far).</summary>
    public bool Installable { get; set; }

    /// <summary>Maintainer notes about the component's releases (asset names, quirks). Not shown in the app.</summary>
    public string? Notes { get; set; }

    /// <summary>Shown on another component's card (the AMD-NR runtime on AMD-NR's), sharing its channel and settings.</summary>
    public string? GroupWith { get; set; }

    /// <summary>Id of the component that ships this one inside its release (fakenvapi and dlssg-to-fsr3 come with OptiScaler).</summary>
    public string? BundledIn { get; set; }

    /// <summary>Finds the bundled version in the host's release notes; group 1 is the version (e.g. "Fakenvapi 1.4.1").</summary>
    public string? BundledVersionPattern { get; set; }

    [JsonIgnore] public Uri RepoUrl => new($"https://github.com/{Repo}");
    [JsonIgnore] public string PinnedText => PinnedVersion is null ? "No version pinned" : $"Tested version: {PinnedVersion}";
}

public sealed class NeuralBackend
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ComponentId { get; set; } = "";
    public GpuVendor Vendor { get; set; }
    public List<string> Generations { get; set; } = new();
    public bool NeedsNvidiaDll { get; set; }
    public bool RequiresSignedDll { get; set; } = true;
    public List<NeuralRuntime> Runtimes { get; set; } = new();
    public string Summary { get; set; } = "";
}

public sealed class NeuralRuntime
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Generations { get; set; } = new();
    public string? ComponentId { get; set; }
}

public sealed class KnownFile
{
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string? Source { get; set; }
}

/// <summary>
/// Where cover art is downloaded from. URL templates use {id}, {name}, {appid}, {formatter} and {ext} placeholders.
/// Sources are tried in this order: Steam's local cache, GOG, Epic, Steam store search, then SteamGridDB (only with a key).
/// </summary>
public sealed class ArtworkSources
{
    public string UserAgent { get; set; } = "Upshift/1.0";
    public int TimeoutSeconds { get; set; } = 20;
    public int MaxParallel { get; set; } = 3;
    /// <summary>A game with no art found anywhere is looked up again after this many days.</summary>
    public int RetryMissingAfterDays { get; set; } = 7;
    public GogArtworkSource Gog { get; set; } = new();
    public EpicArtworkSource Epic { get; set; } = new();
    public SteamSearchArtworkSource SteamSearch { get; set; } = new();
    public SteamGridDbArtworkSource SteamGridDb { get; set; } = new();
}

public sealed class GogArtworkSource
{
    public string? GamesDbUrl { get; set; }
    /// <summary>Fills {formatter} in game.vertical_cover.url_format.</summary>
    public string CoverFormatter { get; set; } = "";
    public string CoverExtension { get; set; } = "jpg";
    public string? ApiUrl { get; set; }
    public string ApiImageLink { get; set; } = "boxArtImage";
}

public sealed class EpicArtworkSource
{
    /// <summary>Environment variables such as %ProgramData% are expanded.</summary>
    public string? CatalogCachePath { get; set; }
    public string? ManifestsPath { get; set; }
    public List<string> ImageTypes { get; set; } = new();
    /// <summary>Appended to the image URL, e.g. to ask Epic's CDN for a smaller copy.</summary>
    public string? ImageQuery { get; set; }
}

public sealed class SteamSearchArtworkSource
{
    public string? SearchUrl { get; set; }
    /// <summary>Tried in order for the matched app.</summary>
    public List<string> ImageUrls { get; set; } = new();
    /// <summary>0 to 1. How close the store's name must be to the game's name after tidying both.</summary>
    public double MinNameSimilarity { get; set; } = 0.9;
}

public sealed class SteamGridDbArtworkSource
{
    public string? SearchUrl { get; set; }
    public string? GridsUrl { get; set; }
}

/// <summary>
/// PCGamingWiki, read through its MediaWiki API for a game's engine and supported upscalers.
/// URL templates use {title} (a page title) and {name} (a tidied game name).
/// </summary>
public sealed class PcGamingWikiSource
{
    /// <summary>Names this app to the wiki's operators, as MediaWiki asks API clients to do.</summary>
    public string UserAgent { get; set; } = "Upshift/1.0";
    public int TimeoutSeconds { get; set; } = 20;
    /// <summary>How long an answer (a page, or "no page") is reused before asking again.</summary>
    public int CacheDays { get; set; } = 30;
    /// <summary>action=parse returning the page's wikitext as JSON.</summary>
    public string? WikitextUrl { get; set; }
    /// <summary>Full-text search, used once when no page has the game's exact name.</summary>
    public string? SearchUrl { get; set; }
    /// <summary>The page link shown next to "Source: PCGamingWiki".</summary>
    public string? PageUrl { get; set; }
    /// <summary>0 to 1. How close a search result's title must be to the game's name.</summary>
    public double MinNameSimilarity { get; set; } = 0.9;
}

/// <summary>Which upscaler and frame generation to suggest for a graphics card. Suggestions only.</summary>
public sealed class RecommendationRules
{
    public List<RecommendationRule> Rules { get; set; } = new();
    public RecommendationRule Fallback { get; set; } = new();
}

public sealed class RecommendationRule
{
    public GpuVendor Vendor { get; set; }
    public List<string> Generations { get; set; } = new();
    /// <summary>Only matches when FSR 4 is officially supported on the card (desktop RDNA 3); otherwise the next rule applies.</summary>
    public bool RequiresOfficialFsr4 { get; set; }
    /// <summary>Suggested when the game has it.</summary>
    public string Upscaler { get; set; } = "";
    /// <summary>Suggested when the game only has other upscalers that OptiScaler can swap out.</summary>
    public string? UpscalerViaOptiScaler { get; set; }
    public string? Alternative { get; set; }
    public string Reason { get; set; } = "";
    public string? FrameGen { get; set; }
    public string? FrameGenReason { get; set; }
}

/// <summary>A DLSS render preset OptiScaler can force, and the oldest nvngx_dlss.dll that has it.</summary>
public sealed class DlssPreset
{
    public string Id { get; set; } = "";
    /// <summary>The number OptiScaler.ini uses (10 = J, 11 = K, 12 = L, 13 = M).</summary>
    public int Value { get; set; }
    public string Name { get; set; } = "";
    public string MinVersion { get; set; } = "";
    public string Note { get; set; } = "";
}

public sealed class Fsr4Support
{
    /// <summary>FSR 4 is official here, so the INT8 options are hidden (desktop cards only; integrated GPUs have their own labels).</summary>
    public List<string> OfficialGenerations { get; set; } = new();
    /// <summary>Cards that support INT8 and may try FSR 4 through OptiScaler.</summary>
    public List<string> Int8Generations { get; set; } = new();
    public string CommunityInt8FileName { get; set; } = "amdxcffx64.dll";
}

/// <summary>The OptiScaler wiki, read as raw markdown/AsciiDoc from its git repo. {page} is a page name like "The-Witcher-3-Wild-Hunt".</summary>
public sealed class OptiScalerWikiSource
{
    public string? CompatibilityListUrl { get; set; }
    public string? PageUrl { get; set; }
    public string? WebPageUrl { get; set; }
    public string? WebListUrl { get; set; }
    public int RefreshDays { get; set; } = 7;
    public string UserAgent { get; set; } = "Upshift/1.0";
}
