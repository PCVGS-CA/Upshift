using Microsoft.UI.Xaml;

namespace Upshift.App;

public partial class App : Application
{
    public static Window? MainAppWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        // Exceptions XAML raises on the UI thread (event handlers, bindings, layout) are logged before they close the app.
        UnhandledException += (_, e) => Services.CrashLog.Write(e.Exception, "Unhandled exception in the UI (Upshift closed)");
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Started elevated by the main window for one install or uninstall: do it, write the result, and quit (no window).
        var commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Contains("--apply"))
        {
            Environment.ExitCode = Services.InstallRunner.RunElevated(commandLine);
            Exit();
            return;
        }

        // Started elevated by the main window for one measurement: wait for the hotkey's signal, run PresentMon once, quit.
        if (commandLine.Contains("--measure"))
        {
            string? Arg(string name) => Array.IndexOf(commandLine, name) is var i and >= 0 && i + 1 < commandLine.Length ? commandLine[i + 1] : null;
            var job = Arg("--measure");
            var hash = Arg("--sha256");
            // The job sits in <data>\measure\<id>\job.json.
            var dataDir = job is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(job))));
            Environment.ExitCode = job is null || hash is null || dataDir is null ? 2 : Core.Measure.PresentMonRunner.RunHelper(job, hash, dataDir);
            Exit();
            return;
        }

        try
        {
            // Data from before the rename (%LocalAppData%\PCVGS\UpscalerManager) moves to %LocalAppData%\Upshift once.
            // This must run before AppServices is touched, since its services are created from the data folder.
            DataFolder.Path = Core.Services.DataMigration.Run();

            // Upshift.exe --crash-test: fails on purpose here, to check that a start-up crash reaches crash.log.
            if (commandLine.Contains("--crash-test"))
                throw new InvalidOperationException("Crash test: Upshift was started with --crash-test.");

            MainAppWindow = new MainWindow();
            MainAppWindow.Activate();

            // An installed copy repairs its own shortcuts if they point elsewhere or lost the icon (in the background).
            _ = Task.Run(() => Services.Shortcuts.Ensure(createMissing: false));
        }
        catch (Exception ex)
        {
            Services.CrashLog.Write(ex, "Start-up failed while opening the main window");
            throw;
        }
    }
}
