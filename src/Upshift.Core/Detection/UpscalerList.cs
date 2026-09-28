using System.Text.RegularExpressions;
using Upshift.Core.Models;
using Upshift.Core.Wiki;

namespace Upshift.Core.Detection;

/// <summary>One line of "Upscalers in this game": a technology and one of its features.</summary>
/// <param name="Tech">"DLSS", "FSR", "XeSS", "TSR", or another engine upscaler such as "TAAU".</param>
/// <param name="Family">The vendor family for colouring, or null for engine upscalers (shown in grey).</param>
/// <param name="FileName">The DLL this row comes from, or null when only PCGamingWiki mentions it.</param>
/// <param name="RelativePath">That DLL's path inside the game folder.</param>
/// <param name="VersionFromFile">True when Version is the DLL's own file version (not PCGamingWiki's, e.g. "3.5").</param>
public sealed record UpscalerRow(string Tech, UpscalerFamily? Family, string Feature, string? Version, string? FileName, int Order,
    string? RelativePath = null, bool VersionFromFile = false);

/// <summary>
/// Merges what the file scan found with what PCGamingWiki lists into one ordered list:
/// DLSS (Super Resolution, Ray Reconstruction, Frame Generation), FSR (Upscaling, Frame Generation),
/// XeSS (Super Resolution, Frame Generation, XeLL), TSR, then any other engine upscaler.
/// When both sources give a version, the file's version wins. Streamline itself is not a row.
/// </summary>
public static partial class UpscalerList
{
    private sealed record Slot(string Tech, UpscalerFamily? Family, string Feature, int Order);

    private static readonly Slot DlssSr = new("DLSS", UpscalerFamily.Dlss, "Super Resolution", 0);
    private static readonly Slot DlssRr = new("DLSS", UpscalerFamily.Dlss, "Ray Reconstruction", 1);
    private static readonly Slot DlssFg = new("DLSS", UpscalerFamily.Dlss, "Frame Generation", 2);
    private static readonly Slot DlssNr = new("DLSS", UpscalerFamily.Dlss, "Neural Rendering (DLSS 5)", 3);
    private static readonly Slot FsrUp = new("FSR", UpscalerFamily.Fsr, "Upscaling", 10);
    private static readonly Slot FsrFg = new("FSR", UpscalerFamily.Fsr, "Frame Generation", 11);
    private static readonly Slot XessSr = new("XeSS", UpscalerFamily.Xess, "Super Resolution", 20);
    private static readonly Slot XessFg = new("XeSS", UpscalerFamily.Xess, "Frame Generation", 21);
    private static readonly Slot Xell = new("XeSS", UpscalerFamily.Xess, "XeLL (low latency)", 22);
    private static readonly Slot Tsr = new("TSR", null, "Upscaling", 30);

    /// <summary>
    /// Which row each DLL belongs to. Earlier files in the list are preferred for the row's file name and version:
    /// Streamline's sl.dlss*.dll only stand in when the NVIDIA DLL itself is missing, and their version
    /// (the Streamline plugin's) is never shown as the DLSS version.
    /// </summary>
    private static readonly (string File, Slot Slot, bool VersionIsTechVersion)[] FileSlots =
    {
        ("nvngx_dlss.dll", DlssSr, true),
        ("sl.dlss.dll", DlssSr, false),
        ("nvngx_dlssd.dll", DlssRr, true),
        ("nvngx_dlssg.dll", DlssFg, true),
        ("sl.dlss_g.dll", DlssFg, false),
        ("nvngx_dlssnr.dll", DlssNr, true),
        ("amd_fidelityfx_upscaler_dx12.dll", FsrUp, true),
        // FidelityFX API DLLs: their version (1.0.x) is the API's, not FSR's, so the wiki's FSR version is shown instead.
        ("amd_fidelityfx_dx12.dll", FsrUp, false),
        ("amd_fidelityfx_vk.dll", FsrUp, false),
        ("ffx_fsr2_api_x64.dll", FsrUp, true),
        ("ffx_fsr2_api_dx12_x64.dll", FsrUp, true),
        ("ffx_fsr2_api_vk_x64.dll", FsrUp, true),
        ("amd_fidelityfx_loader_dx12.dll", FsrUp, false),
        ("amd_fidelityfx_framegeneration_dx12.dll", FsrFg, true),
        ("libxess.dll", XessSr, true),
        ("libxess_dx11.dll", XessSr, true),
        ("libxess_fg.dll", XessFg, true),
        ("libxell.dll", Xell, true),
    };

    /// <summary>True for DLLs that are a row in "Upscalers in this game" (nvngx_dlss.dll, libxess.dll…).</summary>
    public static bool IsUpscalerFile(string fileName) =>
        FileSlots.Any(f => f.File.Equals(fileName, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<UpscalerRow> Build(IEnumerable<UpscalerDll> files, WikiEntry? wiki)
    {
        var rows = new Dictionary<Slot, (string? Version, string? File, int Rank, string? Path, bool FromFile)>();

        // 1. Files, best file per row first.
        foreach (var dll in files)
        {
            var index = Array.FindIndex(FileSlots, f => f.File.Equals(dll.FileName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) continue; // Streamline core files (sl.interposer.dll etc.) aren't rows.
            var (_, slot, versionCounts) = FileSlots[index];
            if (rows.TryGetValue(slot, out var existing) && existing.Rank <= index) continue;
            rows[slot] = (versionCounts ? dll.Version : null, dll.FileName, index, dll.RelativePath, versionCounts && dll.Version is not null);
        }

        // 2. PCGamingWiki adds rows the files don't show, and a version where the file gave none
        //    (the highest it lists, e.g. "FSR 2.1, FSR 3, FSR 3.1, FSR 4" gives 4).
        if (wiki?.Status == WikiStatus.Found)
        {
            var others = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
            var wikiVersions = new Dictionary<Slot, string?>();
            foreach (var (tech, isFrameGen) in wiki.UpscalingTech.Select(t => (t, false)).Concat(wiki.FrameGenTech.Select(t => (t, true))))
            {
                var (slot, version) = SlotForWikiTech(tech, isFrameGen, others);
                if (slot is null) continue;
                if (!wikiVersions.TryGetValue(slot, out var best) || IsNewer(version, best)) wikiVersions[slot] = version ?? best;
            }

            foreach (var (slot, version) in wikiVersions)
            {
                if (!rows.TryGetValue(slot, out var existing)) rows[slot] = (version, null, int.MaxValue, null, false);
                else if (existing.Version is null) rows[slot] = existing with { Version = version };
            }
        }

        return rows
            .OrderBy(r => r.Key.Order)
            .ThenBy(r => r.Key.Tech, StringComparer.OrdinalIgnoreCase)
            .Select(r => new UpscalerRow(r.Key.Tech, r.Key.Family, r.Key.Feature, r.Value.Version, r.Value.File, r.Key.Order,
                r.Value.Path, r.Value.FromFile))
            .ToList();
    }

    /// <summary>The technologies in the list, once each, in list order (for the chips on game cards).</summary>
    public static IReadOnlyList<(string Tech, UpscalerFamily? Family)> Techs(IReadOnlyList<UpscalerRow> rows) =>
        rows.GroupBy(r => r.Tech, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Min(r => r.Order))
            .Select(g => (g.First().Tech, g.First().Family))
            .ToList();

    /// <summary>
    /// "DLSS 3.5" is DLSS Super Resolution 3.5; "DLSS FG" / "DLSS MFG" is DLSS Frame Generation; "DLSS RR" is Ray Reconstruction;
    /// "FSR 3.0" is FSR Upscaling; "XeSS 2" is XeSS Super Resolution; "TSR", "TAAU" and the like are engine upscalers.
    /// </summary>
    private static (Slot? Slot, string? Version) SlotForWikiTech(string tech, bool isFrameGenList, Dictionary<string, Slot> others)
    {
        var words = tech.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return (null, null);
        var name = words[0];
        var rest = words.Skip(1).ToList();
        var frameGen = isFrameGenList || rest.Any(w => w.Equals("FG", StringComparison.OrdinalIgnoreCase) || w.Equals("MFG", StringComparison.OrdinalIgnoreCase));
        var rayRecon = rest.Any(w => w.Equals("RR", StringComparison.OrdinalIgnoreCase));
        var version = rest.Select(w => VersionNumber().Match(w)).FirstOrDefault(m => m.Success)?.Value;

        switch (name.ToUpperInvariant())
        {
            case "DLSS": return (frameGen ? DlssFg : rayRecon ? DlssRr : DlssSr, frameGen ? null : version);
            case "FSR": return (frameGen ? FsrFg : FsrUp, frameGen ? null : version);
            case "XESS": return (frameGen ? XessFg : XessSr, frameGen ? null : version);
            case "XELL": return (Xell, version);
            case "TSR": return (Tsr, version);
        }

        // Other engine upscalers (TAAU and the like) go in grey after TSR. Frame generation from unknown tech is skipped.
        if (frameGen || name.Length < 2) return (null, null);
        if (!others.TryGetValue(name, out var slot))
        {
            slot = new Slot(name, null, "Upscaling", 40);
            others[name] = slot;
        }
        return (slot, version);
    }

    private static bool IsNewer(string? candidate, string? current)
    {
        if (candidate is null) return false;
        if (current is null) return true;
        return Parse(candidate) > Parse(current);

        static Version Parse(string v) =>
            Version.TryParse(v.Contains('.') ? v : v + ".0", out var parsed) ? parsed : new Version(0, 0);
    }

    [GeneratedRegex(@"^\d+(\.\d+)*$")]
    private static partial Regex VersionNumber();
}
