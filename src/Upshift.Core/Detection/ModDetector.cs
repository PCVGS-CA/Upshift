using Upshift.Core.Models;

namespace Upshift.Core.Detection;

/// <summary>Finds OptiScaler, ReShade or Special K that are already sitting next to the game exe.</summary>
public static class ModDetector
{
    public static List<ExistingMod> Detect(string targetDir)
    {
        var mods = new List<ExistingMod>();

        foreach (var proxy in Fingerprints.ProxyNames)
        {
            var path = Path.Combine(targetDir, proxy);
            if (!File.Exists(path)) continue;

            var info = FileVersions.TryGetInfo(path);
            var text = $"{info?.ProductName} {info?.FileDescription}";
            var version = info?.ProductVersion?.Trim();

            if (text.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase)
                || proxy.Equals("OptiScaler.asi", StringComparison.OrdinalIgnoreCase))
            {
                Add(mods, ModKind.OptiScaler, "OptiScaler", version, proxy, text.Trim());
            }
            else if (text.Contains("ReShade", StringComparison.OrdinalIgnoreCase))
            {
                Add(mods, ModKind.ReShade, "ReShade", version, proxy, text.Trim());
            }
            else if (text.Contains("Special K", StringComparison.OrdinalIgnoreCase))
            {
                Add(mods, ModKind.SpecialK, "Special K", version, proxy, text.Trim());
            }
        }

        if (File.Exists(Path.Combine(targetDir, "OptiScaler.ini")))
            Add(mods, ModKind.OptiScaler, "OptiScaler", null, null, null);
        if (File.Exists(Path.Combine(targetDir, "ReShade.ini")))
            Add(mods, ModKind.ReShade, "ReShade", null, null, null);
        if (File.Exists(Path.Combine(targetDir, "SpecialK64.dll")))
            Add(mods, ModKind.SpecialK, "Special K", FileVersions.Read(Path.Combine(targetDir, "SpecialK64.dll")), "SpecialK64.dll", null);

        return mods;
    }

    private static void Add(List<ExistingMod> mods, ModKind kind, string name, string? version, string? file, string? description)
    {
        // Keep the first (most detailed) entry for each kind.
        if (mods.Any(m => m.Kind == kind)) return;
        mods.Add(new ExistingMod { Kind = kind, Name = name, Version = version, FileName = file, Description = description });
    }
}
