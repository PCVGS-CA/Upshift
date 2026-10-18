using System.Text.Json;
using Upshift.Core.Catalog;

namespace Upshift.Core.Services;

/// <summary>User preferences, saved as settings.json in the app data folder.</summary>
public sealed class AppSettings
{
    /// <summary>Optional. When set, SteamGridDB is used as the last cover art source.</summary>
    public string? SteamGridDbKey { get; set; }

    /// <summary>Check GitHub for new releases at start-up (at most every 6 hours).</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Optional address of another catalog.json. Empty means Upshift's online catalog (DefaultCatalogUrl).</summary>
    public string CatalogUrl { get; set; } = "";

    /// <summary>
    /// Upshift's own catalog on GitHub, used when CatalogUrl is empty, so fixes and new entries reach everyone with a
    /// commit. When it can't be reached, or isn't newer, the built-in copy is used. Stable versions read catalog.json on
    /// main; betas (x.y.z-beta.N) read it on the "beta" branch, so a beta's catalog changes never reach older versions
    /// (1.2.0's DLSS 5 switch, for one, can't use 1.3's DLSS 5 entries).
    /// </summary>
    public static string DefaultCatalogUrl { get; } = IsBetaBuild ? BetaCatalogUrl : MainCatalogUrl;

    public const string MainCatalogUrl = "https://raw.githubusercontent.com/PCVGS-CA/Upshift/main/src/Upshift.Core/Catalog/catalog.json";
    public const string BetaCatalogUrl = "https://raw.githubusercontent.com/PCVGS-CA/Upshift/beta/src/Upshift.Core/Catalog/catalog.json";

    /// <summary>This build's version (from Directory.Build.props) is a beta.</summary>
    public static bool IsBetaBuild =>
        (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(AppSettings).Assembly)
            ?.InformationalVersion ?? "").Contains("-beta", StringComparison.OrdinalIgnoreCase);

    /// <summary>The address actually used: the user's own, or Upshift's online catalog.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveCatalogUrl => string.IsNullOrWhiteSpace(CatalogUrl) ? DefaultCatalogUrl : CatalogUrl.Trim();

    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Games the user hid from the Library ("Hide from library"), by game id, with the name for Settings.</summary>
    public Dictionary<string, string> HiddenGames { get; set; } = new();

    /// <summary>Per component (by catalog id): the chosen channel and whether to download new releases automatically.</summary>
    public Dictionary<string, ComponentPreference> Components { get; set; } = new();

    /// <summary>The monitor the frame cap's numbers come from (Windows' device name, e.g. DISPLAY2); null means the main monitor.</summary>
    public string? FrameCapDisplay { get; set; }

    /// <summary>The key that starts a measurement while a game runs (a Windows virtual-key code; F10 by default).</summary>
    public int MeasureHotkey { get; set; } = 0x79;

    /// <summary>How long a measurement runs, in seconds (the last value typed; 10 to 1800).</summary>
    public int MeasureSeconds { get; set; } = Measure.MeasureThresholds.MeasureSeconds;

    /// <summary>"Shader pre-build (Experimental)" in Settings. Off: nothing of it shows, and nothing is added to any game.</summary>
    public bool ShaderPrebuild { get; set; }

    /// <summary>Collapsible sections the user left open (true) or closed (false), by a fixed key such as "game.changes".</summary>
    public Dictionary<string, bool> OpenSections { get; set; } = new();

    public ComponentPreference For(string componentId) =>
        Components.TryGetValue(componentId, out var p) ? p : Components[componentId] = new ComponentPreference();
}

public enum UpdateChannel { Stable, Beta }

public sealed class ComponentPreference
{
    /// <summary>Stable = the catalog's tested version; Beta = the newest release, pre-releases included.</summary>
    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;

    /// <summary>Download the channel's newest release automatically after each check.</summary>
    public bool KeepUpdated { get; set; }
}

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _sync = new();
    private AppSettings _current;

    public SettingsStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "settings.json");
        _current = Load();
    }

    public AppSettings Current
    {
        get { lock (_sync) return _current; }
    }

    public void Save(AppSettings settings)
    {
        lock (_sync)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }));
            File.Move(temp, _path, overwrite: true);
            _current = settings;
        }
    }

    private AppSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), CatalogLoader.JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }
}
