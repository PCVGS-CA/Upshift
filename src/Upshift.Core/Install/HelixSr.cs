using System.Text.Json;
using System.Text.RegularExpressions;
using Upshift.Core.Catalog;
using Upshift.Core.Hardware;
using Upshift.Core.Models;

namespace Upshift.Core.Install;

/// <summary>Whether HelixSR is offered on a card: CanSetUp, whether nobody has tested that card yet, and why not otherwise.</summary>
public sealed record HelixSrCard(bool CanSetUp, bool Untested, string? Reason, bool ShownForTesting = false);

/// <summary>What Upshift set up: the version, when, and the release zip's hash (written next to the files).</summary>
public sealed class HelixSrInstalled
{
    public string Version { get; set; } = "";
    public DateTime SetUpUtc { get; set; }
    public string AssetSha256 { get; set; } = "";
    public string? Card { get; set; }
}

/// <summary>What helixsr.log says about the last run. NetworkRan is null when the log doesn't say clearly.</summary>
public sealed record HelixSrLogStatus(bool Exists, DateTime? WrittenLocal, bool? NetworkRan, string? Evidence, IReadOnlyList<string> KeyLines);

/// <summary>
/// HelixSR (experimental): an unofficial FSR 3.1-compatible upscaler DLL that runs NVIDIA's DLSS network on AMD cards,
/// offered for RDNA 1 and RDNA 2. Upshift sets it up once, into a shared folder in its data folder
/// (%LocalAppData%\Upshift\helixsr\files), by running the author's own setup (HelixSrSetup). A game uses it through
/// OptiScaler: following the OptiScaler section of HelixSR's README, Dx12Upscaler=fsr31 and FfxDx12Path / FfxDx12SRPath
/// point at HelixSR's DLL in the shared folder, so no HelixSR file is ever put in a game folder. HelixSR and FSR 4 both use
/// OptiScaler's FSR slot, so only one of them can be on in a game.
/// </summary>
public static class HelixSr
{
    public const string ChoiceId = "helixsr";
    public const string ChoiceLabel = "DLSS 3.7 via HelixSR (experimental)";
    public const string RowName = "HelixSR";
    public const string Experimental = "Experimental";
    public const string RowSubtitle = "For AMD RDNA 1 and RDNA 2 cards (RX 5000 and RX 6000 series). DLSS 3.7 upscaling for cards without FSR 4.";

    public const string DllName = "amd_fidelityfx_dx12.dll";
    public const string UpscalerDllName = "amd_fidelityfx_upscaler_dx12.dll";
    public const string WeightsName = "helixsr_weights.bin";
    public const string KernelsName = "helixsr_kernels.pak";
    public const string LogName = "helixsr.log";
    public const string MarkerName = "upshift-helixsr.json";

    /// <summary>What a game needs in the shared folder. The two network files are NVIDIA's: never copied anywhere else.</summary>
    public static readonly string[] RequiredFiles = { DllName, UpscalerDllName, WeightsName, KernelsName };

    /// <summary>Files that hold NVIDIA's network: never shared, never put in a report.</summary>
    public static bool IsNetworkFile(string name) =>
        name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);

    public static string Root(string dataDir) => Path.Combine(dataDir, "helixsr");
    public static string FilesDir(string dataDir) => Path.Combine(Root(dataDir), "files");

    /// <summary>Where HelixSR's own setup keeps its tools (Python, numpy, the shader compiler). Its path is fixed in the script.</summary>
    public static string ToolsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HelixSR");

    // ---------------- cards ----------------

    /// <summary>
    /// HelixSR is offered on the catalog's generations (RDNA 1 and RDNA 2, including RDNA 2 integrated graphics). On other
    /// cards the reason is said plainly. <paramref name="showForTesting"/> (Settings > Developer options) offers it anyway.
    /// </summary>
    public static HelixSrCard ForCard(GpuInfo? gpu, HelixSrRelease release, bool showForTesting)
    {
        if (gpu is not null && release.Generations.Contains(gpu.Generation))
            return new HelixSrCard(true, release.UntestedGenerations.Contains(gpu.Generation), null);

        var reason = gpu is null ? "Not available: Upshift couldn't detect your graphics card." : gpu.Vendor switch
        {
            GpuVendor.Nvidia when OptiScalerOptions.IsNvidiaRtx(gpu) => $"Not needed on this card: your {ShortName(gpu.Name)} already runs DLSS.",
            GpuVendor.Amd when gpu.Generation is "RDNA 4" or "RDNA 3" => "Not needed on this card: FSR 4 is the better choice.",
            GpuVendor.Amd when gpu.Generation.StartsWith("RDNA", StringComparison.OrdinalIgnoreCase) =>
                "Not offered on this card: Upshift offers HelixSR only for RDNA 1 and RDNA 2 cards. On your card, try FSR 4 (INT8) instead.",
            GpuVendor.Amd => "Not supported on this card: HelixSR needs an AMD RDNA card (Radeon RX 5000 or newer).",
            GpuVendor.Intel when gpu.Generation is "Battlemage" or "Alchemist" => "Not needed on this card: XeSS runs on your Arc card's own AI cores.",
            _ => "Not offered on this card: HelixSR is made for AMD Radeon RX 5000 and RX 6000 cards."
        };
        return showForTesting
            ? new HelixSrCard(true, true, "Shown for testing (Settings > Developer options). " + reason, ShownForTesting: true)
            : new HelixSrCard(false, false, reason);
    }

    /// <summary>"NVIDIA GeForce RTX 4070" → "RTX 4070"; other names as they are.</summary>
    public static string ShortName(string name) =>
        Regex.Match(name, @"(RTX|GTX|RX|Arc)\s*[A-Z]?\d{3,4}\s*(Ti|SUPER|XT|XTX|GRE)?", RegexOptions.IgnoreCase) is { Success: true } m ? m.Value.Trim() : name;

    /// <summary>The per-game option: an offered card, a Direct3D 12 game, never a game with anti-cheat.</summary>
    public static bool OfferedFor(GameInfo game, HelixSrCard card) =>
        card.CanSetUp && game.Api.HasFlag(GraphicsApi.D3D12) && !game.HasAntiCheat;

    // ---------------- OptiScaler.ini ----------------

    /// <summary>
    /// The values that point OptiScaler at HelixSR (README, "Using with OptiScaler"): FSR 3.1 as the DX12 upscaler and
    /// both FidelityFX paths at HelixSR's DLL in the shared folder. UpscalerIndex goes back to what the file had (its
    /// default, auto), since HelixSR's DLL offers its own list.
    /// </summary>
    public static List<IniSetting> Settings(string filesDir) => new()
    {
        new("Upscalers", "Dx12Upscaler", "fsr31"),
        new("FSR", "UpscalerIndex", null),
        new("Libraries", "FfxDx12Path", Path.Combine(filesDir, DllName)),
        new("Libraries", "FfxDx12SRPath", Path.Combine(filesDir, UpscalerDllName))
    };

    /// <summary>Puts OptiScaler's own FidelityFX paths back (what the file had).</summary>
    public static List<IniSetting> PathsBack() => new()
    {
        new("Libraries", "FfxDx12Path", null),
        new("Libraries", "FfxDx12SRPath", null)
    };

    /// <summary>True when the game's OptiScaler.ini points either FidelityFX path into Upshift's HelixSR folder.</summary>
    public static bool InUse(IniFile ini, string dataDir)
    {
        var root = Path.GetFullPath(Root(dataDir)).TrimEnd('\\') + "\\";
        bool Points(string key) => ini.Get("Libraries", key) is { } v && v != OptiScalerOptions.Auto
                                   && TryFull(v)?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true;
        return Points("FfxDx12Path") || Points("FfxDx12SRPath");
    }

    private static string? TryFull(string path)
    {
        try { return Path.GetFullPath(path); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    // ---------------- the shared folder ----------------

    /// <summary>The first file a game needs that's missing from the shared folder, or null when everything is there.</summary>
    public static string? MissingFile(string dataDir) =>
        RequiredFiles.FirstOrDefault(f => !File.Exists(Path.Combine(FilesDir(dataDir), f)) || new FileInfo(Path.Combine(FilesDir(dataDir), f)).Length == 0);

    /// <summary>What's set up in the shared folder (null when nothing is, or it's incomplete).</summary>
    public static HelixSrInstalled? Installed(string dataDir)
    {
        if (MissingFile(dataDir) is not null) return null;
        try
        {
            return JsonSerializer.Deserialize<HelixSrInstalled>(File.ReadAllText(Path.Combine(FilesDir(dataDir), MarkerName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // ---------------- the game's previous upscaler ----------------

    /// <summary>
    /// The upscaler each game had before HelixSR was chosen (data folder, helixsr-previous.json), so a game whose HelixSR
    /// files are missing can go back to it. Kept outside the shared folder, so removing HelixSR keeps it.
    /// </summary>
    public static string? PreviousChoice(string dataDir, string gameId) =>
        ReadPrevious(dataDir).TryGetValue(gameId, out var id) ? id : null;

    public static void SetPreviousChoice(string dataDir, string gameId, string? choiceId)
    {
        var all = ReadPrevious(dataDir);
        if (choiceId is null) all.Remove(gameId); else all[gameId] = choiceId;
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "helixsr-previous.json"), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, string> ReadPrevious(string dataDir)
    {
        try
        {
            var path = Path.Combine(dataDir, "helixsr-previous.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    // ---------------- helixsr.log ----------------

    /// <summary>
    /// Reads helixsr.log in the shared folder (HelixSR writes it next to its DLL, so all games share one log: it describes
    /// the game that ran last). Runs before <paramref name="sinceUtc"/> don't count. The README says the log names the
    /// network it runs, says when the network files are missing (then only a simple upscale runs) and when the card isn't
    /// supported; Upshift looks for those, and says so when the log isn't clear.
    /// </summary>
    public static HelixSrLogStatus ReadLog(string dataDir, DateTime? sinceUtc)
    {
        var path = Path.Combine(FilesDir(dataDir), LogName);
        if (!File.Exists(path) || (sinceUtc is { } since && File.GetLastWriteTimeUtc(path) < since))
            return new HelixSrLogStatus(false, null, null, null, Array.Empty<string>());
        List<string> lines;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HelixSrLogStatus(false, null, null, null, Array.Empty<string>());
        }
        return ParseLog(lines) with { Exists = true, WrittenLocal = File.GetLastWriteTime(path) };
    }

    private static readonly string[] NotRunWords = { "missing", "placeholder", "not supported", "simple upscale", "failed", "error" };
    private static readonly string[] RanWords = { "network", "model e", "wave size", "kernels loaded" };

    public static HelixSrLogStatus ParseLog(IReadOnlyList<string> lines)
    {
        var key = lines.Where(l => NotRunWords.Concat(RanWords).Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Select(l => l.Trim()).Distinct().TakeLast(8).ToList();
        var notRun = lines.LastOrDefault(l => NotRunWords.Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase)));
        if (notRun is not null) return new HelixSrLogStatus(true, null, false, notRun.Trim(), key);
        var ran = lines.LastOrDefault(l => RanWords.Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase)));
        return new HelixSrLogStatus(true, null, ran is null ? null : true, ran?.Trim(), key);
    }
}
