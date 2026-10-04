using Upshift.Core.Detection;

namespace Upshift.Core.Install;

/// <summary>One file "Needs repair" is about, in plain words, and what Repair will do with it.</summary>
/// <param name="What">"Intel XeSS Frame Generation, part of OptiScaler".</param>
/// <param name="InstalledVersion">The version Upshift installed (from the downloaded release); null when unknown.</param>
/// <param name="NowVersion">The version there now; null when missing or unreadable.</param>
/// <param name="Action">What Repair will do with this file.</param>
/// <param name="LooksLikeGameUpdate">Other game files in the folder changed at the same moment.</param>
public sealed record RepairDetail(string Path, string What, string? InstalledVersion, string? NowVersion, bool Missing,
    string Action, bool LooksLikeGameUpdate, DateTime? ChangedLocal);

public static partial class OptiScalerInstaller
{
    /// <summary>
    /// The files Verify would report, each with what it is, the version Upshift installed (read from the downloaded
    /// release in <paramref name="releaseDir"/> when it's there), the version there now, and what Repair will do,
    /// following the same rules as Repair. A changed file is put down to a game update when at least three other files
    /// in its folder that OptiScaler didn't bring were written within two minutes of it.
    /// </summary>
    public static List<RepairDetail> RepairDetails(string target, string? releaseDir)
    {
        var details = new List<RepairDetail>();
        if (ReadManifest(target) is not { Removed: false } manifest) return details;
        var release = releaseDir is not null && Directory.Exists(releaseDir)
            ? FilesToCopy(releaseDir, manifest.ProxyName).ToDictionary(f => f.Relative, f => f.From, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var managed = AllEntries(manifest).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in AllEntries(manifest))
        {
            if (manifest.OverrideFor(file.Path) is not null) continue;
            var path = Path.Combine(target, file.Path);
            var exists = File.Exists(path);
            if (exists && (IsIni(file.Path) || IsUserFile(file.Path) || !HasChanged(file, path))) continue;

            release.TryGetValue(file.Path, out var from);
            var installed = from is not null ? FileVersions.Read(from) : null;
            var now = exists ? FileVersions.Read(path) : null;
            var ours = installed is null ? "OptiScaler's copy" : $"OptiScaler's v{installed}";
            string action;
            // Without the release on this PC, Repair downloads it first: only the user's own files aren't part of it.
            var partOfRelease = from is not null || (release.Count == 0 && !IsUserFile(file.Path));
            if (!partOfRelease)
                action = exists ? "Repair leaves it as it is." : "Repair forgets it; add it again from the OptiScaler options if you still want it.";
            else if (!exists)
                action = $"Repair copies {ours} back.";
            else if (file.Backup is null && manifest.Added.Contains(file) && !IsOptiScalerDll(path))
                action = $"Repair keeps the copy found there as the game's own (backed up, and put back when you uninstall OptiScaler), then copies {ours} back.";
            else if (file.Backup is not null && Sha256(path) != file.OriginalSha256 && !IsOptiScalerDll(path))
                action = $"Repair keeps this newer copy as the game's original (put back when you uninstall OptiScaler), then copies {ours} back.";
            else
                action = $"Repair copies {ours} back. The changed copy is kept in {StateFolder}\\repair….";

            DateTime? changed = exists ? File.GetLastWriteTime(path) : null;
            details.Add(new RepairDetail(file.Path, Describe(file.Path, manifest.ProxyName), installed, now, !exists, action,
                exists && WrittenWithOthers(path, target, managed), changed));
        }
        return details;
    }

    /// <summary>What a file of OptiScaler's package is, in plain words.</summary>
    private static string Describe(string relative, string proxy)
    {
        var name = Path.GetFileName(relative);
        if (relative.Equals(proxy, StringComparison.OrdinalIgnoreCase)) return $"OptiScaler itself, loaded by the game as {proxy}";
        if (relative.StartsWith("D3D12_Optiscaler", StringComparison.OrdinalIgnoreCase)) return "OptiScaler's DirectX 12 helper";
        if (name.Equals("fakenvapi.dll", StringComparison.OrdinalIgnoreCase)) return "fakenvapi, which stands in for NVIDIA Reflex on AMD and Intel cards (part of OptiScaler)";
        if (name.StartsWith("dlssg_to_fsr3", StringComparison.OrdinalIgnoreCase)) return "Nukem's DLSS-to-FSR frame generation (part of OptiScaler)";
        if (Fingerprints.UpscalerFiles.TryGetValue(name, out var fp))
        {
            var vendor = fp.Family switch
            {
                Models.UpscalerFamily.Xess => "Intel XeSS",
                Models.UpscalerFamily.Fsr => "AMD FidelityFX",
                Models.UpscalerFamily.Dlss => "NVIDIA DLSS",
                _ => ""
            };
            return $"{vendor} {fp.Feature}, part of OptiScaler".Trim();
        }
        return "part of OptiScaler";
    }

    /// <summary>At least three other files in the folder, not OptiScaler's, written within two minutes of this one.</summary>
    private static bool WrittenWithOthers(string path, string target, HashSet<string> managed)
    {
        try
        {
            var when = File.GetLastWriteTimeUtc(path);
            var dir = Path.GetDirectoryName(path)!;
            return Directory.EnumerateFiles(dir)
                .Where(f => !f.Equals(path, StringComparison.OrdinalIgnoreCase) && !managed.Contains(Path.GetRelativePath(target, f)))
                .Count(f => Math.Abs((File.GetLastWriteTimeUtc(f) - when).TotalMinutes) <= 2) >= 3;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
