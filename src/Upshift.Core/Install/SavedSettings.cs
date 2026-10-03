using System.Text.Json;

namespace Upshift.Core.Install;

/// <summary>A copy of a game's OptiScaler.ini that Upshift kept, so a later install can bring the settings back.</summary>
public sealed record SavedIni(string Path, DateTime SavedUtc, string? ComponentId, string? Version, bool WasLeftover);

/// <summary>
/// %LocalAppData%\Upshift\saved-settings\&lt;game&gt;\: Uninstall saves the game's OptiScaler.ini here before removing
/// it, and Install backs up a leftover OptiScaler.ini here before removing it. Each copy is
/// OptiScaler-&lt;yyyyMMdd-HHmmss&gt;.ini, with a .json next to it saying where it came from.
/// </summary>
public static class SavedSettings
{
    public const string FolderName = "saved-settings";

    private sealed class Info
    {
        public string? GameName { get; set; }
        public string? ComponentId { get; set; }
        public string? Version { get; set; }
        public DateTime SavedUtc { get; set; }
        public bool WasLeftover { get; set; }
    }

    /// <summary>The game's folder for saved settings (named after the game, made safe for a folder name).</summary>
    public static string FolderFor(string dataDir, string gameName)
    {
        var safe = string.Concat(gameName.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "game";
        return System.IO.Path.Combine(dataDir, FolderName, safe.Length > 80 ? safe[..80] : safe);
    }

    /// <summary>The most recent copy kept for the game, or null.</summary>
    public static SavedIni? Latest(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            return Directory.EnumerateFiles(folder, "OptiScaler-*.ini")
                .Select(Read)
                .OfType<SavedIni>()
                .OrderByDescending(s => s.SavedUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static SavedIni? Read(string iniPath)
    {
        var infoPath = System.IO.Path.ChangeExtension(iniPath, ".json");
        try
        {
            var info = File.Exists(infoPath) ? JsonSerializer.Deserialize<Info>(File.ReadAllText(infoPath)) : null;
            return new SavedIni(iniPath, info?.SavedUtc ?? File.GetLastWriteTimeUtc(iniPath), info?.ComponentId, info?.Version, info?.WasLeftover ?? false);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    /// <summary>Copies the ini into the folder, checks the copy, and records where it came from. Returns the copy.</summary>
    public static string Save(string folder, string iniPath, string gameName, string? componentId, string? version, bool wasLeftover)
    {
        Directory.CreateDirectory(folder);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var copy = System.IO.Path.Combine(folder, $"OptiScaler-{stamp}.ini");
        for (var i = 2; File.Exists(copy); i++) copy = System.IO.Path.Combine(folder, $"OptiScaler-{stamp}-{i}.ini");
        File.Copy(iniPath, copy);
        if (OptiScalerInstaller.Sha256(copy) != OptiScalerInstaller.Sha256(iniPath))
            throw new IOException("The saved copy of OptiScaler.ini doesn't match the file.");
        var info = new Info { GameName = gameName, ComponentId = componentId, Version = version, SavedUtc = DateTime.UtcNow, WasLeftover = wasLeftover };
        File.WriteAllText(System.IO.Path.ChangeExtension(copy, ".json"), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
        return copy;
    }

    /// <summary>
    /// The values in a saved ini worth putting into a fresh one: keys the fresh file still has, whose saved value
    /// differs from the fresh default. With the old version's own ini, values that were simply that version's
    /// defaults are left out too, so a newer default isn't overwritten by an old one.
    /// </summary>
    public static List<IniSetting> ValuesToRestore(string savedIni, string freshIni, string? oldDefaultsIni)
    {
        var saved = IniFile.Load(savedIni);
        var fresh = IniFile.Load(freshIni);
        var oldDefaults = oldDefaultsIni is not null && File.Exists(oldDefaultsIni) ? IniFile.Load(oldDefaultsIni) : null;
        var list = new List<IniSetting>();
        foreach (var (section, key, value) in saved.Entries())
        {
            if (!fresh.Has(section, key)) continue;
            if (string.Equals(fresh.Get(section, key), value, StringComparison.Ordinal)) continue;
            if (oldDefaults is not null && string.Equals(oldDefaults.Get(section, key), value, StringComparison.Ordinal)) continue;
            if (list.Any(s => s.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && s.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new IniSetting(section, key, value));
        }
        return list;
    }
}
