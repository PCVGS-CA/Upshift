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
        // The elevated one-job helpers (Upshift.exe --apply plan.json, --measure job.json) skip Velopack: it must never apply an app
        // update or run a hook, only the install plan it was given.
        if (!args.Contains("--apply") && !args.Contains("--measure"))
        {
            VelopackApp.Build()
                .OnBeforeUninstallFastCallback(_ => AskToDeleteDataOnUninstall())
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

    /// <summary>
    /// Runs inside the uninstaller, before it removes %LocalAppData%\Upshift.App. The data folder is kept unless the
    /// user says Yes. Velopack stops this hook after 30 seconds, so the question closes itself after 25 and keeps
    /// the data; a silent uninstall therefore keeps it too.
    /// </summary>
    private static void AskToDeleteDataOnUninstall()
    {
        var dataDir = DataMigration.NewDataDir;
        if (!Directory.Exists(dataDir)) return;
        if (AppLocations.DataFolderProblem(dataDir, null) is not null) return;

        var answer = MessageBoxTimeoutW(IntPtr.Zero,
            "Do you also want to delete Upshift's data?\n\n" +
            $"{dataDir}\n\n" +
            "It holds your settings, the list of games, downloaded releases and the files you added in Settings. " +
            "Choose No to keep it, so a later install carries on where you left off.\n\n" +
            "Either way, OptiScaler files and backups inside your game folders are left as they are.\n\n" +
            "This question closes by itself after 25 seconds and keeps your data.",
            "Uninstall Upshift", MbYesNo | MbIconQuestion | MbDefButton2 | MbTopmost | MbSetForeground, 0, 25_000);
        if (answer != IdYes) return;

        try { Directory.Delete(dataDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBoxW(IntPtr.Zero, $"Some of Upshift's data couldn't be deleted:\n\n{ex.Message}\n\nYou can delete {dataDir} yourself.",
                "Uninstall Upshift", MbOk | MbIconWarning | MbTopmost);
        }
    }

    private const uint MbOk = 0x0, MbYesNo = 0x4, MbIconError = 0x10, MbIconQuestion = 0x20, MbIconWarning = 0x30,
        MbDefButton2 = 0x100, MbSetForeground = 0x10000, MbTopmost = 0x40000;
    private const int IdYes = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    // Exported by user32 since Windows XP; returns 32000 when it times out.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxTimeoutW(IntPtr hWnd, string text, string caption, uint type, ushort languageId, uint milliseconds);
}
