using System.Text;

namespace Upshift.Core.ShaderPrebuild;

/// <summary>
/// "Test tools" for Pre-build shaders: empty the graphics drivers' and Windows' shader caches (with a backup), and put
/// the backup back. These caches are shared by every game on the PC, so emptying them affects all games (each builds
/// its shaders once again). Nothing is deleted: each cache's files move to a folder beside it, "&lt;cache&gt;.moved-&lt;time&gt;".
/// The first clear after a restore is "the backup": Restore puts exactly that one back; later clears' folders are
/// listed so they can be deleted by hand. Files a running program has open stay where they are. Nothing in a game
/// folder is ever touched. (The same steps as the earlier test kit's reset_caches and restore_caches scripts.)
/// </summary>
public sealed class ShaderCaches
{
    public sealed record Cache(string Name, string Path);

    private readonly string _logDir;
    public IReadOnlyList<Cache> Caches { get; }

    /// <param name="dataDir">Upshift's data folder (the log and the backup's marker go in shaderwarm).</param>
    /// <param name="caches">The caches (defaults to the real ones; tests pass their own).</param>
    public ShaderCaches(string dataDir, IReadOnlyList<Cache>? caches = null)
    {
        _logDir = ShaderLibrary.Root(dataDir);
        Caches = caches ?? Default();
    }

    public static IReadOnlyList<Cache> Default()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            new Cache("NVIDIA driver shader cache", System.IO.Path.Combine(local, "NVIDIA", "DXCache")),
            new Cache("NVIDIA driver shader cache (older place)", System.IO.Path.Combine(profile, "AppData", "LocalLow", "NVIDIA", "PerDriverVersion", "DXCache")),
            new Cache("AMD driver shader cache (DirectX 12)", System.IO.Path.Combine(local, "AMD", "DxcCache")),
            new Cache("AMD driver shader cache (DirectX 9-11)", System.IO.Path.Combine(local, "AMD", "DxCache")),
            new Cache("Windows' DirectX shader cache", System.IO.Path.Combine(local, "D3DSCache")),
        };
    }

    private string Marker => System.IO.Path.Combine(_logDir, "cache-original.txt");
    private string LogFile => System.IO.Path.Combine(_logDir, "cache-resets.txt");

    /// <summary>A backup from an earlier clear is waiting to be restored.</summary>
    public bool HasBackup => File.Exists(Marker);

    /// <summary>When the backup was made, or null.</summary>
    public DateTime? BackupTime => HasBackup ? ShaderLibrary.ParseStamp(SafeRead(Marker).Trim()) : null;

    /// <summary>What a clear would move: each cache found, how many files and how much, and the folder its backup goes to.</summary>
    public sealed record PreviewItem(string Name, string Path, int Files, long Bytes, string BackupPath);

    public List<PreviewItem> Preview()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var items = new List<PreviewItem>();
        foreach (var cache in Caches)
        {
            try
            {
                if (!Directory.Exists(cache.Path)) continue;
                var files = new DirectoryInfo(cache.Path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
                items.Add(new PreviewItem(cache.Name, cache.Path, files.Count, files.Sum(f => f.Length), $"{cache.Path}.moved-{stamp}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return items;
    }

    /// <summary>Moves every cache's files aside. Returns one line per cache, for the user.</summary>
    public List<string> Clear(string forGame)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var lines = new List<string>();
        Directory.CreateDirectory(_logDir);
        var log = new List<string> { $"Shader caches moved aside {stamp} (for {forGame})" };
        if (!HasBackup)
        {
            File.WriteAllText(Marker, stamp);
            log.Add("(the caches from before: Restore puts these back)");
        }
        foreach (var cache in Caches)
        {
            if (!Directory.Exists(cache.Path)) continue;
            var dest = $"{cache.Path}.moved-{stamp}";
            Directory.CreateDirectory(dest);
            int moved = 0, inUse = 0;
            foreach (var item in Directory.EnumerateFileSystemEntries(cache.Path).ToList())
            {
                try
                {
                    var target = System.IO.Path.Combine(dest, System.IO.Path.GetFileName(item));
                    if (Directory.Exists(item)) Directory.Move(item, target); else File.Move(item, target);
                    moved++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { inUse++; }
            }
            var line = $"{cache.Name}: {moved} moved aside" + (inUse > 0 ? $" ({inUse} in use by running programs, left in place)" : "");
            lines.Add(line);
            log.Add(line + $" → {dest}");
        }
        if (lines.Count == 0) lines.Add("No shader caches were found on this PC.");
        AppendLog(log);
        return lines;
    }

    /// <summary>Puts back the backup (the caches from before the first clear). Returns one line per cache.</summary>
    public List<string> Restore()
    {
        var lines = new List<string>();
        if (!HasBackup) { lines.Add("There's no backup to restore."); return lines; }
        var stamp = SafeRead(Marker).Trim();
        var now = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var log = new List<string> { $"Shader caches restored {now} (the ones moved aside at {stamp})" };
        foreach (var cache in Caches)
        {
            var from = $"{cache.Path}.moved-{stamp}";
            if (!Directory.Exists(from)) continue;
            Directory.CreateDirectory(cache.Path);
            int back = 0, kept = 0, failed = 0;
            foreach (var item in Directory.EnumerateFileSystemEntries(from).ToList())
            {
                var target = System.IO.Path.Combine(cache.Path, System.IO.Path.GetFileName(item));
                if (File.Exists(target) || Directory.Exists(target)) { kept++; continue; }   // the newer one stays
                try
                {
                    if (Directory.Exists(item)) Directory.Move(item, target); else File.Move(item, target);
                    back++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
            }
            var line = $"{cache.Name}: {back} put back";
            if (kept > 0) line += $"; {kept} were there again (newer), so the old ones stay in {System.IO.Path.GetFileName(from)}";
            if (failed > 0) line += $"; {failed} couldn't be moved (in use), left in {System.IO.Path.GetFileName(from)}";
            try { if (!Directory.EnumerateFileSystemEntries(from).Any()) { Directory.Delete(from); } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            lines.Add(line);
            log.Add(line);
        }
        var leftovers = Leftovers();
        if (leftovers.Count > 0)
        {
            lines.Add("Left over from later clears (not needed; delete them to free space): " + string.Join(", ", leftovers));
            log.AddRange(leftovers.Select(l => "left over: " + l));
        }
        if (lines.Count == 0) lines.Add("The backup folders weren't found, so nothing was put back.");
        File.Move(Marker, System.IO.Path.Combine(_logDir, $"cache-original.restored-{now}.txt"), overwrite: true);
        AppendLog(log);
        return lines;
    }

    /// <summary>"…moved-…" folders still next to the caches.</summary>
    public List<string> Leftovers()
    {
        var result = new List<string>();
        foreach (var cache in Caches)
        {
            try
            {
                var parent = System.IO.Path.GetDirectoryName(cache.Path);
                if (parent is null || !Directory.Exists(parent)) continue;
                result.AddRange(Directory.EnumerateDirectories(parent, System.IO.Path.GetFileName(cache.Path) + ".moved-*"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    /// <summary>When caches were cleared and restored, newest last (for the report).</summary>
    public List<(DateTime When, string What)> Events()
    {
        var events = new List<(DateTime, string)>();
        foreach (var line in SafeRead(LogFile).Split('\n'))
        {
            var cleared = line.StartsWith("Shader caches moved aside ");
            var restored = line.StartsWith("Shader caches restored ");
            if (!cleared && !restored) continue;
            var stamp = line.Split(' ').ElementAtOrDefault(cleared ? 4 : 3);
            if (stamp is not null && ShaderLibrary.ParseStamp(stamp) is { } t) events.Add((t, cleared ? "cache cleared" : "cache restored"));
        }
        return events;
    }

    private void AppendLog(IEnumerable<string> lines)
    {
        try { File.AppendAllText(LogFile, string.Join("\n", lines) + "\n\n", new UTF8Encoding(false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string SafeRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
