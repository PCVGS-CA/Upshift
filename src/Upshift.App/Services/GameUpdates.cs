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

    /// <summary>The catalog component installed in a game: regular OptiScaler, or a DLSS 5 build.</summary>
    public static CatalogComponent ComponentFor(string? componentId) =>
        AppServices.Catalog.Components.FirstOrDefault(c => c.Id == componentId) ?? AppServices.OptiScaler;

    /// <summary>
    /// The release to update an install to: the user's channel (Stable or Beta) for the build that's installed
    /// (OptiScaler, OptiScaler DLSSNR or AMD-NR), when it's later than the installed version and has a file to
    /// download. Null when there's nothing newer.
    /// </summary>
    public static string? TargetFor(string? installedVersion, string? componentId = null)
    {
        var component = ComponentFor(componentId);
        if (AppServices.Updates.Target(component) is not { } target || !ComponentStore.IsDownloadable(component, target)) return null;
        return AppServices.Updates.IsNewer(component, target.Tag, installedVersion) ? target.Tag : null;
    }

    /// <summary>Games that can be updated, whichever build they use. Games with anti-cheat never are.</summary>
    public static List<GameUpdateCandidate> Candidates() =>
        AppServices.Library.Current
            .Where(g => !g.HasAntiCheat && g.TargetDir is not null)
            .Select(g => (Game: g, Manifest: OptiScalerInstaller.ReadManifest(g.TargetDir!)))
            .Where(x => x.Manifest is { Removed: false } m && ComponentFor(m.ComponentId).Installable)
            .Select(x => TargetFor(x.Manifest!.Version, x.Manifest.ComponentId) is { } target
                ? new GameUpdateCandidate(x.Game, x.Manifest!.Version ?? "unknown", target) : null)
            .OfType<GameUpdateCandidate>()
            .OrderBy(c => c.Game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// AMD-NR's danielblnc runtime for a release (the component grouped with it), downloaded when needed: the one
    /// with the same tag, or the catalog's pinned one when that release has none. Null for other builds.
    /// </summary>
    private static async Task<CachedComponent?> RuntimeForAsync(CatalogComponent component, string version, IProgress<string>? progress)
    {
        var runtime = AppServices.Catalog.Components.FirstOrDefault(c => c.GroupWith == component.Id && c.Kind == "runtime");
        if (runtime is null) return null;
        try { return await AppServices.Components.EnsureAsync(runtime, version, progress, CancellationToken.None); }
        catch (ComponentDownloadException) when (runtime.PinnedVersion is { } pinned && pinned != version)
        {
            return await AppServices.Components.EnsureAsync(runtime, pinned, progress, CancellationToken.None);
        }
    }

    /// <summary>Downloads the release (and the installed one, whose OptiScaler.ini gives the old defaults), then updates.</summary>
    public static Task<InstallResult> UpdateAsync(GameInfo game, string version, IProgress<string>? progress) =>
        RunAsync(game, progress, async manifest =>
        {
            if (game.HasAntiCheat) return Fail("Installs are blocked for games with anti-cheat.");
            var component = ComponentFor(manifest.ComponentId);
            var installed = manifest.Version;
            var target = await AppServices.Components.EnsureAsync(component, version, progress, CancellationToken.None,
                keep: installed is null ? null : new[] { installed });

            CachedComponent? old = null;
            if (installed is not null)
            {
                try { old = await AppServices.Components.EnsureAsync(component, installed, progress, CancellationToken.None, keep: new[] { version }); }
                catch (ComponentDownloadException) { /* the ini merge then carries over only this app's recorded changes */ }
            }
            var runtime = await RuntimeForAsync(component, version, progress);

            progress?.Report($"Updating {component.Name} in {game.Name} to {version}…");
            return await InstallRunner.RunAsync(new InstallPlan
            {
                Operation = InstallOperation.Update,
                GameId = game.Id,
                GameName = game.Name,
                TargetDir = game.TargetDir!,
                SourceDir = target.Folder,
                OldSourceDir = old?.Folder,
                ExtraSourceDir = runtime?.Folder,
                ComponentId = component.Id,
                ComponentName = component.Name,
                Version = version
            });
        });

    /// <summary>
    /// Switches the game's regular OptiScaler to the DLSS 5 build for this card (DLSSNR for NVIDIA, AMD-NR for AMD):
    /// downloads it (and AMD-NR's runtime), keeps the user's OptiScaler.ini settings like an update, copies in the
    /// user's DLSS 5 file for NVIDIA, sets the runtime for AMD, and saves an exact return point for "Switch back".
    /// </summary>
    public static Task<InstallResult> SwitchToDlss5Async(GameInfo game, NeuralBackend backend, NeuralRuntime? runtime, IProgress<string>? progress) =>
        RunAsync(game, progress, async manifest =>
        {
            if (game.HasAntiCheat) return Fail("Installs are blocked for games with anti-cheat.");
            if (AppServices.PretendGpu is not null) return Fail("A pretend graphics card is set in Settings, so nothing is installed. Turn it off first.");
            if (manifest.Switch is not null) return Fail("This game already uses a DLSS 5 build.");
            var fork = ComponentFor(backend.ComponentId);
            if (fork.PinnedVersion is not { } version) return Fail($"The catalog has no tested version of {fork.Name}.");

            string? userFile = null;
            if (backend.NeedsNvidiaDll)
            {
                userFile = AppServices.UserFiles.Get(Core.UserFiles.UserFileKind.DlssNr)?.Path;
                if (userFile is null) return Fail("Add your DLSS 5 file (nvngx_dlssnr.dll) in Settings > Optional files you supply first.");
            }

            var target = await AppServices.Components.EnsureAsync(fork, version, progress, CancellationToken.None);
            CachedComponent? old = null;
            if (manifest.Version is { } installed)
            {
                try { old = await AppServices.Components.EnsureAsync(ComponentFor(manifest.ComponentId), installed, progress, CancellationToken.None); }
                catch (ComponentDownloadException) { }
            }
            var runtimeFiles = await RuntimeForAsync(fork, version, progress);

            progress?.Report($"Switching {game.Name} to {fork.Name}…");
            return await InstallRunner.RunAsync(new InstallPlan
            {
                Operation = InstallOperation.SwitchBuild,
                GameId = game.Id,
                GameName = game.Name,
                TargetDir = game.TargetDir!,
                SourceDir = target.Folder,
                OldSourceDir = old?.Folder,
                ExtraSourceDir = runtimeFiles?.Folder,
                ComponentId = fork.Id,
                ComponentName = fork.Name,
                Version = version,
                AddFileFrom = userFile,
                AddFileAs = userFile is null ? null : Dlss5.NvidiaFileName,
                // AMD-NR asks for its runtime on first launch unless it's set; preselected for the card.
                IniSettings = runtime?.IniValue is { } value ? new List<IniSetting> { new(Dlss5.Section, "NrBackend", value) } : new()
            });
        });

    /// <summary>Returns the game to the regular OptiScaler it had before the switch, keeping settings changed since.</summary>
    public static Task<InstallResult> SwitchBackAsync(GameInfo game, IProgress<string>? progress) =>
        RunAsync(game, progress, manifest =>
        {
            if (manifest.Switch is null) return Task.FromResult(Fail("This game isn't using a DLSS 5 build."));
            progress?.Report($"Switching {game.Name} back to OptiScaler {manifest.Switch.FromVersion}…");
            return InstallRunner.RunAsync(new InstallPlan
            {
                Operation = InstallOperation.SwitchBack,
                GameId = game.Id,
                GameName = game.Name,
                TargetDir = game.TargetDir!
            });
        });

    /// <summary>Games whose folder has the user's DLSS 5 file from Upshift (a DLSS 5 build switched in).</summary>
    public static List<GameInfo> GamesUsingDlss5File() =>
        AppServices.Library.Current
            .Where(g => g.TargetDir is not null && OptiScalerInstaller.ReadManifest(g.TargetDir) is { Removed: false } m
                        && m.Added.Concat(m.Replaced).Any(f => f.Path.Equals(Dlss5.NvidiaFileName, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

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
            var source = await AppServices.Components.EnsureAsync(ComponentFor(manifest.ComponentId), version, progress, CancellationToken.None);
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

    private static Task<InstallResult> RunAsync(GameInfo game, IProgress<string>? progress, Func<InstallManifest, Task<InstallResult>> work)
    {
        if (game.TargetDir is null || OptiScalerInstaller.ReadManifest(game.TargetDir) is not { Removed: false } manifest)
            return Task.FromResult(Fail("OptiScaler isn't installed in this game by this app."));
        return RunExclusiveAsync(game, () => work(manifest));
    }

    /// <summary>
    /// Runs one change to a game folder, never two at once: Play is off for the game meanwhile, and the game is
    /// looked at again afterwards. Download problems come back as a failed result.
    /// </summary>
    public static async Task<InstallResult> RunExclusiveAsync(GameInfo game, Func<Task<InstallResult>> work)
    {
        if (!await Gate.WaitAsync(0)) return Fail("Another install or update is still running.");
        BusyGameId = game.Id;
        BusyChanged?.Invoke();
        try
        {
            InstallResult result;
            try { result = await work(); }
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
    /// At start-up: the online catalog (Upshift's on GitHub, or the "catalogUrl" address), used from the next start when
    /// it's valid and newer; then, when automatic checks are on and the last one was 6 or more hours ago, a release check
    /// and downloads for components set to "Keep updated". Offline just means nothing new (the built-in catalog stays).
    /// </summary>
    public static async Task StartupAsync()
    {
        try
        {
            var settings = AppServices.Settings.Current;
            var status = await RemoteCatalog.RefreshAsync(AppServices.DataDir, settings.EffectiveCatalogUrl, AppServices.BuiltInCatalog, AppInfo.UserAgent, CancellationToken.None);
            // Upshift's own catalog only needs a mention when it's actually in use; a custom address always gets its status.
            if (!string.IsNullOrWhiteSpace(settings.CatalogUrl) || AppServices.UsingRemoteCatalog) AppServices.CatalogStatus = status;

            if (!AppServices.Updates.AutoCheckDue) return;
            await AppServices.Updates.CheckAsync(CancellationToken.None);
            await AppServices.Updates.DownloadKeptUpdatedAsync(CancellationToken.None);
        }
        catch (Exception) { /* update checks are never allowed to disturb the app */ }
    }
}
