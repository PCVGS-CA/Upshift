using System.Reflection;
using Upshift.Core.Artwork;
using Upshift.Core.Catalog;
using Upshift.Core.Components;
using Upshift.Core.Wiki;
using Upshift.Core.Hardware;
using Upshift.Core.Install;
using Upshift.Core.Services;
using Upshift.Core.UserFiles;

namespace Upshift.App;

/// <summary>The few shared objects the whole app uses.</summary>
public static class AppServices
{
    /// <summary>
    /// %LocalAppData%\Upshift. Chosen by App.OnLaunched (after moving older data across) before anything here is used,
    /// because every service below is created from it the first time AppServices is touched.
    /// </summary>
    public static string DataDir => DataFolder.Path;

    public static GameLibraryService Library { get; } = new(DataDir);

    // Before Catalog: which catalog to use depends on the "catalogUrl" setting.
    public static SettingsStore Settings { get; } = new(DataDir);

    /// <summary>The catalog compiled into the app.</summary>
    public static UpscalerCatalog BuiltInCatalog { get; } = CatalogLoader.LoadBuiltIn();

    public static UpscalerCatalog Catalog { get; } = LoadCatalog();

    /// <summary>True when Catalog is the one downloaded from the "catalogUrl" setting rather than the built-in one.</summary>
    public static bool UsingRemoteCatalog { get; private set; }

    /// <summary>
    /// The catalog downloaded from the "catalogUrl" setting when it's valid and newer, otherwise the built-in one,
    /// with every web request identifying itself as "Upshift/&lt;version&gt;".
    /// </summary>
    private static UpscalerCatalog LoadCatalog()
    {
        var remote = RemoteCatalog.LoadIfNewer(DataDir, Settings.Current.CatalogUrl, BuiltInCatalog);
        UsingRemoteCatalog = remote is not null;
        var catalog = remote ?? CatalogLoader.LoadBuiltIn();
        catalog.ArtworkSources.UserAgent = AppInfo.UserAgent;
        catalog.PcGamingWiki.UserAgent = $"{AppInfo.UserAgent} (Upshift for Windows; reads engine and upscaler info)";
        catalog.OptiScalerWiki.UserAgent = AppInfo.UserAgent;
        return catalog;
    }

    public static ArtworkFinder Artwork { get; } = new(Catalog.ArtworkSources, Library.ArtworkDir, () => Settings.Current.SteamGridDbKey);

    public static PcGamingWikiClient WikiClient { get; } = new(Catalog.PcGamingWiki);

    public static WikiCache WikiCache { get; } = new(DataDir);

    /// <summary>Downloaded releases (OptiScaler etc.), kept under components\.</summary>
    public static ComponentStore Components { get; } = new(DataDir, AppInfo.UserAgent);

    /// <summary>Release checks for every catalog component (GitHub, cached with ETags).</summary>
    public static UpdateChecker Updates { get; } = new(Components, Settings, Catalog.Components);

    /// <summary>The last line about the "catalogUrl" catalog, for the Updates page (null when the setting is empty).</summary>
    public static string? CatalogStatus { get; set; }

    /// <summary>The official OptiScaler entry from the catalog (repo and pinned version).</summary>
    public static CatalogComponent OptiScaler => Catalog.Components.First(c => c.Id == "optiscaler");

    /// <summary>Files the user supplies (DLSS 5, FSR 4.0.2c INT8). Never downloaded or bundled.</summary>
    public static UserFileStore UserFiles { get; } = new(DataDir);

    public static InstallPreferences InstallPrefs { get; } = new(DataDir);

    /// <summary>Each game's launch options for Play.</summary>
    public static Core.Launch.LaunchOptionsStore LaunchOptions { get; } = new(DataDir);

    public static OptiScalerWikiClient OptiWiki { get; } = new(Catalog.OptiScalerWiki, DataDir);

    /// <summary>"Suggestions for this game" from the OptiScaler wiki (or general advice).</summary>
    public static SuggestionService Suggestions { get; } = new(OptiWiki, DataDir, () => OptiIniKeys);

    private static IReadOnlyDictionary<string, List<string>>? _optiIniKeys;

    /// <summary>Every setting name in the downloaded OptiScaler.ini, and its section (for turning wiki fixes into ini changes).</summary>
    public static IReadOnlyDictionary<string, List<string>> OptiIniKeys
    {
        get
        {
            if (_optiIniKeys is not null) return _optiIniKeys;
            var ini = Components.TryGetCached(OptiScaler) is { } c ? Path.Combine(c.Folder, "OptiScaler.ini") : null;
            if (ini is null || !File.Exists(ini)) return new Dictionary<string, List<string>>();
            return _optiIniKeys = IniFile.Load(ini).KeyIndex();
        }
    }

    /// <summary>The primary graphics card once detection has finished (null until then).</summary>
    public static GpuInfo? Gpu => _gpus is { IsCompletedSuccessfully: true } t ? GpuDetector.PickPrimary(t.Result) : null;

    /// <summary>Raised when a setting that affects cover art changes, so the Library can look again.</summary>
    public static event Action? ArtworkSettingsChanged;

    public static void RaiseArtworkSettingsChanged() => ArtworkSettingsChanged?.Invoke();

    private static Task<IReadOnlyList<GpuInfo>>? _gpus;

    /// <summary>Runs GPU detection once, in the background, and reuses the answer.</summary>
    public static Task<IReadOnlyList<GpuInfo>> GetAllGpusAsync() => _gpus ??= Task.Run(() => GpuDetector.DetectAll());

    public static async Task<GpuInfo?> GetGpuAsync() => GpuDetector.PickPrimary(await GetAllGpusAsync());
}

/// <summary>Where the app keeps its data. Set once at start-up, before AppServices is first used.</summary>
public static class DataFolder
{
    public static string Path { get; set; } = Core.Services.DataMigration.NewDataDir;
}

/// <summary>The app's name and version, e.g. for the About section and the HTTP User-Agent ("Upshift/1.0.0").</summary>
public static class AppInfo
{
    /// <summary>From Directory.Build.props, e.g. "1.0.0" or "1.1.0-beta.1".</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "1.0.0";

    public static string UserAgent => $"Upshift/{Version}";

    /// <summary>Where Upshift's releases are published; the installer and the app's own updates come from here.</summary>
    public const string RepoUrl = "https://github.com/PCVGS-CA/Upshift";

    /// <summary>The Assets folder next to Upshift.exe (logo and icons).</summary>
    public static string AssetsDir => Path.Combine(Core.Services.AppLocations.ExeDir, "Assets");
}
