using System.Text.RegularExpressions;

namespace Upshift.Core.Install;

/// <summary>
/// What the removed "Pre-build shaders" feature (1.3.0-beta.1) can have left: in a game's OptiScaler.ini, the two values
/// that load its recorder ([Plugins] Path = Upshift's shaderwarm\plugins, LoadAsiPlugins = true), or the earlier test
/// kit's four; in Upshift's data, the shaderwarm folder; beside the driver's shader caches, "&lt;cache&gt;.moved-&lt;stamp&gt;"
/// folders from "Clear driver shader cache". Upshift removes all of it once, at start-up.
/// </summary>
public static class ShaderLeftovers
{
    public static string Folder(string dataDir) => Path.Combine(dataDir, "shaderwarm");
    private static string PluginsDir(string dataDir) => Path.Combine(Folder(dataDir), "plugins");

    /// <summary>Anything left to clean up (the data folder, or moved-aside caches).</summary>
    public static bool Pending(string dataDir) => Directory.Exists(Folder(dataDir)) || MovedCaches().Count > 0;

    /// <summary>The game's OptiScaler.ini still loads the recorder.</summary>
    public static bool Loads(IniFile ini, string dataDir) =>
        string.Equals(ini.Get("Plugins", "Path")?.Trim().TrimEnd('\\'), PluginsDir(dataDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        && string.Equals(ini.Get("Plugins", "LoadAsiPlugins")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The values that turn it off. When Upshift recorded setting the path, "put back" (null) restores each value's
    /// original. Otherwise the earlier test kit set them by hand, with [Log] LogToFile and LogLevel: those still holding
    /// the kit's value go back to "auto" (they were "auto" before the kit), not recorded as Upshift's change.
    /// </summary>
    public static List<IniSetting> TurnOff(IniFile ini, IEnumerable<IniChange> recorded, string dataDir)
    {
        var changes = recorded.ToList();
        bool Recorded(string section, string key) =>
            changes.Any(c => c.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (Recorded("Plugins", "Path"))
            return new() { new("Plugins", "Path", null), new("Plugins", "LoadAsiPlugins", null) };
        var kit = new (string Section, string Key, string Value)[]
        {
            ("Plugins", "Path", PluginsDir(dataDir)), ("Plugins", "LoadAsiPlugins", "true"), ("Log", "LogToFile", "true"), ("Log", "LogLevel", "2")
        };
        return kit.Where(k => !Recorded(k.Section, k.Key)
                              && string.Equals(ini.Get(k.Section, k.Key)?.Trim().TrimEnd('\\'), k.Value.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                  .Select(k => new IniSetting(k.Section, k.Key, OptiScalerOptions.Auto, Unrecorded: true))
                  .ToList();
    }

    /// <summary>
    /// The driver shader caches' moved-aside copies go back where the driver hasn't made a newer file, the rest of each
    /// copy is deleted (only Upshift's "&lt;cache&gt;.moved-&lt;stamp&gt;" folders), and then Upshift's shaderwarm folder.
    /// Returns what happened, for the log.
    /// </summary>
    public static List<string> RemoveData(string dataDir)
    {
        var lines = new List<string>();
        foreach (var (cache, moved) in MovedCaches())
        {
            int back = 0, dropped = 0;
            try
            {
                Directory.CreateDirectory(cache);
                foreach (var item in Directory.EnumerateFileSystemEntries(moved).ToList())
                {
                    var target = Path.Combine(cache, Path.GetFileName(item));
                    try
                    {
                        if (File.Exists(target) || Directory.Exists(target)) { dropped++; continue; }
                        if (Directory.Exists(item)) Directory.Move(item, target); else File.Move(item, target);
                        back++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                Directory.Delete(moved, recursive: true);
                lines.Add($"{moved}: {back} put back, {dropped} older copies removed");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lines.Add($"{moved}: left in place ({ex.Message})"); }
        }
        try
        {
            if (Directory.Exists(Folder(dataDir))) { Directory.Delete(Folder(dataDir), recursive: true); lines.Add($"removed {Folder(dataDir)}"); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lines.Add($"{Folder(dataDir)}: left in place ({ex.Message})"); }
        return lines;
    }

    /// <summary>The driver shader caches Upshift's "Clear" knew, and their "&lt;cache&gt;.moved-&lt;yyyyMMdd-HHmmss&gt;" copies.</summary>
    private static List<(string Cache, string Moved)> MovedCaches()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var caches = new[]
        {
            Path.Combine(local, "NVIDIA", "DXCache"), Path.Combine(profile, "AppData", "LocalLow", "NVIDIA", "PerDriverVersion", "DXCache"),
            Path.Combine(local, "AMD", "DxcCache"), Path.Combine(local, "AMD", "DxCache"), Path.Combine(local, "D3DSCache")
        };
        var found = new List<(string, string)>();
        foreach (var cache in caches)
        {
            try
            {
                var parent = Path.GetDirectoryName(cache);
                if (parent is null || !Directory.Exists(parent)) continue;
                var pattern = new Regex("^" + Regex.Escape(Path.GetFileName(cache)) + @"\.moved-\d{8}-\d{6}$", RegexOptions.IgnoreCase);
                foreach (var dir in Directory.EnumerateDirectories(parent).Where(d => pattern.IsMatch(Path.GetFileName(d))))
                    found.Add((cache, dir));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }
}
