using Upshift.Core.Catalog;
using Upshift.Core.Components;
using Upshift.Core.Install;
using Upshift.Core.Models;

namespace Upshift.App.Services;

/// <summary>A game with an OptiScaler from this app that a newer release on the user's channel can replace.</summary>
public sealed record GameUpdateCandidate(GameInfo Game, string Installed, string Target);

/// <summary>
/// Updating, undoing and repairing OptiScaler in games, shared by the Library and the Updates page, plus the
/// start-up update check. Every change goes through InstallRunner (UAC when the folder needs it), one at a time.
/// </summary>
public static class GameUpdates
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Raised on the calling thread after a game folder changed, so pages can rebuild their cards.</summary>
    public static event Action? LibraryChanged;

    public static bool IsBusy => Gate.CurrentCount == 0;

    /// <summary>The game being updated, undone or repaired right now (Play is off for it meanwhile).</summary>
    public static string? BusyGameId { get; private set; }

    /// <summary>Raised on the calling thread when BusyGameId changes.</summary>
    public static event Action? BusyChanged;

    /// <summary>
    /// The release to update an install to: the user's OptiScaler channel (Stable or Beta), when it's later than the
    /// installed version and has a file to download. Null when there's nothing newer.
    /// </summary>
    public static string? TargetFor(string? installedVersion)
    {
        var opti = AppServices.OptiScaler;
        if (AppServices.Updates.Target(opti) is not { } target || !ComponentStore.IsDownloadable(opti, target)) return null;
        return AppServices.Updates.IsNewer(opti, target.Tag, installedVersion) ? target.Tag : null;
    }

    /// <summary>Games that can be updated. Games with anti-cheat never are.</summary>
    public static List<GameUpdateCandidate> Candidates() =>
        AppServices.Library.Current
            .Where(g => !g.HasAntiCheat && g.TargetDir is not null)
            .Select(g => (Game: g, Manifest: OptiScalerInstaller.ReadManifest(g.TargetDir!)))
            .Where(x => x.Manifest is { Removed: false, ComponentId: "optiscaler" })
            .Select(x => TargetFor(x.Manifest!.Version) is { } target ? new GameUpdateCandidate(x.Game, x.Manifest!.Version ?? "unknown", target) : null)
            .OfType<GameUpdateCandidate>()
            .OrderBy(c => c.Game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Downloads the release (and the installed one, whose OptiScaler.ini gives the old defaults), then updates.</summary>
    public static Task<InstallResult> UpdateAsync(GameInfo game, string version, IProgress<string>? progress) =>
        RunAsync(game, progress, async manifest =>
        {
            if (game.HasAntiCheat) return Fail("Installs are blocked for games with anti-cheat.");
            var opti = AppServices.OptiScaler;
            var installed = manifest.Version;
            var target = await AppServices.Components.EnsureAsync(opti, version, progress, CancellationToken.None,
                keep: installed is null ? null : new[] { installed });

            CachedComponent? old = null;
            if (installed is not null)
            {
                try { old = await AppServices.Components.EnsureAsync(opti, installed, progress, CancellationToken.None, keep: new[] { version }); }
                catch (ComponentDownloadException) { /* the ini merge then carries over only this app's recorded changes */ }
            }

            progress?.Report($"Updating OptiScaler in {game.Name} to {version}…");
            return await InstallRunner.RunAsync(new InstallPlan
            {
                Operation = InstallOperation.Update,
                GameId = game.Id,
                GameName = game.Name,
                TargetDir = game.TargetDir!,
                SourceDir = target.Folder,
                OldSourceDir = old?.Folder,
                ComponentId = opti.Id,
                Version = version
            });
        });

    public static Task<InstallResult> UndoAsync(GameInfo game, IProgress<string>? progress) =>
        RunAsync(game, progress, manifest =>
        {
            progress?.Report($"Putting back OptiScaler {manifest.LastUpdate?.FromVersion} in {game.Name}…");
            return InstallRunner.RunAsync(new InstallPlan
            {
                Operation = InstallOperation.UndoUpdate,
                GameId = game.Id,
                GameName = game.Name,
                TargetDir = game.TargetDir!
            });
        });

    /// <summary>Reinstalls the files of the installed version (downloading it again if needed), keeping the settings.</summary>
    public static Task<InstallResult> RepairAsync(GameInfo game, IProgress<string>? progress) =>
        RunAsync(game, progress, async manifest =>
        {
            if (game.HasAntiCheat) return Fail("Installs are blocked for games with anti-cheat.");
            if (manifest.Version is not { } version) return Fail("The install record doesn't say which version is installed.");
            var source = await AppServices.Components.EnsureAsync(AppServices.OptiScaler, version, progress, CancellationToken.None);
            progress?.Report($"Repairing OptiScaler in {game.Name}…");
            return await InstallRunner.RunAsync(new InstallPlan
            {
                Operation = InstallOperation.Repair,
                GameId = game.Id,
                GameName = game.Name,
                TargetDir = game.TargetDir!,
                SourceDir = source.Folder,
                ComponentId = manifest.ComponentId,
                Version = version
            });
        });

    private static async Task<InstallResult> RunAsync(GameInfo game, IProgress<string>? progress, Func<InstallManifest, Task<InstallResult>> work)
    {
        if (game.TargetDir is null || OptiScalerInstaller.ReadManifest(game.TargetDir) is not { Removed: false } manifest)
            return Fail("OptiScaler isn't installed in this game by this app.");
        if (!await Gate.WaitAsync(0)) return Fail("Another install or update is still running.");
        BusyGameId = game.Id;
        BusyChanged?.Invoke();
        try
        {
            InstallResult result;
            try { result = await work(manifest); }
            catch (ComponentDownloadException ex) { result = Fail(ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                result = Fail(ex.Message);
            }
            await AppServices.Library.ReanalyzeAsync(game.Id);
            LibraryChanged?.Invoke();
            return result;
        }
        finally
        {
            BusyGameId = null;
            Gate.Release();
            BusyChanged?.Invoke();
        }
    }

    private static InstallResult Fail(string message) => new() { Message = message };

    // ---------------- start-up ----------------

    /// <summary>
    /// At start-up: the "catalogUrl" catalog (if set), then, when automatic checks are on and the last one was 6 or more
    /// hours ago, a release check and downloads for components set to "Keep updated". Offline just means nothing new.
    /// </summary>
    public static async Task StartupAsync()
    {
        try
        {
            var url = AppServices.Settings.Current.CatalogUrl;
            if (!string.IsNullOrWhiteSpace(url))
                AppServices.CatalogStatus = await RemoteCatalog.RefreshAsync(AppServices.DataDir, url, AppServices.BuiltInCatalog, AppInfo.UserAgent, CancellationToken.None);

            if (!AppServices.Updates.AutoCheckDue) return;
            await AppServices.Updates.CheckAsync(CancellationToken.None);
            await AppServices.Updates.DownloadKeptUpdatedAsync(CancellationToken.None);
        }
        catch (Exception) { /* update checks are never allowed to disturb the app */ }
    }
}
