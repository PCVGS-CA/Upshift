using System.Diagnostics;

namespace Upshift.App.Services;

/// <summary>
/// Did Upshift close properly last time? Each main-window process writes data\running\&lt;process id&gt;.txt at start and
/// deletes it when its window closes. A file left by a process that isn't running any more means that session ended
/// badly (a crash, or Windows ending it). The helpers (--apply, --measure) never write one.
/// </summary>
public static class RunMarker
{
    public static bool LastRunEndedBadly { get; private set; }

    private static string Folder => Path.Combine(AppServices.DataDir, "running");
    private static string Mine => Path.Combine(Folder, $"{Environment.ProcessId}.txt");

    /// <summary>At start: looks for markers left by processes that aren't running, removes them, and writes this one's.</summary>
    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            foreach (var file in Directory.GetFiles(Folder, "*.txt"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid) || pid == Environment.ProcessId) continue;
                if (IsUpshift(pid)) continue;   // another Upshift window is open
                LastRunEndedBadly = true;
                File.Delete(file);
            }
            File.WriteAllText(Mine, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* never stops Upshift from starting */ }
    }

    /// <summary>When the main window closes normally.</summary>
    public static void End()
    {
        try { if (File.Exists(Mine)) File.Delete(Mine); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private static bool IsUpshift(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && string.Equals(p.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }
}
