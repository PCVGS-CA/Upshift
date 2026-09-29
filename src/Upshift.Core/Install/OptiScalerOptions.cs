using Upshift.Core.Catalog;
using Upshift.Core.Hardware;
using Upshift.Core.Models;

namespace Upshift.Core.Install;

/// <summary>One OptiScaler.ini value to write. A null Value means "put back what the file had before this app changed it".</summary>
public sealed record IniSetting(string Section, string Key, string? Value);

public sealed record UpscalerChoice(string Id, string Label, IReadOnlyList<IniSetting> Settings);

public sealed record FrameGenMode(string Id, string Label, string Input, string Output, bool Enabled, string? Note = null);

/// <summary>A DLSS model choice; Unavailable is set when the game's DLSS file is too old for it.</summary>
public sealed record DlssModelChoice(string Id, string Label, string? Note, bool Available, IReadOnlyList<IniSetting> Settings);

/// <summary>
/// What OptiScaler can do for one game on one graphics card: the upscaler list, the frame generation modes,
/// the DLSS models and the FSR 4 (INT8) options, each with the OptiScaler.ini values it writes.
/// Option names and values come from OptiScaler v0.9.4's own OptiScaler.ini.
/// </summary>
public static class OptiScalerOptions
{
    public const string Auto = "auto";

    // ---------------- Upscaler ----------------

    /// <summary>The [Upscalers] key for the game's graphics API.</summary>
    public static string UpscalerKey(GraphicsApi api) =>
        api.HasFlag(GraphicsApi.D3D12) ? "Dx12Upscaler"
        : api.HasFlag(GraphicsApi.D3D11) ? "Dx11Upscaler"
        : api.HasFlag(GraphicsApi.Vulkan) ? "VulkanUpscaler"
        : "Dx12Upscaler";

    public static bool IsNvidiaRtx(GpuInfo? gpu) =>
        gpu?.Vendor == GpuVendor.Nvidia && gpu.Generation is "Blackwell" or "Ada Lovelace" or "Ampere" or "Turing";

    /// <summary>Cards that can run XeSS's DP4a path and XeSS frame generation.</summary>
    public static bool HasDp4a(GpuInfo? gpu) => gpu is not null && gpu.Generation switch
    {
        "Older NVIDIA" or "Older AMD" or "RDNA 1" or "Radeon integrated" or "Intel integrated" or "Unknown" => false,
        _ => true
    };

    public static bool Fsr4Official(GpuInfo? gpu, Fsr4Support fsr4) =>
        gpu is not null && !gpu.IsIntegrated && fsr4.OfficialGenerations.Contains(gpu.Generation);

    public static bool Int8OptionsShown(GpuInfo? gpu, Fsr4Support fsr4) =>
        gpu is not null && !Fsr4Official(gpu, fsr4) && fsr4.Int8Generations.Contains(gpu.Generation);

    /// <summary>
    /// Only the upscalers OptiScaler can use on this card. FSR 4 is listed when it's official on the card or an INT8 option is on.
    /// </summary>
    public static List<UpscalerChoice> Upscalers(GpuInfo? gpu, GraphicsApi api, Fsr4Support fsr4, bool int8On)
    {
        var key = UpscalerKey(api);
        var dx11 = key == "Dx11Upscaler";
        var vulkan = key == "VulkanUpscaler";
        var list = new List<UpscalerChoice> { new("auto", "Automatic (OptiScaler decides)", Up(key, Auto, fsrIndex: Auto)) };

        if (IsNvidiaRtx(gpu)) list.Add(new("dlss", "DLSS", Up(key, "dlss", Auto)));
        if (HasDp4a(gpu))
        {
            // Native DX11 XeSS is Arc only; other cards use XeSS through DX11-on-12.
            var xess = dx11 && gpu!.Vendor != GpuVendor.Intel ? "xess_12" : "xess";
            list.Add(new("xess", "XeSS", Up(key, xess, Auto)));
        }
        if (Fsr4Official(gpu, fsr4) || int8On)
            list.Add(new("fsr4", "FSR 4", Up(key, dx11 || vulkan ? "fsr31_12" : "fsr31", "0")));
        list.Add(new("fsr31", "FSR 3.1", Up(key, "fsr31", "1")));
        list.Add(new("fsr22", "FSR 2.2", Up(key, "fsr22", Auto)));
        return list;
    }

    private static List<IniSetting> Up(string key, string value, string fsrIndex) => new()
    {
        new("Upscalers", key, value),
        // [FSR] UpscalerIndex: 0 = FSR 4, 1 = FSR 3.1.5, 2 = FSR 2.3.4.
        new("FSR", "UpscalerIndex", fsrIndex)
    };

    /// <summary>Maps a suggestion ("DLSS", "XeSS through OptiScaler", "FSR 4"…) to an upscaler choice id.</summary>
    public static string? ChoiceForSuggestion(string? suggestion)
    {
        if (suggestion is null) return null;
        if (suggestion.StartsWith("DLSS", StringComparison.OrdinalIgnoreCase)) return "dlss";
        if (suggestion.StartsWith("XeSS", StringComparison.OrdinalIgnoreCase)) return "xess";
        if (suggestion.StartsWith("FSR 4", StringComparison.OrdinalIgnoreCase)) return "fsr4";
        if (suggestion.StartsWith("FSR", StringComparison.OrdinalIgnoreCase)) return "fsr31";
        return null;
    }

    // ---------------- Frame generation ----------------

    public static readonly FrameGenMode Off = new("off", "Off", "nofg", "nofg", false);
    public static readonly FrameGenMode Native = new("native", "Game's own", "nofg", "nofg", false, "Turn frame generation on in the game's own menu.");
    public static readonly FrameGenMode Nukems = new("nukems", "FSR FG via Nukem's mod (uses the game's DLSS FG)", "nukems", "nukems", true, "Turn on DLSS frame generation in the game's menu.");
    public static readonly FrameGenMode DlssgFsr = new("dlssg-fsrfg", "FSR FG, from the game's DLSS FG", "dlssg", "fsrfg", true, "Turn on DLSS frame generation in the game's menu.");
    public static readonly FrameGenMode DlssgXe = new("dlssg-xefg", "XeSS FG, from the game's DLSS FG", "dlssg", "xefg", true, "Needs borderless windowed mode.");
    public static readonly FrameGenMode FsrFsr = new("fsrfg-fsrfg", "FSR FG, from the game's FSR FG", "fsrfg", "fsrfg", true, "Turn on FSR frame generation in the game's menu.");
    public static readonly FrameGenMode FsrXe = new("fsrfg-xefg", "XeSS FG, from the game's FSR FG", "fsrfg", "xefg", true, "Needs borderless windowed mode.");
    public static readonly FrameGenMode OptiFsr = new("optifg-fsrfg", "OptiFG → FSR FG (any game; the HUD may need HUDfix)", "upscaler", "fsrfg", true, "Needs an upscaler active in the game.");
    public static readonly FrameGenMode OptiXe = new("optifg-xefg", "OptiFG → XeSS FG (any game)", "upscaler", "xefg", true, "Needs an upscaler active in the game and borderless windowed mode.");

    public static readonly IReadOnlyList<FrameGenMode> AllFrameGenModes =
        new[] { Off, Native, Nukems, DlssgFsr, DlssgXe, FsrFsr, FsrXe, OptiFsr, OptiXe };

    /// <summary>
    /// Frame generation modes this card and game can use. DLSSG-via-Streamline input needs Streamline 2+
    /// (so Streamline 1 games like The Witcher 3 only get Nukem's), XeSS FG needs DP4a and doesn't do Vulkan,
    /// and anything the OptiScaler wiki says doesn't work for the game is left out.
    /// </summary>
    public static List<FrameGenMode> FrameGenModes(GpuInfo? gpu, GameInfo game, IReadOnlyCollection<string> hiddenByWiki)
    {
        var files = game.Upscalers.Select(u => u.FileName.ToLowerInvariant()).ToHashSet();
        var hasDlssFg = files.Contains("nvngx_dlssg.dll") || files.Contains("sl.dlss_g.dll");
        var hasFsrFg = files.Contains("amd_fidelityfx_framegeneration_dx12.dll");
        var hasXeFg = files.Contains("libxess_fg.dll");
        var streamline = game.Upscalers.FirstOrDefault(u => u.FileName.Equals("sl.interposer.dll", StringComparison.OrdinalIgnoreCase));
        var streamline2 = streamline?.Version is { } v && int.TryParse(v.Split('.')[0], out var major) && major >= 2;
        var vulkanOnly = game.Api.HasFlag(GraphicsApi.Vulkan) && !game.Api.HasFlag(GraphicsApi.D3D12);
        var xeFgOk = HasDp4a(gpu) && !vulkanOnly;
        var rtx40Plus = gpu?.Vendor == GpuVendor.Nvidia && gpu.Generation is "Blackwell" or "Ada Lovelace";

        var modes = new List<FrameGenMode> { Off };
        if ((hasDlssFg && rtx40Plus) || hasFsrFg || (hasXeFg && HasDp4a(gpu))) modes.Add(Native);
        if (hasDlssFg) modes.Add(Nukems);
        if (hasDlssFg && streamline2) modes.Add(DlssgFsr);
        if (hasDlssFg && streamline2 && xeFgOk) modes.Add(DlssgXe);
        if (hasFsrFg) modes.Add(FsrFsr);
        if (hasFsrFg && xeFgOk) modes.Add(FsrXe);
        modes.Add(OptiFsr);
        if (xeFgOk) modes.Add(OptiXe);
        return modes.Where(m => !hiddenByWiki.Contains(m.Id)).ToList();
    }

    public static List<IniSetting> FrameGenSettings(FrameGenMode mode) => new()
    {
        new("FrameGen", "Enabled", mode.Enabled ? "true" : "false"),
        new("FrameGen", "FGInput", mode.Input),
        new("FrameGen", "FGOutput", mode.Output)
    };

    public static FrameGenMode? CurrentFrameGen(IniFile ini)
    {
        var input = ini.Get("FrameGen", "FGInput") ?? Auto;
        var output = ini.Get("FrameGen", "FGOutput") ?? Auto;
        if (input is Auto or "nofg" && output is Auto or "nofg") return Off;
        return AllFrameGenModes.FirstOrDefault(m => m.Enabled && m.Input == input && m.Output == output);
    }

    // ---------------- DLSS models ----------------

    public static readonly string[] QualityModes = { "DLAA", "UltraQuality", "Quality", "Balanced", "Performance", "UltraPerformance" };

    public static string QualityModeLabel(string mode) => mode switch
    {
        "UltraQuality" => "Ultra Quality",
        "UltraPerformance" => "Ultra Performance",
        _ => mode
    };

    /// <summary>The game's DLSS Super Resolution version (from nvngx_dlss.dll), or null.</summary>
    public static Version? GameDlssVersion(GameInfo game) =>
        game.Upscalers.FirstOrDefault(u => u.FileName.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase))?.Version is { } v
            ? ParseVersion(v) : null;

    public static bool PresetAvailable(DlssPreset preset, Version? gameDlss) =>
        gameDlss is not null && gameDlss >= ParseVersion(preset.MinVersion);

    /// <summary>
    /// "Game default", "NVIDIA recommended" (M for Performance, L for Ultra Performance, K for the rest),
    /// then each preset on its own. Presets the game's DLSS file is too old for come back with Available = false.
    /// </summary>
    public static List<DlssModelChoice> DlssModels(IReadOnlyList<DlssPreset> presets, Version? gameDlss)
    {
        var byId = presets.ToDictionary(p => p.Id);
        var list = new List<DlssModelChoice>
        {
            new("default", "Game default", "Let the game decide.", true, DefaultPresetSettings())
        };

        if (byId.TryGetValue("K", out var k) && byId.TryGetValue("L", out var l) && byId.TryGetValue("M", out var m))
        {
            var perMode = QualityModes.ToDictionary(q => q, q => q switch { "Performance" => m.Value, "UltraPerformance" => l.Value, _ => k.Value });
            list.Add(new("recommended", "NVIDIA recommended",
                "M for Performance, L for Ultra Performance, K for everything else.",
                PresetAvailable(k, gameDlss) && PresetAvailable(l, gameDlss) && PresetAvailable(m, gameDlss),
                PerModeSettings(perMode)));
        }

        foreach (var preset in presets)
            list.Add(new($"preset-{preset.Id}", preset.Name, preset.Note, PresetAvailable(preset, gameDlss),
                PerModeSettings(QualityModes.ToDictionary(q => q, _ => preset.Value), forAll: preset.Value)));
        return list;
    }

    /// <summary>Settings for the Advanced choice: one preset value (0 = game default) per quality mode.</summary>
    public static List<IniSetting> PerModeSettings(IReadOnlyDictionary<string, int> perMode, int? forAll = null)
    {
        var list = new List<IniSetting>
        {
            new("DLSS", "RenderPresetOverride", "true"),
            new("DLSS", "RenderPresetForAll", forAll?.ToString() ?? Auto)
        };
        foreach (var mode in QualityModes)
            list.Add(new("DLSS", "RenderPreset" + mode, perMode.TryGetValue(mode, out var v) && v > 0 ? v.ToString() : Auto));
        return list;
    }

    public static List<IniSetting> DefaultPresetSettings()
    {
        var list = new List<IniSetting> { new("DLSS", "RenderPresetOverride", null), new("DLSS", "RenderPresetForAll", null) };
        list.AddRange(QualityModes.Select(m => new IniSetting("DLSS", "RenderPreset" + m, null)));
        return list;
    }

    /// <summary>Reads back which DLSS model choice the ini currently matches ("advanced" when it's a custom mix).</summary>
    public static string CurrentDlssModel(IniFile ini, IReadOnlyList<DlssModelChoice> choices)
    {
        if (!string.Equals(ini.Get("DLSS", "RenderPresetOverride"), "true", StringComparison.OrdinalIgnoreCase)) return "default";
        foreach (var choice in choices.Where(c => c.Id != "default"))
            if (choice.Settings.All(s => string.Equals(ini.Get(s.Section, s.Key) ?? Auto, s.Value ?? Auto, StringComparison.OrdinalIgnoreCase)))
                return choice.Id;
        return "advanced";
    }

    public static Version ParseVersion(string v)
    {
        var parts = v.Split(new[] { '.', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var n) ? n : 0).Take(4).ToList();
        while (parts.Count < 2) parts.Add(0);
        return parts.Count switch
        {
            2 => new Version(parts[0], parts[1]),
            3 => new Version(parts[0], parts[1], parts[2]),
            _ => new Version(parts[0], parts[1], parts[2], parts[3])
        };
    }

    // ---------------- FSR 4 (INT8) ----------------

    public static List<IniSetting> ForceInt8Settings(bool on) => new() { new("FSR", "Fsr4ForceEnableInt8", on ? "true" : null) };
}
