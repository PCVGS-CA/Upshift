using Upshift.App.Helpers;
using Upshift.Core.Catalog;
using Upshift.Core.Install;
using Upshift.Core.Models;

namespace Upshift.App.ViewModels;

/// <summary>One row of "Upscaler files": a DLL the game has, its version, the newer one on offer, and its button.</summary>
public sealed class UpscalerFileRowViewModel
{
    public UpscalerFileRowViewModel(UpscalerFileItem item, GameCardViewModel card)
    {
        Item = item;
        Card = card;
        Chip = UpscalerSectionViewModel.Chip(Ui.FamilyLabel(item.Family), item.Family, item.State == UpscalerFileState.UpdatedByUpshift);
        Title = item.Feature.Length > 0 ? $"{item.FileName} · {item.Feature}" : item.FileName;
        Path = item.RelativePath;

        var current = item.CurrentVersion is null ? "unknown" : Ui.VersionLabel(item.CurrentVersion, item.FileName);
        VersionText = item.Target is { } t ? $"{current} → {Ui.VersionLabel(t.Version, item.FileName)}" : current;
        Status = UpscalerFileText.Subtitle(item) ?? item.Note ?? (item.Target is null && item.State == UpscalerFileState.GameFile ? "Up to date" : "");
        CanUpdate = item.CanUpdate;
        UpdateLabel = item.State == UpscalerFileState.GameRestoredOld ? "Re-apply" : "Update";
        UpdateName = $"{UpdateLabel} {item.FileName}";
    }

    public UpscalerFileItem Item { get; }
    public GameCardViewModel Card { get; }
    public ChipViewModel Chip { get; }
    public string Title { get; }
    public string Path { get; }
    public string VersionText { get; }
    public string Status { get; }
    public bool HasStatus => Status.Length > 0;
    public bool CanUpdate { get; }
    public string UpdateLabel { get; }
    /// <summary>The button's accessible name, e.g. "Update nvngx_dlss.dll".</summary>
    public string UpdateName { get; }
}

/// <summary>Wording shared by "Upscalers in this game" and "Upscaler files".</summary>
public static class UpscalerFileText
{
    /// <summary>Who changed the file, or null when it's the game's own untouched file.</summary>
    public static string? Subtitle(UpscalerFileItem item)
    {
        string V(string? version) => version is null ? "unknown" : Ui.VersionLabel(version, item.FileName);
        return item.State switch
        {
            UpscalerFileState.UpdatedByUpshift => $"Updated by Upshift · original {V(item.OriginalVersion)} backed up",
            UpscalerFileState.GameRestoredOld => $"The game put its old file back ({V(item.CurrentVersion)}). Re-apply to update it again.",
            UpscalerFileState.ChangedSince => "Changed since Upshift updated it (a game update?) · original still backed up",
            UpscalerFileState.OptiScalerCopy when item.OptiScalerReplaced =>
                $"Replaced by OptiScaler {item.OptiScalerVersion}'s copy · original {V(item.OriginalVersion)} backed up",
            UpscalerFileState.OptiScalerCopy =>
                $"Added by OptiScaler {item.OptiScalerVersion} (not the game's own)",
            _ => null
        };
    }

    /// <summary>
    /// The DLSS bar: "DLSS 4.5 (310.9.1) is ready for this game. It has 3.1.1 (DLSS 3). Updating unlocks the DLSS 4 and
    /// 4.5 models."
    /// </summary>
    public static string DlssBar(string current, string target)
    {
        var table = AppServices.Catalog.DlssVersionNames;
        var targetName = DlssNames.Name(table, target) ?? "A newer DLSS";
        var text = $"{targetName} ({target}) is ready for this game. It has {Ui.DlssLabel(current)}.";
        var unlocked = UnlockedModelNames(current, target);
        if (unlocked.Count > 0)
            text += $" Updating unlocks the {JoinNames(unlocked)} models.";
        return text;
    }

    /// <summary>The DLSS names ("DLSS 4", "DLSS 4.5") of the models a game gets by going from current to target.</summary>
    public static List<string> UnlockedModelNames(string current, string target)
    {
        var from = UpscalerFiles.ParseVersion(current);
        var to = UpscalerFiles.ParseVersion(target);
        return AppServices.Catalog.DlssPresets
            .Where(p => UpscalerFiles.ParseVersion(p.MinVersion) is var min && min > from && min <= to)
            .Select(p => DlssNames.Name(AppServices.Catalog.DlssVersionNames, p.MinVersion))
            .OfType<string>()
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>["DLSS 4", "DLSS 4.5"] → "DLSS 4 and 4.5".</summary>
    private static string JoinNames(List<string> names)
    {
        if (names.Count == 1) return names[0];
        var rest = names.Skip(1).Select(n => n.StartsWith("DLSS ") ? n[5..] : n).ToList();
        return $"{names[0]}{(rest.Count > 1 ? ", " + string.Join(", ", rest.Take(rest.Count - 1)) : "")} and {rest[^1]}";
    }
}
