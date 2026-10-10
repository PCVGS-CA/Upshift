using Upshift.Core.Models;

namespace Upshift.Core.Detection;

/// <summary>
/// Which upscaler to choose in the game's own menu once OptiScaler is installed. OptiScaler takes the game's upscaler
/// (its "input") and replaces it with the one picked in its options (the "output"), so the game must have one switched on.
/// Order, following OptiScaler: the wiki's advice for this game when it gives one (some games crash with DLSS inputs);
/// otherwise DLSS when the game offers it; otherwise FSR 3.1 or newer, which OptiScaler's README calls fully supported;
/// otherwise XeSS; otherwise older FSR, whose support "will depend on the developers' implementation" (README).
/// </summary>
public static class InGameChoice
{
    /// <summary>
    /// The upscaler to choose in the game, or null when the game has none for OptiScaler to replace. Rows for files that
    /// OptiScaler's own install added are left out: the game's menu doesn't offer those.
    /// </summary>
    public static string? Pick(IEnumerable<UpscalerRow> rows, string? wikiInput, ISet<string>? addedByOptiScaler = null)
    {
        if (!string.IsNullOrWhiteSpace(wikiInput)) return wikiInput.Trim();
        var game = rows.Where(r => r.RelativePath is null || addedByOptiScaler is null || !addedByOptiScaler.Contains(r.RelativePath)).ToList();
        bool Has(UpscalerFamily family, Func<UpscalerRow, bool>? when = null) =>
            game.Any(r => r.Family == family && r.Feature is "Super Resolution" or "Upscaling" && (when?.Invoke(r) ?? true));

        if (Has(UpscalerFamily.Dlss)) return "DLSS";
        if (Has(UpscalerFamily.Fsr, r => IsFsr31OrNewer(r.Version))) return "FSR 3.1 (or the newest FSR it lists)";
        if (Has(UpscalerFamily.Xess)) return "XeSS";
        if (Has(UpscalerFamily.Fsr)) return "FSR";
        return null;
    }

    /// <summary>The line under the options: "In the game's own graphics menu, choose DLSS as the upscaler. …"</summary>
    public static string? Line(string? pick, bool fromWiki, bool helixSr)
    {
        if (pick is null) return null;
        var why = fromWiki ? " (the OptiScaler wiki's advice for this game)" : "";
        var then = helixSr
            ? "OptiScaler takes it from there and runs HelixSR in its place."
            : "OptiScaler takes it from there and uses the upscaler chosen here.";
        return $"In the game's own graphics menu, choose {pick} as the upscaler{why}. {then}";
    }

    private static bool IsFsr31OrNewer(string? version)
    {
        if (version is null) return false;
        var parts = version.Split('.', ' ');
        if (!int.TryParse(parts[0], out var major)) return false;
        if (major != 3) return major > 3;
        return parts.Length > 1 && int.TryParse(parts[1], out var minor) && minor >= 1;
    }
}
