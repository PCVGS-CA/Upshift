using System.Text.Json.Serialization;

namespace Upshift.Core.Models;

/// <summary>A game as a launcher (or the drive scan) reported it, before we look inside it.</summary>
public sealed record DiscoveredGame(
    string Name,
    string InstallDir,
    GameSourceKind Source,
    string? SourceId = null,
    string? ExePath = null,
    string? ArtworkPath = null);

/// <summary>One upscaler DLL found inside a game folder.</summary>
public sealed class UpscalerDll
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public UpscalerFamily Family { get; set; }
    public string Feature { get; set; } = "";
    public string? Version { get; set; }
}

/// <summary>A mod that was already in the game folder before we touched it.</summary>
public sealed class ExistingMod
{
    public ModKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? FileName { get; set; }
    public string? Description { get; set; }
}

/// <summary>Everything the app knows about one game after analysis. Saved to the library cache.</summary>
public sealed class GameInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public GameSourceKind Source { get; set; }
    public string? SourceId { get; set; }
    public string InstallDir { get; set; } = "";

    /// <summary>The main game executable we picked (or the user chose).</summary>
    public string? ExePath { get; set; }

    /// <summary>The exe the store itself starts (GOG's, Epic's LaunchExecutable…), when it reported one. Play runs this for exe-launched games.</summary>
    public string? LaunchExePath { get; set; }

    /// <summary>True when ExePath is the user's own choice rather than ours.</summary>
    public bool ExeChosenByUser { get; set; }

    /// <summary>The folder OptiScaler would be installed into (the folder holding ExePath).</summary>
    public string? TargetDir { get; set; }

    public string Engine { get; set; } = "Unknown";

    /// <summary>Set when Engine came from an online lookup ("PCGamingWiki") instead of the game's files.</summary>
    public string? EngineSource { get; set; }
    public GraphicsApi Api { get; set; }
    public bool? Is64Bit { get; set; }
    public List<UpscalerDll> Upscalers { get; set; } = new();
    public List<string> AntiCheat { get; set; } = new();
    public List<ExistingMod> ExistingMods { get; set; } = new();

    /// <summary>Files of an OptiScaler this app installed that are missing or changed (found by the last scan).</summary>
    public List<string> RepairProblems { get; set; } = new();

    /// <summary>Upscaler files Upshift updated that the game has put its old copy back into (found by the last scan).</summary>
    public List<string> UpscalerFilesRestoredByGame { get; set; } = new();

    /// <summary>Cover art found automatically: Steam's local cache or a file downloaded into the artwork folder.</summary>
    public string? ArtworkPath { get; set; }

    /// <summary>Where ArtworkPath came from ("Steam", "GOG", "Epic", "Steam store", "SteamGridDB").</summary>
    public string? ArtworkSource { get; set; }

    /// <summary>A picture the user chose. Wins over ArtworkPath and survives rescans.</summary>
    public string? CustomArtworkPath { get; set; }

    /// <summary>When the last online lookup found nothing, so it isn't repeated on every scan.</summary>
    public DateTime? ArtworkMissUtc { get; set; }

    public DateTime ScannedUtc { get; set; }

    [JsonIgnore] public bool HasAntiCheat => AntiCheat.Count > 0;
    [JsonIgnore] public string? DisplayArtworkPath => FileExists(CustomArtworkPath) ? CustomArtworkPath : FileExists(ArtworkPath) ? ArtworkPath : null;
    [JsonIgnore] public bool HasOptiScaler => ExistingMods.Any(m => m.Kind == ModKind.OptiScaler);

    private static bool FileExists(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try { return File.Exists(path); } catch { return false; }
    }
}
