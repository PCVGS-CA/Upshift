using Upshift.App.Helpers;
using Upshift.Core.Detection;
using Upshift.Core.Install;
using Upshift.Core.Models;
using Upshift.Core.Wiki;

namespace Upshift.App.ViewModels;

/// <summary>One row of "Upscalers in this game".</summary>
public sealed class UpscalerRowViewModel
{
    public UpscalerRowViewModel(UpscalerRow row, GameInfo game, InstallManifest? manifest, IReadOnlyList<UpscalerFileItem> items)
    {
        Feature = row.Feature;
        // A DLSS file's own version gets its marketing name; PCGamingWiki's "3.5" already is one.
        Version = row.Version is null ? ""
            : row.Family == UpscalerFamily.Dlss && row.VersionFromFile && row.FileName is not null ? Ui.VersionLabel(row.Version, row.FileName)
            : row.Version;
        Subtitle = row.FileName ?? "Built into the game";

        var item = row.RelativePath is null ? null : items.FirstOrDefault(i => i.RelativePath.Equals(row.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            // Who changed the file: Upshift's update, OptiScaler's own copy, or the game putting its old one back.
            if (UpscalerFileText.Subtitle(item) is { } who) Subtitle = $"{row.FileName} · {who}";
            Updated = item.State == UpscalerFileState.UpdatedByUpshift;
            if (item.Target is { } target)
                UpdateNote = $"Update available: {Ui.VersionLabel(target.Version, item.FileName)}"
                             + (Services.UpscalerUpdates.IsOnThisPc(target) ? "" : $" · {UpscalerFileText.ReleaseName(target)} (downloads when you update)");
        }
        else if (manifest is { Removed: false } && game.TargetDir is not null && row.RelativePath is not null)
        {
            // Other files OptiScaler's install put in the game (and that are still as it left them).
            var full = Path.GetFullPath(Path.Combine(game.InstallDir, row.RelativePath));
            ManifestFile? Match(IEnumerable<ManifestFile> files) =>
                files.FirstOrDefault(f => Path.GetFullPath(Path.Combine(game.TargetDir, f.Path)).Equals(full, StringComparison.OrdinalIgnoreCase));

            if (Match(manifest.Replaced) is { } replaced && OptiScalerInstaller.IsUnchanged(game.TargetDir, replaced))
            {
                var backup = replaced.Backup is null ? null : Path.Combine(game.TargetDir, replaced.Backup);
                var original = backup is not null && File.Exists(backup) ? FileVersions.Read(backup) : null;
                Subtitle = OptiScalerInstaller.UserFileNames.Contains(replaced.Path, StringComparer.OrdinalIgnoreCase)
                    ? $"{row.FileName} · your own file, added through Upshift · the game's copy backed up"
                    : $"{row.FileName} · replaced by OptiScaler {manifest.Version}'s copy · original {(original is null ? "" : Ui.VersionLabel(original, row.FileName!) + " ")}backed up";
            }
            else if (Match(manifest.Added) is { } added && OptiScalerInstaller.IsUnchanged(game.TargetDir, added))
            {
                Subtitle = OptiScalerInstaller.UserFileNames.Contains(added.Path, StringComparer.OrdinalIgnoreCase)
                    ? $"{row.FileName} · added by you through Upshift"
                    : $"{row.FileName} · added by OptiScaler {manifest.Version} (not the game's own)";
            }
        }
        Chip = UpscalerSectionViewModel.Chip(row.Tech, row.Family);
    }

    /// <summary>Upshift replaced this row's file with a newer one (the game's original is backed up).</summary>
    public bool Updated { get; }

    /// <summary>"Update available: 310.9.1 (DLSS 4.5)", shown in the accent colour; empty when there's none.</summary>
    public string UpdateNote { get; } = "";
    public bool HasUpdateNote => UpdateNote.Length > 0;

    public ChipViewModel Chip { get; }
    public string Feature { get; }
    public string Version { get; }
    public string Subtitle { get; }
}

/// <summary>
/// "Upscalers in this game": the file scan and PCGamingWiki merged into one list,
/// the "Show files" list of every upscaler and Streamline file, and the PCGamingWiki credit line.
/// </summary>
public sealed class UpscalerSectionViewModel
{
    private const string WikiSearch = "https://www.pcgamingwiki.com/w/index.php?search=";

    public UpscalerSectionViewModel(GameInfo game, WikiEntry? wiki, bool checking, IReadOnlyList<UpscalerFileItem> items)
    {
        var rows = UpscalerList.Build(game.Upscalers, wiki);
        CoreRows = rows;
        var manifest = game.TargetDir is null ? null : OptiScalerInstaller.ReadManifest(game.TargetDir);
        Rows = rows.Select(r => new UpscalerRowViewModel(r, game, manifest, items)).ToList();
        Chips = UpscalerList.Techs(rows)
            .Select(t => Chip(t.Tech, t.Family, Rows.Any(r => r.Updated && r.Chip.Text.Equals(t.Tech, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        Files = game.Upscalers
            .Select(u => u.RelativePath)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        PageUri = wiki?.PageUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var page)
            ? page
            : new Uri(WikiSearch + Uri.EscapeDataString(game.Name));

        FooterSuffix = wiki switch
        {
            { Status: WikiStatus.NoPage } => ", PCGamingWiki had no page for this game",
            { Status: WikiStatus.Failed } => ", PCGamingWiki couldn't be reached",
            null when checking => ", checking PCGamingWiki…",
            _ => ""
        };
    }

    public List<UpscalerRowViewModel> Rows { get; }
    public IReadOnlyList<UpscalerRow> CoreRows { get; }
    public bool HasRows => Rows.Count > 0;
    public bool HasNoRows => Rows.Count == 0;

    /// <summary>Technologies once each, in list order, for the chips on the game card.</summary>
    public List<ChipViewModel> Chips { get; }

    public List<string> Files { get; }
    public bool HasFiles => Files.Count > 0;

    public Uri PageUri { get; }
    public string FooterSuffix { get; }

    /// <summary>DLSS in NVIDIA green, FSR in AMD red, XeSS in Intel blue; TSR and other engine upscalers in grey.</summary>
    public static ChipViewModel Chip(string tech, UpscalerFamily? family, bool updated = false) =>
        new(tech, family is { } f ? Ui.FamilyColors(f) : Ui.VendorColors(GpuVendor.Unknown), updated);
}

/// <summary>The recommendation lines, with visibility flags for the optional ones.</summary>
public sealed class RecommendationViewModel
{
    public RecommendationViewModel(Recommendation rec)
    {
        Upscaler = rec.UpscalerLine;
        Reason = rec.UpscalerReason;
        FrameGen = rec.FrameGenLine ?? "";
        InGameInput = rec.InGameInputLine ?? "";
        ChoiceId = rec.UpscalerChoiceId;
    }

    public string Upscaler { get; }
    public string Reason { get; }
    public string FrameGen { get; }
    public bool HasFrameGen => FrameGen.Length > 0;
    public string InGameInput { get; }
    public bool HasInGameInput => InGameInput.Length > 0;
    public string? ChoiceId { get; }
}
