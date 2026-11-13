using Upshift.Core.Catalog;
using Upshift.Core.Components;
using Upshift.Core.Install;
using Upshift.Core.Models;

namespace Upshift.App.Services;

/// <summary>
/// ANR frame generation (experimental, Settings > Experimental): per game add, remove ("switch back") and go back a version,
/// plus its own update check (at start-up and once a day). Downloads come only from apex-compute/ANR's GitHub releases,
/// only the newest release's addon zip, only when GitHub reports its SHA-256 digest and the file matches (the component's
/// RequireDigest). The previous version stays in the data folder (ComponentStore keeps two). A game is never changed while
/// it runs: its update waits until Upshift sees it closed.
/// </summary>
public static class AnrGames
{
    /// <summary>A message for the Library ("ANR updated to V.B15: …"), and whether it offers "Update now" (Ask first).</summary>
    public static event Action<string, bool>? Notice;

    public static CatalogComponent? Component => AppServices.Catalog.Addons.FirstOrDefault(c => c.Id == Anr.ComponentId);

    public static bool Enabled => AppServices.Settings.Current.AnrEnabled && Component is not null;

    /// <summary>The newest release seen on GitHub (from the last check), with its notes.</summary>
    public static ReleaseInfo? Newest { get; private set; }

    private static Timer? _timer;
    private static int _checking;

    /// <summary>Why ANR isn't offered for this game, or null when it is. Needs the setting, OptiScaler from Upshift, DirectX 12 or Vulkan, no anti-cheat.</summary>
    public static string? WhyNot(GameInfo game, InstallManifest? manifest)
    {
        if (!Enabled) return "Turn on ANR in Settings > Experimental.";
        if (game.HasAntiCheat) return "Not for games with anti-cheat.";
        if (manifest is not { Removed: false }) return "Install OptiScaler with Upshift first.";
        if (!Anr.ApiFits(game.Api)) return "Only for DirectX 12 and Vulkan games.";
        return null;
    }

    public static AnrRecord? InGame(GameInfo game) =>
        game.TargetDir is { } dir ? OptiScalerInstaller.ReadManifest(dir) is { Removed: false } m ? m.Anr : null : null;

    /// <summary>Games where Upshift added ANR.</summary>
    public static List<(GameInfo Game, AnrRecord Anr)> Games() =>
        AppServices.Library.Current
            .Select(g => (Game: g, Anr: InGame(g)))
            .Where(x => x.Anr is not null)
            .Select(x => (x.Game, x.Anr!))
            .ToList();

    /// <summary>Versions in the data folder, newest build first.</summary>
    public static List<CachedComponent> Downloaded() =>
        Component is { } c ? AppServices.Components.Downloaded(c).OrderByDescending(d => Anr.BuildNumber(d.Version)).ToList() : new();

    /// <summary>The downloaded version just below this one, for "Go back to previous ANR version".</summary>
    public static CachedComponent? PreviousOf(string version) =>
        Downloaded().FirstOrDefault(d => Anr.BuildNumber(d.Version) < Anr.BuildNumber(version));

    public static bool IsHeld(GameInfo game) => AppServices.Settings.Current.AnrHeldGames.Contains(game.Id);

    // ---------------- per game ----------------

    /// <summary>Adds the newest ANR to a game (downloading it the first time).</summary>
    public static Task<InstallResult> AddAsync(GameInfo game, IProgress<string>? progress) =>
        GameUpdates.RunExclusiveAsync(game, async () =>
        {
            if (WhyNot(game, game.TargetDir is { } d ? OptiScalerInstaller.ReadManifest(d) : null) is { } why) return Fail(why);
            var (cached, release) = await EnsureNewestAsync(progress);
            SetHeld(game, false);
            return await RunAddAsync(game, cached, release?.Published, progress);
        });

    /// <summary>"Switch back": the game goes back to plain OptiScaler, exactly.</summary>
    public static Task<InstallResult> RemoveAsync(GameInfo game, IProgress<string>? progress) =>
        GameUpdates.RunExclusiveAsync(game, async () =>
        {
            SetHeld(game, false);
            progress?.Report($"Taking ANR out of {game.Name}…");
            return await InstallRunner.RunAsync(Plan(game, InstallOperation.RemoveAnr));
        });

    /// <summary>The downloaded version before the game's one; automatic updates leave the game alone until "Use the newest".</summary>
    public static Task<InstallResult> GoBackAsync(GameInfo game, IProgress<string>? progress) =>
        GameUpdates.RunExclusiveAsync(game, async () =>
        {
            if (InGame(game) is not { } anr || PreviousOf(anr.Version) is not { } previous) return Fail("No earlier ANR version is in Upshift's folder.");
            var result = await RunAddAsync(game, previous, ReleaseDate(previous.Version), progress);
            if (result.Success) SetHeld(game, true);
            return result;
        });

    private static async Task<InstallResult> RunAddAsync(GameInfo game, CachedComponent cached, DateTimeOffset? released, IProgress<string>? progress)
    {
        if (Anr.PackageFolder(cached.Folder) is not { } package) return Fail("The downloaded ANR package has no provider folder.");
        progress?.Report($"Adding ANR {Anr.ShortVersion(cached.Version)} to {game.Name}…");
        var plan = Plan(game, InstallOperation.AddAnr);
        plan.SourceDir = package;
        plan.Version = cached.Version;
        plan.Released = released;
        return await InstallRunner.RunAsync(plan);
    }

    private static InstallPlan Plan(GameInfo game, InstallOperation op) => new()
    {
        Operation = op, GameId = game.Id, GameName = game.Name, TargetDir = game.TargetDir!, ComponentName = "ANR"
    };

    private static DateTimeOffset? ReleaseDate(string tag) =>
        Component is { } c ? AppServices.Components.GitHub.GetCachedReleases(c.Repo)?.Releases.FirstOrDefault(r => r.Tag == tag)?.Published : null;

    private static void SetHeld(GameInfo game, bool held)
    {
        var settings = AppServices.Settings.Current;
        if (held == settings.AnrHeldGames.Contains(game.Id)) return;
        if (held) settings.AnrHeldGames.Add(game.Id); else settings.AnrHeldGames.Remove(game.Id);
        try { AppServices.Settings.Save(settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---------------- around OptiScaler changes ----------------

    /// <summary>
    /// Before an OptiScaler update, undo or switch: ANR comes out first, so those work on plain OptiScaler (and Undo returns
    /// to exactly plain OptiScaler). Runs inside the caller's exclusive change. Returns what was there, or a failure.
    /// </summary>
    public static async Task<(AnrRecord? Was, InstallResult? Failed)> TakeOutAsync(GameInfo game, InstallManifest manifest, IProgress<string>? progress)
    {
        if (manifest.Anr is not { } anr) return (null, null);
        progress?.Report($"Taking ANR out of {game.Name} first…");
        var result = await InstallRunner.RunAsync(Plan(game, InstallOperation.RemoveAnr));
        return result.Success ? (anr, null) : (null, result);
    }

    /// <summary>After an OptiScaler update: ANR goes back in (the same version) when the new OptiScaler can load it.</summary>
    public static async Task<string?> PutBackAsync(GameInfo game, AnrRecord? was, IProgress<string>? progress)
    {
        if (was is null || Component is not { } c || AppServices.Components.TryGetCached(c, was.Version) is not { } cached) return null;
        var ini = Path.Combine(game.TargetDir!, "OptiScaler.ini");
        if (!File.Exists(ini) || !Anr.Supported(IniFile.Load(ini))) return "ANR was taken out: this OptiScaler can't load it.";
        var result = await RunAddAsync(game, cached, was.Released, progress);
        return result.Success ? null : $"ANR couldn't be put back: {result.Message}";
    }

    // ---------------- updates ----------------

    /// <summary>Starts the checks: one now, then every 10 minutes the daily check when due and any update that waited for a game to close.</summary>
    public static void Start()
    {
        _timer ??= new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(10));
    }

    private static bool _startupDone;

    private static async Task TickAsync()
    {
        if (!Enabled || Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            var settings = AppServices.Settings.Current;
            var due = !_startupDone || settings.AnrLastCheckUtc is not { } last || DateTime.UtcNow - last > TimeSpan.FromDays(1);
            _startupDone = true;
            if (due) await CheckAsync();
            else if (settings.AnrAutoUpdate) await ApplyToGamesAsync(Newest);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { CrashLog.Write(ex, "ANR update check"); }
        finally { Interlocked.Exchange(ref _checking, 0); }
    }

    /// <summary>Asks GitHub for the newest release; downloads it and updates games ("Update ANR automatically"), or offers it ("Ask first").</summary>
    public static async Task CheckAsync()
    {
        if (Component is not { } c) return;
        var list = await AppServices.Components.GitHub.GetReleasesAsync(c.Repo, CancellationToken.None);
        var settings = AppServices.Settings.Current;
        if (list.Problem is null)
        {
            settings.AnrLastCheckUtc = DateTime.UtcNow;
            try { AppServices.Settings.Save(settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Newest = NewestOf(list.Releases);
        if (Newest is null || Games().Count == 0 && Downloaded().Count == 0) return;
        var behind = Games().Any(g => !IsHeld(g.Game) && Anr.BuildNumber(g.Anr.Version) < Anr.BuildNumber(Newest.Tag));
        if (!behind) return;
        if (settings.AnrAutoUpdate) await ApplyToGamesAsync(Newest);
        else Notice?.Invoke($"ANR {Anr.ShortVersion(Newest.Tag)} is available: {Anr.NotesLine(Newest.Notes)}", true);
    }

    /// <summary>The newest regular release with an addon zip GitHub gives a checksum for.</summary>
    public static ReleaseInfo? NewestOf(IEnumerable<ReleaseInfo> releases) =>
        Component is { } c
            ? releases.Where(r => !r.Prerelease && ComponentStore.PickAsset(c, r) is { Sha256: not null })
                .OrderByDescending(r => Anr.BuildNumber(r.Tag)).FirstOrDefault()
            : null;

    private static async Task<(CachedComponent Cached, ReleaseInfo? Release)> EnsureNewestAsync(IProgress<string>? progress)
    {
        var c = Component ?? throw new ComponentDownloadException("This catalog has no ANR entry.");
        if (Newest is null)
        {
            var list = await AppServices.Components.GitHub.GetReleasesAsync(c.Repo, CancellationToken.None);
            Newest = NewestOf(list.Releases);
        }
        if (Newest is null) throw new ComponentDownloadException("No ANR release with a GitHub checksum was found.");
        var inUse = Games().Select(g => g.Anr.Version).Distinct().ToList();
        var cached = await AppServices.Components.EnsureAsync(c, Newest.Tag, progress, CancellationToken.None, keep: inUse);
        return (cached, Newest);
    }

    /// <summary>"Update now" from the Ask-first notice.</summary>
    public static Task UpdateNowAsync() => ApplyToGamesAsync(Newest);

    /// <summary>Updates every game on an older ANR (not held back, not running) to the release; running games wait for the next tick.</summary>
    private static async Task ApplyToGamesAsync(ReleaseInfo? release)
    {
        if (release is null) return;
        var games = Games().Where(g => !IsHeld(g.Game) && Anr.BuildNumber(g.Anr.Version) < Anr.BuildNumber(release.Tag)).ToList();
        if (games.Count == 0) return;
        var (cached, _) = await EnsureNewestAsync(null);
        var updated = new List<string>();
        var waiting = new List<string>();
        foreach (var (game, _) in games)
        {
            if (OptiScalerInstaller.RunningFrom(game.TargetDir!).Count > 0) { waiting.Add(game.Name); continue; }
            var result = await GameUpdates.RunExclusiveAsync(game, () => RunAddAsync(game, cached, release.Published, null));
            if (result.Success) updated.Add(game.Name);
        }
        if (updated.Count > 0)
            Notice?.Invoke($"ANR updated to {Anr.ShortVersion(release.Tag)}: {Anr.NotesLine(release.Notes)}" +
                           (waiting.Count > 0 ? $" ({string.Join(", ", waiting)} will be updated after the game closes.)" : ""), false);
    }

    private static InstallResult Fail(string message) => new() { Message = message };
}
