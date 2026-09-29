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

    /// <summary>Optional address of a newer catalog.json. Empty means the built-in catalog is used.</summary>
    public string CatalogUrl { get; set; } = "";

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
