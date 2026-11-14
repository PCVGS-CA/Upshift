using Upshift.Core.Catalog;
using Upshift.Core.Components;
using Upshift.Core.Install;
using Upshift.Core.Models;

namespace Upshift.App.Services;

/// <summary>
/// OptiScaler test builds per game (Settings > OptiScaler > "Allow OptiScaler test builds"). Only the official nightlies from
/// optiscaler/OptiScaler-nightly, only when GitHub reports the asset's SHA-256 and it matches (the catalog's requireDigest).
/// Stable (the catalog's pinned 0.9.4) stays the default and is never pruned from the data folder. Moving a game either way
/// is an OptiScaler update (settings in both versions kept, new ones at that version's defaults, an undo copy); going back to
/// Stable right after a move is an exact Undo. Checks at start-up and once a day; "Ask first" (default) or automatic, never
/// while the game runs. A game taken back to an earlier test build is left alone until "Use the newest test build".
/// </summary>
public static class TestBuilds
{
    /// <summary>A message for the Library ("OptiScaler test build updated to 13 Nov: …"), and whether it offers "Update now".</summary>
    public static event Action<string, bool>? Notice;

    public static CatalogComponent? Component => GameUpdates.TestBuild;
    public static bool Allowed => AppServices.Settings.Current.OptiScalerTestBuilds && Component is not null;
    public static string StableVersion => AppServices.OptiScaler.PinnedVersion ?? "";

    public const string NoticeText =
        "Test builds are the OptiScaler team's daily builds (from github.com/optiscaler/OptiScaler-nightly). They have newer " +
        "features, like ANR frame generation and the FSR 4 model choice, but may have bugs. Use Stable for games you just want " +
        "to play. You choose per game, on its Upscaling tab; Stable stays available for every game.";

    /// <summary>Why this game can't use a test build, or null when it can.</summary>
    public static string? WhyNot(GameInfo game, InstallManifest? manifest)
    {
        if (!Allowed) return "Turn on \"Allow OptiScaler test builds\" in Settings > OptiScaler.";
        if (game.HasAntiCheat) return "Not for games with anti-cheat.";
        if (manifest is not { Removed: false }) return "Install OptiScaler with Upshift first.";
        if (manifest.Switch is not null) return "Not available while the DLSS 5 build is on in this game. Switch back to regular OptiScaler first.";
        return null;
    }

    public static bool OnTestBuild(InstallManifest? manifest) => manifest is { Removed: false } m && Component is { } c && m.ComponentId == c.Id;

    /// <summary>"10.0.0-pre1 · test build · 13 Nov" for a game's installed test build.</summary>
    public static string Text(string? tag, string? targetDir, string? proxyName)
    {
        if (tag is null) return "test build";
        var asset = Component is { } c ? AppServices.Components.TryGetCached(c, tag)?.AssetName : null;
        string? product = null;
        if (asset is null && targetDir is not null && proxyName is not null)
            try { product = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(targetDir, proxyName)).ProductVersion; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) { }
        return OptiScalerOptions.TestBuildText(tag, asset, product);
    }

    /// <summary>"Needs a newer OptiScaler", with where to get one.</summary>
    public static string NeedsNewerText(string what) => Allowed
        ? $"{what}: needs a newer OptiScaler. Switch this game to the test build (OptiScaler version, above)."
        : $"{what}: needs a newer OptiScaler. Allow test builds in Settings > OptiScaler to try it.";

    /// <summary>Test builds in the data folder, newest first.</summary>
    public static List<CachedComponent> Downloaded() =>
        Component is { } c ? AppServices.Components.Downloaded(c).OrderByDescending(d => d.Version, StringComparer.Ordinal).ToList() : new();

    public static CachedComponent? PreviousOf(string? tag) =>
        tag is null ? null : Downloaded().FirstOrDefault(d => string.CompareOrdinal(d.Version, tag) < 0);

    public static bool IsHeld(GameInfo game) => AppServices.Settings.Current.TestBuildHeldGames.Contains(game.Id);

    public static ReleaseInfo? Newest => Component is { } c ? AppServices.Updates.Target(c) : null;

    // ---------------- per game ----------------

    private static string? Running(GameInfo game) =>
        game.TargetDir is { } dir && OptiScalerInstaller.RunningFrom(dir).FirstOrDefault() is { } name ? $"Close the game first ({name} is running); nothing was changed." : null;

    /// <summary>Stable → the newest test build (or a held game back to the newest).</summary>
    public static async Task<InstallResult> UseNewestAsync(GameInfo game, IProgress<string>? progress)
    {
        if (WhyNot(game, Manifest(game)) is { } why) return Fail(why);
        if (Running(game) is { } running) return Fail(running);
        if (Newest is null) await AppServices.Updates.CheckAsync(CancellationToken.None);
        SetHeld(game, false);
        return await GameUpdates.InstallTestBuildAsync(game, progress);
    }

    /// <summary>Back to Stable: an exact Undo when the last update came from it, otherwise an update that keeps the settings.</summary>
    public static Task<InstallResult> BackToStableAsync(GameInfo game, IProgress<string>? progress)
    {
        if (Running(game) is { } running) return Task.FromResult(Fail(running));
        SetHeld(game, false);
        return GameUpdates.BackToStableAsync(game, progress);
    }

    /// <summary>The test build before this game's one (kept in the data folder): Undo when it came from there, else an update.</summary>
    public static async Task<InstallResult> GoBackAsync(GameInfo game, IProgress<string>? progress)
    {
        if (Running(game) is { } running) return Fail(running);
        var manifest = Manifest(game);
        if (Component is not { } c || PreviousOf(manifest?.Version) is not { } previous) return Fail("No earlier test build is in Upshift's folder.");
        InstallResult result;
        if (manifest!.LastUpdate is { FromVersion: { } from, UndoFolder: { } undo } && from == previous.Version && Directory.Exists(Path.Combine(game.TargetDir!, undo)))
            result = await GameUpdates.UndoAsync(game, progress);
        else
            result = await GameUpdates.UpdateAsync(game, previous.Version, progress, to: c);
        if (result.Success) SetHeld(game, true);
        return result;
    }

    private static InstallManifest? Manifest(GameInfo game) => game.TargetDir is { } dir ? OptiScalerInstaller.ReadManifest(dir) : null;

    private static void SetHeld(GameInfo game, bool held)
    {
        var settings = AppServices.Settings.Current;
        if (held == settings.TestBuildHeldGames.Contains(game.Id)) return;
        if (held) settings.TestBuildHeldGames.Add(game.Id); else settings.TestBuildHeldGames.Remove(game.Id);
        try { AppServices.Settings.Save(settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---------------- updates ----------------

    private static Timer? _timer;
    private static int _checking;
    private static bool _startupDone;

    /// <summary>One check soon after start-up, then every 10 minutes: the daily check when due, and updates that waited for a game to close.</summary>
    public static void Start() =>
        _timer ??= new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));

    private static async Task TickAsync()
    {
        if (!Allowed || Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            var settings = AppServices.Settings.Current;
            var due = !_startupDone || settings.TestBuildLastCheckUtc is not { } last || DateTime.UtcNow - last > TimeSpan.FromDays(1);
            _startupDone = true;
            if (due) await CheckAsync();
            else if (settings.TestBuildAutoUpdate) await ApplyAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { CrashLog.Write(ex, "Test build check"); }
        finally { Interlocked.Exchange(ref _checking, 0); }
    }

    /// <summary>Asks GitHub for the newest test build, then updates the games on older ones, or (Ask first) offers it.</summary>
    public static async Task CheckAsync()
    {
        await AppServices.Updates.CheckAsync(CancellationToken.None);
        var settings = AppServices.Settings.Current;
        settings.TestBuildLastCheckUtc = DateTime.UtcNow;
        try { AppServices.Settings.Save(settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (Behind().Count == 0 || Newest is not { } newest) return;
        if (settings.TestBuildAutoUpdate) await ApplyAsync();
        else Notice?.Invoke($"OptiScaler test build {Label(newest)} is available" + Changes(newest), true);
    }

    /// <summary>Games on an older test build that aren't held back.</summary>
    public static List<GameInfo> Behind() =>
        Newest is not { } newest ? new()
            : GameUpdates.GamesOnTestBuild().Where(g => !IsHeld(g) && !g.HasAntiCheat
                  && string.CompareOrdinal(Manifest(g)?.Version, newest.Tag) < 0).ToList();

    /// <summary>"Update now", or the automatic update: every game behind and not running; running games wait for a later tick.</summary>
    public static async Task ApplyAsync()
    {
        if (Newest is not { } newest || Component is not { } c) return;
        var updated = new List<string>();
        var waiting = new List<string>();
        foreach (var game in Behind())
        {
            if (Running(game) is not null) { waiting.Add(game.Name); continue; }
            var result = await GameUpdates.UpdateAsync(game, newest.Tag, null, to: c);
            if (result.Success) updated.Add(game.Name);
        }
        if (updated.Count > 0)
            Notice?.Invoke($"OptiScaler test build updated to {Label(newest)} in {string.Join(", ", updated)}" + Changes(newest)
                           + (waiting.Count > 0 ? $" ({string.Join(", ", waiting)}: after the game closes.)" : ""), false);
        else if (waiting.Count > 0 && !AppServices.Settings.Current.TestBuildAutoUpdate)
            Notice?.Invoke($"Close {string.Join(", ", waiting)} first: test builds are never updated while the game runs.", false);
    }

    private static string Label(ReleaseInfo release) => OptiScalerOptions.TestBuildText(release.Tag, null, null).Replace("test build · ", "");

    private static string Changes(ReleaseInfo release) =>
        OptiScalerOptions.NightlyNotesLine(release.Notes) is { Length: > 0 } line ? ": " + line : ".";

    private static InstallResult Fail(string message) => new() { Message = message };
}
