using System.Text;
using Upshift.Core.Install;

namespace Upshift.Core.Services;

/// <summary>
/// Moves the app's data from the folder it used before the rename (%LocalAppData%\PCVGS\UpscalerManager) to
/// %LocalAppData%\Upshift, once. The old folder is only removed after everything has arrived. Paths stored inside the
/// data (e.g. cover pictures in library.json) are rewritten to the new place.
/// </summary>
public static class DataMigration
{
    public static string NewDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Upshift");

    public static string OldDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCVGS", "UpscalerManager");

    /// <summary>
    /// Returns the data folder to use. Normally the new one; if a move was needed and failed, the old one for this
    /// session (the move is tried again next time, since the new folder won't exist yet).
    /// </summary>
    public static string Run() => Run(OldDataDir, NewDataDir);

    public static string Run(string oldDir, string newDir)
    {
        if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return newDir;

        var summary = Describe(oldDir);
        string? problem = null;
        var how = "";
        try
        {
            // Same drive: one rename, nothing can be half-copied.
            Directory.CreateDirectory(Path.GetDirectoryName(newDir)!);
            Directory.Move(oldDir, newDir);
            how = "moved in one step";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = CopyThenRemove(oldDir, newDir, ex.Message);
            how = "copied, checked and the old copy removed";
        }

        if (problem is not null)
        {
            // Nothing half-done is left behind: the new folder was removed and the old one is untouched.
            TryLog(oldDir, $"DATA MOVE to {newDir} failed, still using {oldDir}: {problem}");
            return oldDir;
        }

        var rewritten = RewritePaths(newDir, oldDir);
        var parent = Path.GetDirectoryName(oldDir)!;
        var parentRemoved = false;
        try
        {
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
                parentRemoved = true;
            }
        }
        catch (IOException) { }

        var log = new InstallLog(newDir);
        log.Write($"DATA MOVE from {oldDir} to {newDir} ({how})");
        foreach (var line in summary) log.Write("  " + line);
        log.Write($"  paths updated inside {rewritten} file(s)");
        log.Write(parentRemoved ? $"  removed the empty {parent} folder" : $"  {parent} kept (it has other things in it)");
        return newDir;
    }

    /// <summary>For a move across drives: copy everything, compare, and only then delete the old folder.</summary>
    private static string? CopyThenRemove(string oldDir, string newDir, string whyNotRename)
    {
        var staging = newDir + ".moving";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            foreach (var file in Directory.EnumerateFiles(oldDir, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(staging, Path.GetRelativePath(oldDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to);
                if (OptiScalerInstaller.Sha256(file) != OptiScalerInstaller.Sha256(to))
                    throw new IOException($"{Path.GetRelativePath(oldDir, file)} didn't copy correctly.");
            }
            foreach (var dir in Directory.EnumerateDirectories(oldDir, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(staging, Path.GetRelativePath(oldDir, dir)));

            Directory.Move(staging, newDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (Exception) { }
            return $"{whyNotRename}; copying failed too: {ex.Message}";
        }

        try { Directory.Delete(oldDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Everything is safely in the new place; the old copy is only clutter now.
            TryLog(newDir, $"DATA MOVE: the old folder {oldDir} couldn't be removed ({ex.Message}); it can be deleted by hand.");
        }
        return null;
    }

    /// <summary>Stored absolute paths (cover pictures, user files, cached downloads) now point into the new folder.</summary>
    private static int RewritePaths(string newDir, string oldDir)
    {
        var count = 0;
        var oldEscaped = oldDir.Replace("\\", "\\\\");
        var newEscaped = newDir.Replace("\\", "\\\\");
        foreach (var file in Directory.EnumerateFiles(newDir, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(file);
                if (!text.Contains(oldEscaped, StringComparison.OrdinalIgnoreCase)) continue;
                var updated = text.Replace(oldEscaped, newEscaped, StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(file, updated, new UTF8Encoding(false));
                count++;
            }
            catch (IOException) { }
        }
        return count;
    }

    private static List<string> Describe(string dir)
    {
        var lines = new List<string>();
        try
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                long bytes = entry is FileInfo f ? f.Length
                    : ((DirectoryInfo)entry).EnumerateFiles("*", SearchOption.AllDirectories).Sum(x => x.Length);
                var files = entry is FileInfo ? 1 : ((DirectoryInfo)entry).EnumerateFiles("*", SearchOption.AllDirectories).Count();
                lines.Add($"{entry.Name}{(entry is DirectoryInfo ? "\\" : "")}: {files} file(s), {bytes:N0} bytes");
            }
        }
        catch (IOException) { }
        return lines;
    }

    private static void TryLog(string dataDir, string line)
    {
        try { new InstallLog(dataDir).Write(line); } catch (Exception) { }
    }
}
