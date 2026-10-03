using System.Text;

namespace Upshift.App.Services;

/// <summary>
/// Writes any crash to %LocalAppData%\Upshift\logs\crash.log: the exception with its inner exceptions and stack
/// traces, the version, the command line and Windows' version. It's installed first thing in Main, so it also covers
/// start-up (before the window, the data folder or any service exists), and it never depends on them.
/// </summary>
/// <remarks>
/// It can't record a start that Windows stops before Upshift's own code runs: Smart App Control or another code
/// integrity policy refusing to load Upshift.dll ("An Application Control policy has blocked this file", exit code
/// 0xe0434352). Those show up in Event Viewer: Applications and Services Logs > Microsoft > Windows > CodeIntegrity >
/// Operational (event 3077), and as a ".NET Runtime" error 1026 when no debugger is attached.
/// </remarks>
public static class CrashLog
{
    private static readonly object Sync = new();
    private static bool _installed;

    /// <summary>%LocalAppData%\Upshift\logs\crash.log (fixed, so it works before anything else is set up).</summary>
    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Upshift", "logs", "crash.log");

    /// <summary>Hooks the process-wide handlers. Call once, first thing in Main.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.ExceptionObject as Exception, e.IsTerminating ? "Unhandled exception (Upshift closed)" : "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) => Write(e.Exception, "Unobserved task exception (Upshift kept running)");
    }

    /// <summary>Appends one entry. Never throws: a crash log that crashes would hide the real problem.</summary>
    public static void Write(Exception? exception, string what)
    {
        try
        {
            var text = new StringBuilder()
                .AppendLine("==================================================================")
                .AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {what}")
                .AppendLine($"Upshift {AppInfo.Version}  |  {Environment.OSVersion.VersionString}  |  .NET {Environment.Version}  |  pid {Environment.ProcessId}")
                .AppendLine($"Command line: {Environment.CommandLine}")
                .AppendLine(exception?.ToString() ?? "(no exception object)")
                .AppendLine()
                .ToString();
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, text);
            }
        }
        catch (Exception)
        {
            // Last resort: the temp folder, in case the data folder itself is the problem.
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "Upshift-crash.log"), $"{DateTime.Now:s} {what}: {exception}\r\n"); }
            catch (Exception) { }
        }
    }
}
