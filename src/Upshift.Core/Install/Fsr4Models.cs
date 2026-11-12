using Upshift.Core.Hardware;
using Upshift.Core.Models;

namespace Upshift.Core.Install;

/// <summary>The FSR 4 model a game uses, as Upshift offers it: OptiScaler's own pick, the full model or the lite one.</summary>
public enum Fsr4ModelChoice { Auto, Full, Lite }

/// <summary>
/// The FSR 4 model choice per game, written only to that game's OptiScaler.ini as <c>[FSR] Fsr4Model</c>. Key and values are
/// from the OptiScaler build's own ini (the v10.0.0-pre1 nightly of 2026-11-11; 0.9.4 has no such key):
/// "0 = FP8 | 1 = INT8 | 2 = INT8 RDNA2 | 3 = INT8 Lite", "Default (auto) is the best one the GPU supports", "FP8 needs
/// RDNA 4 …and isn't available when Fsr4GpuSupport limits the GPU to INT8", "INT8 RDNA2 and INT8 Lite are only in the Vulkan
/// driver FSR (amdvkffx64)", "INT8 Lite only uses standard shader features so it can run on any GPU".
/// So: FSR 4 runs on AMD cards; Full is FP8 on RDNA 4 (INT8 when the ini limits the card to INT8) and INT8 on RDNA 3 and
/// RDNA 3.5; Lite is offered only in Vulkan games. Where the right full model isn't clear (RDNA 2 and older, other vendors),
/// only Auto (and Lite in Vulkan games on AMD) is offered.
/// </summary>
public static class Fsr4Models
{
    public const string Section = "FSR", Key = "Fsr4Model";
    public const string Auto = "auto", Fp8 = "0", Int8 = "1", Int8Rdna2 = "2", Int8Lite = "3";

    public const string NeedsNewerText = "Needs a newer OptiScaler";
    public const string Hint = "Try Lite if the frame rate is too low. Use Measure to compare.";

    public static string Label(Fsr4ModelChoice choice) => choice switch
    {
        Fsr4ModelChoice.Full => "Full model (best quality)",
        Fsr4ModelChoice.Lite => "Lite model (faster, for handhelds and low-power PCs)",
        _ => "Auto (recommended)"
    };

    /// <summary>"Full model", "Lite model", for "FSR 4 · Lite model".</summary>
    public static string ShortName(Fsr4ModelChoice choice) => choice switch
    {
        Fsr4ModelChoice.Full => "Full model",
        Fsr4ModelChoice.Lite => "Lite model",
        _ => "Auto model"
    };

    /// <summary>
    /// True when FSR 4 is this game's upscaler: OptiScaler's FSR slot for the game's API (fsr31, or fsr31_12 through DX12)
    /// with [FSR] UpscalerIndex 0 (FSR 4), as Upshift's "FSR 4" choice writes it.
    /// </summary>
    public static bool IsUpscaler(IniFile ini, GraphicsApi api) =>
        ini.Get("Upscalers", OptiScalerOptions.UpscalerKey(api))?.Trim().ToLowerInvariant() is "fsr31" or "fsr31_12"
        && ini.Get("FSR", "UpscalerIndex")?.Trim() == "0";

    /// <summary>True when this game's OptiScaler.ini has the FSR 4 model setting.</summary>
    public static bool Supported(IniFile ini) => ini.Has(Section, Key);

    private static bool Amd(GpuInfo? gpu) => gpu?.Vendor == GpuVendor.Amd;

    private static bool LimitedToInt8(IniFile? ini) =>
        ini?.Get("FSR", "Fsr4GpuSupport")?.Trim() is "2" or "3" or "4";

    /// <summary>The ini value for the full model on this card, or null when it isn't clear (then Full isn't offered).</summary>
    private static string? FullValue(GpuInfo? gpu, IniFile? ini)
    {
        if (!Amd(gpu)) return null;
        var generation = gpu!.Generation;
        if (generation.StartsWith("RDNA 4", StringComparison.OrdinalIgnoreCase)) return LimitedToInt8(ini) ? Int8 : Fp8;
        if (generation.StartsWith("RDNA 3", StringComparison.OrdinalIgnoreCase)) return Int8;   // RDNA 3 and RDNA 3.5 integrated
        return null;
    }

    /// <summary>The choices this card and game can use, Auto first. Auto only when unsure.</summary>
    public static IReadOnlyList<Fsr4ModelChoice> Offered(GpuInfo? gpu, GraphicsApi api, IniFile? ini)
    {
        var list = new List<Fsr4ModelChoice> { Fsr4ModelChoice.Auto };
        if (FullValue(gpu, ini) is not null) list.Add(Fsr4ModelChoice.Full);
        if (Amd(gpu) && api.HasFlag(GraphicsApi.Vulkan)) list.Add(Fsr4ModelChoice.Lite);
        return list;
    }

    /// <summary>The ini value for a choice on this card, or null when the card can't use it.</summary>
    public static string? Value(Fsr4ModelChoice choice, GpuInfo? gpu, GraphicsApi api, IniFile? ini) => choice switch
    {
        Fsr4ModelChoice.Auto => Auto,
        Fsr4ModelChoice.Full => FullValue(gpu, ini),
        Fsr4ModelChoice.Lite => Amd(gpu) && api.HasFlag(GraphicsApi.Vulkan) ? Int8Lite : null,
        _ => null
    };

    /// <summary>
    /// The game's current choice from its ini: auto (or no value) is Auto, INT8 Lite is Lite, FP8, INT8 and INT8 RDNA 2 count
    /// as Full. Null when the ini has no such setting or an unknown value.
    /// </summary>
    public static Fsr4ModelChoice? Current(IniFile ini)
    {
        if (!Supported(ini)) return null;
        return ini.Get(Section, Key)?.Trim().ToLowerInvariant() switch
        {
            null or "" or Auto => Fsr4ModelChoice.Auto,
            Int8Lite => Fsr4ModelChoice.Lite,
            Fp8 or Int8 or Int8Rdna2 => Fsr4ModelChoice.Full,
            _ => null
        };
    }

    public static List<IniSetting> Settings(string value) => new() { new IniSetting(Section, Key, value) };

    /// <summary>
    /// What a new install writes for the user's default (Settings > "Default FSR 4 model for new installs"): nothing for Auto,
    /// or when this card or game can't use the choice. A build without the setting drops it when the ini is written.
    /// </summary>
    public static List<IniSetting> ForNewInstall(Fsr4ModelChoice defaultChoice, GpuInfo? gpu, GraphicsApi api) =>
        defaultChoice != Fsr4ModelChoice.Auto && Value(defaultChoice, gpu, api, null) is { } value ? Settings(value) : new();

    /// <summary>"Auto", "Full", "Lite" as saved in settings.json.</summary>
    public static Fsr4ModelChoice Parse(string? saved) =>
        Enum.TryParse<Fsr4ModelChoice>(saved, true, out var choice) ? choice : Fsr4ModelChoice.Auto;
}
