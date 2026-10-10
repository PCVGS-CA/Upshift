using Upshift.Core.Install;

namespace Upshift.App.Services;

/// <summary>
/// HelixSR's setup, run by the app, not by a page: it keeps running when the user goes to another page, and any Updates
/// page shown later reattaches to it through State and Changed. The end of the setup process is always noticed
/// (HelixSrSetup watches the process itself) and its result kept here until the next setup. Changed is raised on the UI
/// thread. Nothing here throws.
/// </summary>
public static class HelixSrSetupSession
{
    private static readonly HttpClient Http = CreateHttp();
    private static Microsoft.UI.Dispatching.DispatcherQueue? _ui;
    private static CancellationTokenSource? _cancel;

    public static bool Running { get; private set; }
    /// <summary>The current step in plain words ("Building the network, step 5 of 5 (shaders; …)").</summary>
    public static string Step { get; private set; } = "";
    public static double Fraction { get; private set; }
    public static DateTime? StartedUtc { get; private set; }
    /// <summary>The last setup's result (null until one has finished in this session).</summary>
    public static HelixSrSetupResult? LastResult { get; private set; }
    public static TimeSpan? LastDuration { get; private set; }

    public static event Action? Changed;

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        return http;
    }

    /// <summary>Starts the setup (after the notice was accepted). Returns false when one is already running.</summary>
    public static bool Start(Core.Catalog.HelixSrRelease release)
    {
        if (Running) return false;
        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _cancel = new CancellationTokenSource();
        Running = true;
        StartedUtc = DateTime.UtcNow;
        Step = "Starting";
        Fraction = 0;
        LastResult = null;
        Raise();
        _ = RunAsync(release, _cancel.Token);
        return true;
    }

    public static void Cancel() => _cancel?.Cancel();

    private static async Task RunAsync(Core.Catalog.HelixSrRelease release, CancellationToken ct)
    {
        HelixSrSetupResult result;
        try
        {
            var progress = new Progress<HelixSrProgress>(p =>
            {
                Step = p.Step;
                Fraction = p.Fraction;
                Raise();
            });
            result = await Task.Run(() => HelixSrSetup.RunAsync(release, AppServices.DataDir, Http, progress, ct));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = new HelixSrSetupResult(false, $"The HelixSR setup failed unexpectedly: {ex.Message}");
        }
        Post(() =>
        {
            LastDuration = StartedUtc is { } s ? DateTime.UtcNow - s : null;
            LastResult = result;
            Running = false;
            Step = "";
            _cancel?.Dispose();
            _cancel = null;
            Raise();
            AppServices.RaiseEffectiveGpuChanged();   // the games' option lists follow the new state
        });
    }

    private static void Post(Action action)
    {
        if (_ui is { } ui && !ui.HasThreadAccess) ui.TryEnqueue(() => action());
        else action();
    }

    private static void Raise() => Post(() =>
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { CrashLog.Write(ex, "HelixSR setup display"); }
    });
}
