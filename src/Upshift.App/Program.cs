using System.Diagnostics;
using System.Runtime.InteropServices;
using Upshift.Core.Services;
using Velopack;

namespace Upshift.App;

/// <summary>
/// The entry point (instead of the one WinUI generates), so Velopack's install and uninstall hooks run before any
/// window is created.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // First of all: any crash from here on is written to %LocalAppData%\Upshift\logs\crash.log.
        Services.CrashLog.Install();
        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            Services.CrashLog.Write(ex, "Start-up failed");
            throw;
        }
    }

    private static void Run(string[] args)
    {
        // The copy the uninstaller started from %TEMP% (see Uninstalling): the questions and the removal from games, no window.
        if (args.Contains(AfterUninstallArg))
        {
            AfterUninstall();
            return;
        }

        // The elevated one-job helpers (Upshift.exe --apply plan.json, --measure job.json) skip Velopack: it must never apply an app
        // update or run a hook, only the install plan it was given.
        if (!args.Contains("--apply") && !args.Contains("--measure"))
        {
            VelopackApp.Build()
                // Only a real uninstall runs this; updates never do.
                .OnBeforeUninstallFastCallback(_ => Uninstalling())
                // After installing or updating (also over an older copy): Start menu and desktop shortcuts with Upshift's
                // icon, replacing any broken "Upshift" shortcut.
                .OnAfterInstallFastCallback(_ => Services.Shortcuts.Ensure(createMissing: true))
                .OnAfterUpdateFastCallback(_ => Services.Shortcuts.Ensure(createMissing: true))
                .Run();

            if (AppLocations.DataFolderProblem(DataMigration.NewDataDir, null) is { } problem)
            {
                MessageBoxW(IntPtr.Zero,
                    $"{problem}\n\nMove Upshift's program files to a folder of their own (for example, unzip the portable " +
                    "version somewhere else) and start it again.", "Upshift can't start", MbOk | MbIconError);
                return;
            }
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private const string AfterUninstallArg = "--after-uninstall";
    private const string CleanupCopyPrefix = "Upshift-uninstall-";

    /// <summary>
    /// Runs inside the uninstaller, before it removes %LocalAppData%\Upshift.App. Velopack stops this hook after 30
    /// seconds, and removing Upshift's changes from many games (with a Windows permission prompt for protected folders)
    /// can take longer than that, and the questions mustn't be rushed. So the hook only copies the program to
    /// %TEMP%\Upshift-uninstall-…, starts that copy with --after-uninstall and returns at once; the copy, which the
    /// uninstaller neither waits for nor stops, asks both questions and does the work, then deletes itself. If the copy
    /// can't be made in time, the old question (keep or delete the data) is asked here and the games are left as they are.
    /// </summary>
    private static void Uninstalling()
    {
        var clock = Stopwatch.StartNew();
        string? copy = null;
        try
        {
            copy = CopyProgram(AppLocations.ExeDir, TimeSpan.FromSeconds(18));
            if (copy is not null)
            {
                // Shell start: the copy inherits no handles from the uninstaller, so it isn't waited for.
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(copy, "Upshift.exe"),
                    Arguments = AfterUninstallArg,
                    WorkingDirectory = copy,
                    UseShellExecute = true
                })?.Dispose();
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Services.CrashLog.Write(ex, "The uninstaller couldn't start the removal from games");
            if (copy is not null) TryDeleteFolder(copy);
        }
        // About 28 seconds are left at most; keep a margin.
        var left = (int)Math.Max(0, 27_000 - clock.ElapsedMilliseconds);
        if (left > 5_000) AskToDeleteData(left, gamesLeft: true);
    }

    /// <summary>Copies the program folder; null when it isn't done before the deadline (the partial copy is deleted).</summary>
    private static string? CopyProgram(string from, TimeSpan deadline)
    {
        var clock = Stopwatch.StartNew();
        var to = Path.Combine(Path.GetTempPath(), CleanupCopyPrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (clock.Elapsed > deadline) { TryDeleteFolder(to); return null; }
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
        return to;
    }

    /// <summary>
    /// The copy started by the uninstaller: "Also remove OptiScaler and Upshift's changes from your games?" (Yes by
    /// default), the removal with a summary of what was done and skipped, then the question about Upshift's data, and
    /// finally the copy deletes itself.
    /// </summary>
    private static void AfterUninstall()
    {
        try
        {
            var dataDir = UninstallDataDir();
            if (Directory.Exists(dataDir) && AppLocations.DataFolderProblem(dataDir, null) is null)
            {
                DataFolder.Path = dataDir;
                RemoveFromGamesAfterUninstall();
                AskToDeleteData(120_000, gamesLeft: false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Services.CrashLog.Write(ex, "Removing Upshift from games after the uninstall failed");
            MessageBoxW(IntPtr.Zero, $"Removing Upshift's changes from your games stopped: {ex.Message}", "Uninstall Upshift", MbOk | MbIconWarning | MbTopmost);
        }
        finally
        {
            DeleteThisCopyLater();
        }
    }

    private static void RemoveFromGamesAfterUninstall()
    {
        var games = AppServices.Library.LoadCachedAsync().GetAwaiter().GetResult();
        var targets = Core.Install.GameCleanup.Find(games);
        var cacheBackup = Core.Install.ShaderLeftovers.Pending(AppServices.DataDir);
        if (targets.Count == 0 && !cacheBackup) return;

        var names = targets.Take(8).Select(t => "  • " + t.Name).ToList();
        if (targets.Count > 8) names.Add($"  • and {targets.Count - 8} more");
        if (cacheBackup) names.Add("  • what Pre-build shaders left (its data and moved-aside driver caches)");
        var answer = MessageBoxTimeoutW(IntPtr.Zero,
            "Also remove OptiScaler and Upshift's changes from your games?\n\n" + string.Join("\n", names) + "\n\n" +
            "Yes removes OptiScaler and everything Upshift added, and puts back the game's own DLSS, FSR and XeSS files that " +
            "Upshift replaced, using each game's own record. Files Upshift didn't add (REFramework, your saves, other mods) " +
            "stay where they are, and so do files that changed since. A game that's running is skipped and listed.\n\n" +
            "No leaves your games exactly as they are now.",
            "Uninstall Upshift", MbYesNo | MbIconQuestion | MbTopmost | MbSetForeground, 0, 300_000);
        // Yes is the default: the question timing out (nobody there) counts as Yes.
        if (answer != IdYes && answer != TimedOut) return;

        var summary = Services.RemoveFromGames.RunAsync(games, null).GetAwaiter().GetResult();
        MessageBoxTimeoutW(IntPtr.Zero, "Upshift's changes to your games:\n\n" + summary.Text(), "Uninstall Upshift",
            MbOk | (summary.Skipped.Count > 0 ? MbIconWarning : MbIconInformation) | MbTopmost | MbSetForeground, 0, 600_000);
    }

    /// <summary>The data folder; a Debug build can be pointed at a throwaway one (UPSHIFT_TEST_DATA) to test this flow.</summary>
    private static string UninstallDataDir()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("UPSHIFT_TEST_DATA") is { Length: > 0 } test) return test;
#endif
        return DataMigration.NewDataDir;
    }

    /// <summary>Deletes the %TEMP% copy a few seconds after it has closed (a running program can't delete itself).</summary>
    private static void DeleteThisCopyLater()
    {
        var dir = AppLocations.ExeDir;
        if (!Path.GetFileName(dir.TrimEnd('\\')).StartsWith(CleanupCopyPrefix, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/c timeout /t 5 /nobreak >nul & rmdir /s /q \"{dir}\"",
                WorkingDirectory = Path.GetTempPath(),
                CreateNoWindow = true,
                UseShellExecute = false
            })?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private static void TryDeleteFolder(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// "Do you also want to delete Upshift's data?" (No by default; the question closing by itself keeps the data, so a
    /// silent uninstall keeps it too).
    /// </summary>
    private static void AskToDeleteData(int timeoutMs, bool gamesLeft)
    {
        var dataDir = UninstallDataDir();
        if (!Directory.Exists(dataDir)) return;
        if (AppLocations.DataFolderProblem(dataDir, null) is not null) return;

        var answer = MessageBoxTimeoutW(IntPtr.Zero,
            "Do you also want to delete Upshift's data?\n\n" +
            $"{dataDir}\n\n" +
            "It holds your settings, the list of games, downloaded releases, saved OptiScaler settings and the files you added " +
            "in Settings. Choose No to keep it, so a later install carries on where you left off.\n\n" +
            (gamesLeft
                ? "Your games were left as they are: Upshift couldn't prepare removing its changes from them in time. Install Upshift " +
                  "again (or use the portable version) and choose Settings > Remove Upshift from all games.\n\n"
                : "") +
            $"This question closes by itself after {timeoutMs / 1000} seconds and keeps your data.",
            "Uninstall Upshift", MbYesNo | MbIconQuestion | MbDefButton2 | MbTopmost | MbSetForeground, 0, (uint)timeoutMs);
        if (answer != IdYes) return;

        try { Directory.Delete(dataDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBoxW(IntPtr.Zero, $"Some of Upshift's data couldn't be deleted:\n\n{ex.Message}\n\nYou can delete {dataDir} yourself.",
                "Uninstall Upshift", MbOk | MbIconWarning | MbTopmost);
        }
    }

    private const uint MbOk = 0x0, MbYesNo = 0x4, MbIconError = 0x10, MbIconQuestion = 0x20, MbIconWarning = 0x30,
        MbIconInformation = 0x40, MbDefButton2 = 0x100, MbSetForeground = 0x10000, MbTopmost = 0x40000;
    private const int IdYes = 6, TimedOut = 32000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    // Exported by user32 since Windows XP; returns 32000 when it times out.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxTimeoutW(IntPtr hWnd, string text, string caption, uint type, ushort languageId, uint milliseconds);
}
