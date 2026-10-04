using System.Net;
using Velopack;
using Velopack.Sources;

namespace Upshift.App.Services;

public enum AppUpdateState { Idle, NotInstalled, Checking, UpToDate, Available, Downloading, Failed }

/// <summary>
/// Updates for Upshift itself, through Velopack, from the GitHub releases at AppInfo.RepoUrl. Checking only reads the
/// release list; nothing is downloaded until the user chooses "Restart to update". A copy that wasn't installed with
/// the installer (a build from Visual Studio, say) can't update itself, but still learns the latest version from
/// GitHub's release list, so the Updates page can show it.
/// Changed is raised on whichever thread did the work.
/// </summary>
public static class AppUpdates
{
    private const string Repo = "PCVGS-CA/Upshift";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static UpdateManager? _manager;
    private static UpdateInfo? _update;

    public static AppUpdateState State { get; private set; } = AppUpdateState.Idle;

    /// <summary>The last check's result in a sentence, for the Upshift row on the Updates page.</summary>
    public static string Message { get; private set; } = "";

    /// <summary>The newer version found by the last check, e.g. "1.0.3" (null when there's none).</summary>
    public static string? AvailableVersion => _update?.TargetFullRelease.Version.ToString();

    /// <summary>The newest released version the last check saw (null until a check has run).</summary>
    public static string? LatestVersion { get; private set; }

    /// <summary>A newer Upshift exists (for the marker on Updates in the sidebar).</summary>
    public static bool NewerExists => State is AppUpdateState.Available or AppUpdateState.Downloading
        || (State == AppUpdateState.NotInstalled && LatestVersion is { } latest && IsNewer(latest, AppInfo.Version));

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
        if (!AppServices.Settings.Current.AutoCheckUpdates) return;
        await CheckAsync();
    }

    /// <summary>Checks for a newer Upshift (also run by "Check now" on the Updates page).</summary>
    public static async Task CheckAsync()
    {
        if (!await Gate.WaitAsync(0)) return;
        try
        {
            Set(AppUpdateState.Checking, "Checking for a newer Upshift…");
            if (!CanUpdate)
            {
                // Only the version is read here; this copy can't install it.
                var list = await AppServices.Components.GitHub.GetReleasesAsync(Repo, CancellationToken.None);
                LatestVersion = list.Releases.Where(r => !r.Prerelease).Select(r => r.Tag.TrimStart('v', 'V')).FirstOrDefault() ?? LatestVersion;
                Set(AppUpdateState.NotInstalled,
                    (LatestVersion is { } l && IsNewer(l, AppInfo.Version) ? $"Upshift {l} is out. " : "") +
                    "This copy can't update itself because it wasn't installed with the Upshift installer. New versions are on GitHub.");
                return;
            }
            _update = await Manager.CheckForUpdatesAsync();
            if (_update is null)
            {
                LatestVersion = AppInfo.Version;
                Set(AppUpdateState.UpToDate, $"You have the latest version. Checked {DateTime.Now:HH:mm}.");
            }
            else
            {
                LatestVersion = AvailableVersion;
                Set(AppUpdateState.Available, $"Upshift {AvailableVersion} is available. Update downloads it, then restarts Upshift.");
            }
        }
        catch (Exception ex)
        {
            _update = null;
            Set(AppUpdateState.Failed, $"Couldn't check for a newer Upshift. {Describe(ex)}");
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

    /// <summary>"1.0.3" is newer than "1.0.2"; anything that isn't a plain version counts as not newer.</summary>
    public static bool IsNewer(string candidate, string current) =>
        System.Version.TryParse(candidate.Split('-')[0], out var a) && System.Version.TryParse(current.Split('-')[0], out var b) && a > b;

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
