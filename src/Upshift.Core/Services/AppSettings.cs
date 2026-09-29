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
    /// Upshift's own catalog on GitHub (catalog.json on main), used when CatalogUrl is empty, so fixes and new entries
    /// reach everyone with a commit. When it can't be reached, or isn't newer, the built-in copy is used.
    /// </summary>
    public const string DefaultCatalogUrl = "https://raw.githubusercontent.com/PCVGS-CA/Upshift/main/src/Upshift.Core/Catalog/catalog.json";

    /// <summary>The address actually used: the user's own, or Upshift's online catalog.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveCatalogUrl => string.IsNullOrWhiteSpace(CatalogUrl) ? DefaultCatalogUrl : CatalogUrl.Trim();

    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Games the user hid from the Library ("Hide from library"), by game id, with the name for Settings.</summary>
    public Dictionary<string, string> HiddenGames { get; set; } = new();

    /// <summary>Per component (by catalog id): the chosen channel and whether to download new releases automatically.</summary>
    public Dictionary<string, ComponentPreference> Components { get; set; } = new();

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
