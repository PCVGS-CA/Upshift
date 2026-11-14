using Upshift.Core.Catalog;
using Upshift.Core.Components;
using Upshift.Core.Install;
using Upshift.Core.Models;
using Upshift.Core.Store;

namespace Upshift.App.Services;

/// <summary>
/// The Upscaler Store in the app: the entries (store.json, built in or a newer copy from the repo), what can go into which
/// game, and adding, taking out and going back a version. Downloads come only from the entry's own GitHub repo, through the
/// component store with <c>RequireDigest</c> (no GitHub SHA-256, or a different one → refused). Recipes whose downloads say
/// "latest" are checked at start-up and once a day: "Ask first" (default) or automatic, never while the game runs; a game
/// taken back a version is left alone until "Use the newest". Before an OptiScaler update, undo or switch the game's add-ons
/// come out (OptiScaler refuses otherwise); an update puts them back when they still fit.
/// </summary>
public static class StoreService
{
    private static StoreCatalog? _store;
    public static StoreCatalog Store => _store ??= StoreCatalog.Load(AppServices.DataDir);

    /// <summary>A message for the Library ("ANR updated to FG-V.B16: …"), and whether it offers "Update now".</summary>
    public static event Action<string, bool>? Notice;

    // ---------------- what fits ----------------

    public static StoreContext Context(GameInfo game) => new()
    {
        GpuVendor = AppServices.Gpu?.Vendor.ToString().ToLowerInvariant(),
        GpuGeneration = AppServices.Gpu?.Generation,
        Api = game.Api.ToString(),
        TestBuildComponentId = GameUpdates.TestBuild?.Id
    };

    public static bool WorksOnThisPc(StoreEntry entry) => StoreCatalog.FitsGpu(entry, AppServices.Gpu);

    /// <summary>Why the entry can't go into this game, or null when it can.</summary>
    public static string? WhyNot(StoreEntry entry, GameInfo game)
    {
        if (!entry.Installable) return "Nothing is installed for this one.";
        if (game.HasAntiCheat) return "Not for games with anti-cheat.";
        if (game.TargetDir is null) return "The game's folder isn't known.";
        if (entry.Type == StoreTypes.AuthorSetup && AuthorSetup.Blocked(AppServices.DataDir).TryGetValue(entry.Id, out var why))
            return $"Can't be managed safely on this PC: {why}";
        if (!StoreCatalog.FitsGpu(entry, AppServices.Gpu)) return "Not for this graphics card.";
        if (!StoreCatalog.FitsApi(entry, game.Api)) return $"Only for {string.Join(" or ", entry.Apis)} games.";
        return OptiScalerInstaller.StoreRequirementProblem(entry, game.TargetDir, Context(game));
    }

    public static IReadOnlyList<StoreInstalled> InGame(GameInfo game) =>
        game.TargetDir is { } dir ? OptiScalerInstaller.ReadStoreRecord(dir).Entries : Array.Empty<StoreInstalled>();

    public static StoreInstalled? InGame(GameInfo game, string entryId) => InGame(game).FirstOrDefault(e => e.EntryId == entryId);

    // ---------------- downloads ----------------

    private static string RepoOf(StoreEntry entry) => new Uri(entry.Repo).AbsolutePath.Trim('/');

    private static CatalogComponent Component(StoreEntry entry, string downloadId, string asset) => new()
    {
        Id = $"store-{entry.Id}-{downloadId}", Name = entry.Name, Kind = "store", Repo = RepoOf(entry), AssetPattern = asset, RequireDigest = true
    };

    private static IEnumerable<(string Id, string Asset, string Tag)> Downloads(StoreEntry entry) =>
        entry.Type == StoreTypes.AuthorSetup && entry.Setup is { } s
            ? new[] { ("setup", s.Asset, s.Tag) }
            : (entry.Recipe ?? new()).Where(x => x.Type == "download").Select(x => (x.Id!, x.Asset!, x.Tag!));

    /// <summary>The newest regular release of the entry's repo with a matching asset that GitHub gives a SHA-256 for.</summary>
    public static async Task<ReleaseInfo?> NewestAsync(StoreEntry entry, string asset, bool fromCache = false)
    {
        var repo = RepoOf(entry);
        var list = fromCache ? AppServices.Components.GitHub.GetCachedReleases(repo) : await AppServices.Components.GitHub.GetReleasesAsync(repo, CancellationToken.None);
        var component = Component(entry, "x", asset);
        return list?.Releases.Where(r => !r.Prerelease && ComponentStore.PickAsset(component, r) is { Sha256: not null })
            .OrderByDescending(r => r.Published).FirstOrDefault();
    }

    /// <summary>Downloads (or finds) every release the entry needs: by tag, or the newest for "latest" (or <paramref name="version"/>).</summary>
    public static async Task<(Dictionary<string, string> Sources, string Version, DateTimeOffset? Released)> EnsureAsync(StoreEntry entry, string? version, IProgress<string>? progress)
    {
        var sources = new Dictionary<string, string>();
        var tags = new List<string>();
        DateTimeOffset? released = null;
        var versions = version?.Split(" + ");
        var i = 0;
        foreach (var (id, asset, tag) in Downloads(entry))
        {
            var component = Component(entry, id, asset);
            string chosen;
            if (versions is not null && i < versions.Length) chosen = versions[i];
            else if (tag == "latest") chosen = (await NewestAsync(entry, asset))?.Tag ?? throw new ComponentDownloadException($"No release of {entry.Name} with a GitHub checksum was found.");
            else chosen = tag;
            var keep = Games(entry.Id).Select(g => g.Installed.Version.Split(" + ").ElementAtOrDefault(i)).OfType<string>().Distinct().ToList();
            var cached = await AppServices.Components.EnsureAsync(component, chosen, progress, CancellationToken.None, keep: keep);
            sources[id] = cached.Folder;
            tags.Add(chosen);
            released ??= (await AppServices.Components.GitHub.GetReleaseAsync(component.Repo, chosen, CancellationToken.None)).Published;
            i++;
        }
        return (sources, string.Join(" + ", tags), released);
    }

    // ---------------- per game ----------------

    public static List<(GameInfo Game, StoreInstalled Installed)> Games(string entryId) =>
        AppServices.Library.Current.Select(g => (Game: g, Installed: InGame(g, entryId))).Where(x => x.Installed is not null).Select(x => (x.Game, x.Installed!)).ToList();

    /// <summary>Adds the entry (newest, or a given version) to a game, inside the one-change-at-a-time gate.</summary>
    public static Task<InstallResult> AddAsync(StoreEntry entry, GameInfo game, IProgress<string>? progress, string? version = null, string? userFile = null) =>
        GameUpdates.RunExclusiveAsync(game, () => AddInGateAsync(entry, game, progress, version, userFile));

    private static async Task<InstallResult> AddInGateAsync(StoreEntry entry, GameInfo game, IProgress<string>? progress, string? version, string? userFile)
    {
        if (WhyNot(entry, game) is { } why) return Fail(why);
        if (AppServices.PretendGpu is not null) return Fail("A pretend graphics card is set in Settings, so nothing is installed. Turn it off first.");
        if (entry.Type == StoreTypes.UserSupplied)
        {
            // The user's file is kept in Upshift's folder, so it can go back in after an OptiScaler update.
            var kept = Path.Combine(AppServices.DataDir, "store-user", entry.Id, entry.UserFile!.FileName);
            if (userFile is not null)
            {
                if (!Path.GetFileName(userFile).Equals(entry.UserFile.FileName, StringComparison.OrdinalIgnoreCase))
                    return Fail($"Choose a file called {entry.UserFile.FileName}.");
                Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                File.Copy(userFile, kept, true);
            }
            if (!File.Exists(kept)) return Fail($"Choose your {entry.UserFile.FileName} first.");
            userFile = kept;
        }
        var (sources, tags, released) = entry.Type == StoreTypes.UserSupplied ? (new Dictionary<string, string>(), "", (DateTimeOffset?)null) : await EnsureAsync(entry, version, progress);
        progress?.Report(entry.Type == StoreTypes.AuthorSetup ? $"Running {entry.Name}'s setup…" : $"Adding {entry.Name} to {game.Name}…");
        return await InstallRunner.RunAsync(new InstallPlan
        {
            Operation = entry.Type == StoreTypes.AuthorSetup ? InstallOperation.StoreSetup : InstallOperation.StoreAdd,
            GameId = game.Id, GameName = game.Name, TargetDir = game.TargetDir!, ComponentName = entry.Name,
            StoreEntryJson = System.Text.Json.JsonSerializer.Serialize(entry, CatalogLoader.JsonOptions), StoreEntryId = entry.Id,
            StoreSources = sources, Version = tags, Released = released, StoreContext = Context(game), AddFileFrom = userFile
        });
    }

    /// <summary>Takes the entry out: the game is exactly as before it was added.</summary>
    public static Task<InstallResult> RemoveAsync(GameInfo game, string entryId, IProgress<string>? progress) =>
        GameUpdates.RunExclusiveAsync(game, async () =>
        {
            SetHeld(game, entryId, false);
            progress?.Report($"Taking it out of {game.Name}…");
            return await InstallRunner.RunAsync(new InstallPlan { Operation = InstallOperation.StoreRemove, GameId = game.Id, GameName = game.Name, TargetDir = game.TargetDir!, StoreEntryId = entryId });
        });

    /// <summary>The downloaded version before the game's one (single-download recipes on "latest"); updates then leave the game alone.</summary>
    public static CachedComponent? PreviousOf(StoreEntry entry, string? version)
    {
        if (version is null || Downloads(entry).ToList() is not { Count: 1 } list || list[0].Tag != "latest") return null;
        var component = Component(entry, list[0].Id, list[0].Asset);
        var cachedList = AppServices.Components.GitHub.GetCachedReleases(component.Repo)?.Releases;
        DateTimeOffset? Date(string tag) => cachedList?.FirstOrDefault(r => r.Tag == tag)?.Published;
        var current = Date(version);
        return AppServices.Components.Downloaded(component)
            .Where(d => d.Version != version && (current is null || Date(d.Version) is not { } dt || dt < current))
            .OrderByDescending(d => Date(d.Version) ?? DateTimeOffset.MinValue).FirstOrDefault();
    }

    public static async Task<InstallResult> GoBackAsync(StoreEntry entry, GameInfo game, IProgress<string>? progress)
    {
        if (InGame(game, entry.Id) is not { } installed || PreviousOf(entry, installed.Version) is not { } previous)
            return Fail("No earlier version is in Upshift's folder.");
        var result = await AddAsync(entry, game, progress, previous.Version);
        if (result.Success) SetHeld(game, entry.Id, true);
        return result;
    }

    public static async Task<InstallResult> UseNewestAsync(StoreEntry entry, GameInfo game, IProgress<string>? progress)
    {
        SetHeld(game, entry.Id, false);
        return await AddAsync(entry, game, progress);
    }

    public static bool IsHeld(GameInfo game, string entryId) => AppServices.Settings.Current.StoreHeld.Contains($"{game.Id}|{entryId}");

    private static void SetHeld(GameInfo game, string entryId, bool held)
    {
        var settings = AppServices.Settings.Current;
        var key = $"{game.Id}|{entryId}";
        if (held == settings.StoreHeld.Contains(key)) return;
        if (held) settings.StoreHeld.Add(key); else settings.StoreHeld.Remove(key);
        try { AppServices.Settings.Save(settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---------------- around OptiScaler changes (inside the caller's exclusive change) ----------------

    /// <summary>Every add-on out of the game first (also an old ANR record). Returns what was there, or a failure.</summary>
    public static async Task<(List<StoreInstalled> Was, InstallResult? Failed)> TakeOutAsync(GameInfo game, InstallManifest manifest, IProgress<string>? progress)
    {
        if (manifest.Anr is not null)
        {
            progress?.Report($"Taking ANR out of {game.Name} first…");
            var legacy = await InstallRunner.RunAsync(new InstallPlan { Operation = InstallOperation.RemoveAnr, GameId = game.Id, GameName = game.Name, TargetDir = game.TargetDir! });
            if (!legacy.Success) return (new(), legacy);
        }
        var was = InGame(game).ToList();
        foreach (var installed in Enumerable.Reverse(was))
        {
            progress?.Report($"Taking {installed.Name} out of {game.Name} first…");
            var result = await InstallRunner.RunAsync(new InstallPlan { Operation = InstallOperation.StoreRemove, GameId = game.Id, GameName = game.Name, TargetDir = game.TargetDir!, StoreEntryId = installed.EntryId });
            if (!result.Success) return (was, result);
        }
        return (was, null);
    }

    /// <summary>After an OptiScaler update: the add-ons go back in (same version), where they still fit. Returns a note.</summary>
    public static async Task<string?> PutBackAsync(GameInfo game, List<StoreInstalled> was, IProgress<string>? progress)
    {
        var notes = new List<string>();
        foreach (var installed in was)
        {
            var entry = installed.Entry ?? Store.Find(installed.EntryId);
            if (entry is null || entry.Type == StoreTypes.AuthorSetup) { notes.Add($"{installed.Name} was taken out; add it again from the Store."); continue; }
            if (WhyNot(entry, game) is { } why) { notes.Add($"{installed.Name} was taken out: {why}"); continue; }
            try
            {
                var result = await AddInGateAsync(entry, game, progress, installed.Version, null);
                if (!result.Success) notes.Add($"{installed.Name} couldn't be put back: {result.Message}");
            }
            catch (ComponentDownloadException ex) { notes.Add($"{installed.Name} couldn't be put back: {Helpers.ScreenText.Clean(ex.Message)}"); }
        }
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    // ---------------- updates ----------------

    private static Timer? _timer;
    private static int _checking;
    private static bool _startupDone;
    private static readonly Dictionary<string, ReleaseInfo> NewestSeen = new();

    public static ReleaseInfo? NewestKnown(string entryId) => NewestSeen.GetValueOrDefault(entryId);

    public static void Start()
    {
        _ = Task.Run(async () =>
        {
            try { await StoreCatalog.RefreshAsync(AppServices.DataDir, AppInfo.UserAgent, CancellationToken.None); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { CrashLog.Write(ex, "Store refresh"); }
        });
        _timer ??= new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(25), TimeSpan.FromMinutes(10));
    }

    private static async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            var settings = AppServices.Settings.Current;
            var due = !_startupDone || settings.StoreLastCheckUtc is not { } last || DateTime.UtcNow - last > TimeSpan.FromDays(1);
            _startupDone = true;
            if (due) await CheckAsync();
            else if (settings.StoreAutoUpdate) await ApplyAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { CrashLog.Write(ex, "Store update check"); }
        finally { Interlocked.Exchange(ref _checking, 0); }
    }

    /// <summary>Recipes in use whose downloads follow "latest": a newer release is offered (Ask first) or put in.</summary>
    public static async Task CheckAsync()
    {
        var settings = AppServices.Settings.Current;
        var ids = AppServices.Library.Current.SelectMany(InGame).Select(i => i.EntryId).Distinct().ToList();
        if (ids.Count == 0) return;
        foreach (var id in ids)
            if (Store.Find(id) is { Type: StoreTypes.Recipe } entry && Downloads(entry).ToList() is { Count: 1 } list && list[0].Tag == "latest"
                && await NewestAsync(entry, list[0].Asset) is { } newest)
                NewestSeen[id] = newest;
        settings.StoreLastCheckUtc = DateTime.UtcNow;
        try { AppServices.Settings.Save(settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var behind = Behind();
        if (behind.Count == 0) return;
        if (settings.StoreAutoUpdate) await ApplyAsync();
        else
            foreach (var group in behind.GroupBy(b => b.Entry.Id))
                Notice?.Invoke($"{group.First().Entry.Name} {NewestSeen[group.Key].Tag} is available: {Anr.NotesLine(NewestSeen[group.Key].Notes)}", true);
    }

    private static List<(GameInfo Game, StoreEntry Entry, StoreInstalled Installed)> Behind() =>
        AppServices.Library.Current.SelectMany(g => InGame(g).Select(i => (Game: g, Installed: i)))
            .Where(x => NewestSeen.TryGetValue(x.Installed.EntryId, out var n) && n.Tag != x.Installed.Version
                        && (x.Installed.Released is null || n.Published > x.Installed.Released) && !IsHeld(x.Game, x.Installed.EntryId)
                        && Store.Find(x.Installed.EntryId) is not null && !x.Game.HasAntiCheat)
            .Select(x => (x.Game, Store.Find(x.Installed.EntryId)!, x.Installed)).ToList();

    /// <summary>"Update now", or the automatic update: every game behind and not running; running games wait for a later tick.</summary>
    public static async Task ApplyAsync()
    {
        var updated = new Dictionary<string, List<string>>();
        var waiting = new List<string>();
        foreach (var (game, entry, _) in Behind())
        {
            if (OptiScalerInstaller.RunningFrom(game.TargetDir!).Count > 0) { waiting.Add(game.Name); continue; }
            var result = await AddAsync(entry, game, null);
            if (result.Success) (updated.TryGetValue(entry.Id, out var l) ? l : updated[entry.Id] = new()).Add(game.Name);
        }
        foreach (var (id, games) in updated)
            Notice?.Invoke($"{Store.Find(id)!.Name} updated to {NewestSeen[id].Tag}: {Anr.NotesLine(NewestSeen[id].Notes)}" +
                           (waiting.Count > 0 ? $" ({string.Join(", ", waiting)}: after the game closes.)" : ""), false);
        if (updated.Count == 0 && waiting.Count > 0 && !AppServices.Settings.Current.StoreAutoUpdate)
            Notice?.Invoke($"Close {string.Join(", ", waiting)} first: add-ons are never updated while the game runs.", false);
    }

    private static InstallResult Fail(string message) => new() { Message = message };
}
