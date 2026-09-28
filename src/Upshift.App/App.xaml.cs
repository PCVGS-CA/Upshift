using Microsoft.UI.Xaml;

namespace Upshift.App;

public partial class App : Application
{
    public static Window? MainAppWindow { get; private set; }

    public App()
    {
        InitializeComponent();
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

        // Data from before the rename (%LocalAppData%\PCVGS\UpscalerManager) moves to %LocalAppData%\Upshift once.
        // This must run before AppServices is touched, since its services are created from the data folder.
        DataFolder.Path = Core.Services.DataMigration.Run();

        MainAppWindow = new MainWindow();
        MainAppWindow.Activate();
    }
}
