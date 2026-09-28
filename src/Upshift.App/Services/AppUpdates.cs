using System.Net;
using Velopack;
using Velopack.Sources;

namespace Upshift.App.Services;

public enum AppUpdateState { Idle, NotInstalled, Checking, UpToDate, Available, Downloading, Failed }

/// <summary>
/// Updates for Upshift itself, through Velopack, from the GitHub releases at AppInfo.RepoUrl. Checking only reads the
/// release list; nothing is downloaded until the user chooses "Restart to update".
/// Changed is raised on whichever thread did the work.
/// </summary>
public static class AppUpdates
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static UpdateManager? _manager;
    private static UpdateInfo? _update;

    public static AppUpdateState State { get; private set; } = AppUpdateState.Idle;

    /// <summary>What to show under "Check for app updates".</summary>
    public static string Message { get; private set; } = "";

    /// <summary>The newer version found by the last check, e.g. "1.0.1" (null when there's none).</summary>
    public static string? AvailableVersion => _update?.TargetFullRelease.Version.ToString();

    public static event Action? Changed;

    private static UpdateManager Manager => _manager ??= new UpdateManager(new GithubSource(AppInfo.RepoUrl, null, false));

    /// <summary>
    /// False for a build run straight from Visual Studio or an unzipped build folder. The installed app and the
    /// portable zip (both made by Velopack) can update themselves.
    /// </summary>
    public static bool CanUpdate
    {
        get
        {
            try { return Manager.IsInstalled; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>At start-up, when "Check for updates automatically" is on.</summary>
    public static async Task StartupAsync()
    {
        if (!AppServices.Settings.Current.AutoCheckUpdates || !CanUpdate) return;
        await CheckAsync();
    }

    public static async Task CheckAsync()
    {
        if (!await Gate.WaitAsync(0)) return;
        try
        {
            if (!CanUpdate)
            {
                Set(AppUpdateState.NotInstalled,
                    "This copy of Upshift can't update itself, because it wasn't installed with the Upshift installer. " +
                    "New versions are on GitHub.");
                return;
            }
            Set(AppUpdateState.Checking, "Checking for app updates…");
            _update = await Manager.CheckForUpdatesAsync();
            if (_update is null)
                Set(AppUpdateState.UpToDate, $"You have the latest version, {AppInfo.Version}. Checked {DateTime.Now:HH:mm}.");
            else
                Set(AppUpdateState.Available, $"Upshift {AvailableVersion} is available. You have {AppInfo.Version}.");
        }
        catch (Exception ex)
        {
            _update = null;
            Set(AppUpdateState.Failed, $"Couldn't check for app updates. {Describe(ex)}");
        }
        finally { Gate.Release(); }
    }

    /// <summary>Downloads the update found by the last check, then closes Upshift, applies it and starts it again.</summary>
    public static async Task DownloadAndRestartAsync()
    {
        if (_update is not { } update) return;
        if (!await Gate.WaitAsync(0)) return;
        var version = AvailableVersion;
        try
        {
            if (IsWorkRunning())
            {
                Set(AppUpdateState.Available, $"Upshift {version} is available. Wait for the install or update in progress to finish, then restart.");
                return;
            }
            Set(AppUpdateState.Downloading, $"Downloading Upshift {version}…");
            await Manager.DownloadUpdatesAsync(update, percent => Set(AppUpdateState.Downloading, $"Downloading Upshift {version}… {percent}%"));

            // An install may have been started while the download ran.
            if (IsWorkRunning())
            {
                Set(AppUpdateState.Available, $"Upshift {version} is downloaded. Wait for the install or update in progress to finish, then restart.");
                return;
            }
            Manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
        }
        catch (Exception ex)
        {
            Set(AppUpdateState.Available, $"Upshift {version} couldn't be downloaded. {Describe(ex)}");
        }
        finally { Gate.Release(); }
    }

    private static bool IsWorkRunning() => GameUpdates.IsBusy || InstallRunner.IsRunning;

    private static void Set(AppUpdateState state, string message)
    {
        State = state;
        Message = message;
        Changed?.Invoke();
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "GitHub didn't find Upshift's releases.",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } =>
            "GitHub's limit on requests was reached. Try again in an hour.",
        HttpRequestException or TaskCanceledException => "Check your internet connection and try again.",
        _ => ex.Message
    };
}
