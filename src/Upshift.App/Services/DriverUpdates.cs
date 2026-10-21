using Upshift.Core.Hardware;

namespace Upshift.App.Services;

/// <summary>
/// The sidebar's "Newer driver available" line: one lookup a day against NVIDIA's, AMD's or Intel's own pages. A failed
/// lookup shows nothing (it's written to logs\driver-check.log). Upshift never downloads or installs drivers.
/// </summary>
public static class DriverUpdates
{
    private static readonly HttpClient Http = CreateHttp();

    public static NewerDriver? Newer { get; private set; }

    public static event Action? Changed;

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // AMD's and Intel's sites answer browsers; Upshift says what it is too.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Upshift/" + AppInfo.Version.Split('+')[0]);
        return http;
    }

    public static async Task StartupAsync()
    {
        try
        {
            var gpu = await AppServices.GetGpuAsync();
            var installed = DriverDetails.PublishedVersion(gpu, DriverDetails.Read(gpu));
            Newer = await DriverCheck.RunAsync(gpu, installed, AppServices.Catalog.DriverChecks, AppServices.DataDir, Http, DateTime.UtcNow);
            if (Newer is not null) Changed?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DriverCheck.Log(AppServices.DataDir, $"start-up check failed: {ex.Message}");
        }
    }

    /// <summary>This PC, as Measure's shared results describe it, plus the driver's date and the monitors.</summary>
    public static Core.Services.PcInfo ThisPc()
    {
        var gpu = AppServices.Gpu;
        var details = DriverDetails.Read(gpu);
        return Core.Services.PcInfo.Read(gpu?.Name, DriverDetails.PublishedVersion(gpu, details)) with
        {
            DriverDate = details.Date,
            Displays = Helpers.Display.All().Select(d => d.Label).ToList()
        };
    }
}
