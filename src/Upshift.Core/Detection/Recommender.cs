using Upshift.Core.Catalog;
using Upshift.Core.Hardware;
using Upshift.Core.Install;
using Upshift.Core.Models;

namespace Upshift.Core.Detection;

/// <summary>The "Suggested for your …" lines at the top of "Upscalers in this game". Always a suggestion, never a requirement.</summary>
public sealed record Recommendation(
    string UpscalerLine,
    string UpscalerReason,
    string? FrameGenLine,
    string? InGameInputLine,
    /// <summary>The OptiScaler upscaler choice this suggestion maps to ("dlss", "xess", "fsr4", "fsr31"), for the options dropdown.</summary>
    string? UpscalerChoiceId);

public static class Recommender
{
    public static Recommendation? For(GpuInfo? gpu, IReadOnlyList<UpscalerRow> rows, UpscalerCatalog catalog, string? wikiInGameInput)
    {
        if (gpu is null) return null;
        var rule = catalog.Recommendations.Rules.FirstOrDefault(r =>
            r.Vendor == gpu.Vendor && r.Generations.Contains(gpu.Generation)
            && (!r.RequiresOfficialFsr4 || OptiScalerOptions.Fsr4Official(gpu, catalog.Fsr4)));

        var techs = rows.Select(r => r.Tech).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var swappable = techs.Contains("DLSS") || techs.Contains("FSR") || techs.Contains("XeSS");

        string upscaler, reason;
        string? choice = null;
        if (rule is not null && swappable)
        {
            var tech = rule.Upscaler.Split(' ')[0];
            upscaler = techs.Contains(tech) ? rule.Upscaler : rule.UpscalerViaOptiScaler ?? $"{rule.Upscaler} through OptiScaler";
            if (rule.Alternative is { } alt) upscaler += $", or {alt}";
            reason = rule.Reason;
            choice = OptiScalerOptions.ChoiceForSuggestion(rule.Upscaler);
        }
        else if (rule is null && swappable)
        {
            // A card none of the rules know: FSR runs everywhere.
            upscaler = "FSR 3.1";
            reason = "It runs on any recent graphics card.";
            choice = "fsr31";
        }
        else
        {
            var engine = rows.FirstOrDefault(r => r.Family is null);
            upscaler = engine?.Tech ?? catalog.Recommendations.Fallback.Upscaler;
            reason = engine is null
                ? "This game has no DLSS, FSR or XeSS for OptiScaler to work with, so its own anti-aliasing and resolution settings are the options."
                : catalog.Recommendations.Fallback.Reason;
        }

        var frameGen = rule?.FrameGen is { } fg ? $"Frame generation: try {fg}. {rule.FrameGenReason}" : null;
        var inGame = wikiInGameInput is null
            ? null
            : $"With OptiScaler, set the game's own upscaler to {wikiInGameInput} (OptiScaler wiki advice for this game).";

        return new Recommendation($"Suggested for your {gpu.Name}: {upscaler}", reason, frameGen, inGame, choice);
    }
}
