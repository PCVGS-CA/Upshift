using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Text.Json;
using Upshift.Core.Artwork;
using Upshift.Core.Catalog;
using Upshift.Core.Detection;
using Upshift.Core.Discovery;
using Upshift.Core.Models;
using Upshift.Core.Util;
using Upshift.Core.Wiki;

namespace Upshift.Core.Services;

/// <summary>
/// Finds games, analyses them, and keeps a cached copy on disk so the Library opens instantly.
/// Everything here is read-only: nothing inside a game folder is changed.
/// </summary>
public sealed class GameLibraryService
{
    private readonly string _cachePath;
    private readonly string _manualPath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>Guards the artwork fields, which the background artwork fill changes while the UI reads them.</summary>
    private readonly object _sync = new();
    private List<GameInfo> _current = new();

    public GameLibraryService(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _cachePath = Path.Combine(dataDir, "library.json");
        _manualPath = Path.Combine(dataDir, "manual-folders.json");
        _exeOverridesPath = Path.Combine(dataDir, "exe-overrides.json");
        ArtworkDir = Path.Combine(dataDir, "artwork");
    }

    /// <summary>Main exes the user picked, by game id. Kept in their own file so clearing the library cache keeps them.</summary>
    private readonly string _exeOverridesPath;

    /// <summary>Downloaded and user-chosen cover art lives here.</summary>
    public string ArtworkDir { get; }

    public IReadOnlyList<GameInfo> Current => _current;

    public async Task<IReadOnlyList<GameInfo>> LoadCachedAsync()
    {
        if (!File.Exists(_cachePath)) return _current;
        try
        {
            await using var stream = File.OpenRead(_cachePath);
            var list = await JsonSerializer.DeserializeAsync<List<GameInfo>>(stream, CatalogLoader.JsonOptions) ?? new();
            _current = list.Where(g => PathUtil.SafeDirectoryExists(g.InstallDir)).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _current = new();
        }
        return _current;
    }

    /// <summary>
    /// Quick scan checks every launcher. The drive scan also walks every fixed drive, which can take a few minutes.
    /// Games found by an earlier drive scan are kept on a quick scan as long as their folder still exists.
    /// </summary>
    public async Task<IReadOnlyList<GameInfo>> ScanAsync(bool includeDriveScan, IProgress<string>? progress, CancellationToken ct)
    {
        var previous = _current;
        var exeOverrides = LoadExeOverrides();
        var result = await Task.Run(async () =>
        {
            var sources = new IGameSource[]
            {
                new SteamSource(), new EpicSource(), new GogSource(), new XboxSource(),
                new HeroicAndItchSource(), new RegistrySources()
            };

            var found = new List<DiscoveredGame>();
            foreach (var source in sources)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Checking {source.DisplayName}…");
                try { found.AddRange(source.Find(ct)); }
                catch (OperationCanceledException) { throw; }
                catch { /* one broken launcher shouldn't stop the whole scan */ }
            }

            foreach (var folder in LoadManualFolders().Where(PathUtil.SafeDirectoryExists))
                found.Add(new DiscoveredGame(PathUtil.PrettyName(folder), folder, GameSourceKind.Manual));

            if (includeDriveScan)
            {
                progress?.Report("Scanning drives…");
                found.AddRange(new DriveScanSource().Find(ct, progress));
            }
            else
            {
                found.AddRange(previous
                    .Where(g => g.Source == GameSourceKind.DriveScan && PathUtil.SafeDirectoryExists(g.InstallDir))
                    .Select(g => new DiscoveredGame(g.Name, g.InstallDir, GameSourceKind.DriveScan)));
            }

            var merged = Merge(found);
            var analyzed = new ConcurrentBag<GameInfo>();
            var done = 0;
            progress?.Report($"Checking {merged.Count} games…");

            await Parallel.ForEachAsync(merged,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                (game, token) =>
                {
                    analyzed.Add(GameAnalyzer.Analyze(game, token, exeOverrides.GetValueOrDefault(PathUtil.StableId(game.InstallDir))));
                    var n = Interlocked.Increment(ref done);
                    if (n % 5 == 0) progress?.Report($"Checking games… {n} of {merged.Count}");
                    return ValueTask.CompletedTask;
                });

            return analyzed.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }, ct);

        KeepArtwork(result, previous);
        _current = result;
        await SaveAsync();
        return result;
    }

    /// <summary>Adds a folder the user picked, remembers it for future scans, and returns the analysed game.</summary>
    public async Task<GameInfo> AddManualAsync(string folder)
    {
        var folders = LoadManualFolders();
        if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            folders.Add(folder);
            await File.WriteAllTextAsync(_manualPath, JsonSerializer.Serialize(folders));
        }

        var exeOverride = LoadExeOverrides().GetValueOrDefault(PathUtil.StableId(folder));
        var info = await Task.Run(() => GameAnalyzer.Analyze(
            new DiscoveredGame(PathUtil.PrettyName(folder), folder, GameSourceKind.Manual), CancellationToken.None, exeOverride));
        KeepArtwork(new[] { info }, _current);

        _current = _current.Where(g => g.Id != info.Id)
            .Append(info)
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        await SaveAsync();
        return info;
    }

    /// <summary>
    /// Looks up cover art online for every game that has none, calling onFound as each picture arrives.
    /// Returns quietly when the PC is offline. Games with nothing found anywhere are retried after a few days.
    /// </summary>
    public async Task<IReadOnlyList<(GameInfo Game, ArtworkResult Result)>> FillArtworkAsync(
        ArtworkFinder finder, ArtworkSources config, Action<GameInfo>? onFound, CancellationToken ct)
    {
        var results = new ConcurrentBag<(GameInfo, ArtworkResult)>();
        var retryBefore = DateTime.UtcNow.AddDays(-Math.Max(0, config.RetryMissingAfterDays));
        List<GameInfo> todo;
        lock (_sync)
            todo = _current.Where(g => g.DisplayArtworkPath is null && (g.ArtworkMissUtc is null || g.ArtworkMissUtc < retryBefore)).ToList();

        if (todo.Count == 0 || !NetworkInterface.GetIsNetworkAvailable()) return results.ToList();

        using var offline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var silent = 0;
        var anyAnswer = false;
        var changed = false;

        try
        {
            await Parallel.ForEachAsync(todo,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, config.MaxParallel), CancellationToken = offline.Token },
                async (game, token) =>
                {
                    var result = await finder.FindAsync(game, token);
                    results.Add((game, result));

                    // No server answering the first few lookups means no internet: stop without fuss.
                    if (result.AnyResponse) anyAnswer = true;
                    else if (!anyAnswer && Interlocked.Increment(ref silent) >= 3) offline.Cancel();

                    lock (_sync)
                    {
                        if (result.Outcome == ArtworkOutcome.Found)
                        {
                            game.ArtworkPath = result.Path;
                            game.ArtworkSource = result.Source;
                            game.ArtworkMissUtc = null;
                        }
                        else if (result.Outcome == ArtworkOutcome.NotFound)
                        {
                            game.ArtworkMissUtc = DateTime.UtcNow;
                        }
                        else return;
                        changed = true;
                    }
                    if (result.Outcome == ArtworkOutcome.Found) onFound?.Invoke(game);
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* offline */ }
        finally
        {
            if (changed) await SaveAsync();
        }
        return results.ToList();
    }

    /// <summary>Copies a picture the user picked into the artwork folder and uses it for this game from now on.</summary>
    public async Task<GameInfo?> SetCustomArtworkAsync(string gameId, string imageFile)
    {
        GameInfo? game;
        lock (_sync) game = _current.FirstOrDefault(g => g.Id == gameId);
        if (game is null) return null;

        Directory.CreateDirectory(ArtworkDir);
        var ext = Path.GetExtension(imageFile).ToLowerInvariant();
        // A new name each time, so the picture on screen really changes (images are cached by path).
        var target = Path.Combine(ArtworkDir, $"{game.Id}-custom-{DateTime.UtcNow.Ticks}{ext}");
        await Task.Run(() => File.Copy(imageFile, target, overwrite: true));

        string? old;
        lock (_sync)
        {
            old = game.CustomArtworkPath;
            game.CustomArtworkPath = target;
        }
        DeleteArtworkFile(old);
        await SaveAsync();
        return game;
    }

    /// <summary>
    /// Checks one game again (after an install or uninstall changed its folder), keeping the user's exe choice,
    /// picture and online engine, and saves it. Returns the new GameInfo.
    /// </summary>
    public async Task<GameInfo?> ReanalyzeAsync(string gameId)
    {
        GameInfo? old;
        lock (_sync) old = _current.FirstOrDefault(g => g.Id == gameId);
        if (old is null) return null;

        var exeOverride = LoadExeOverrides().GetValueOrDefault(gameId);
        var discovered = new DiscoveredGame(old.Name, old.InstallDir, old.Source, old.SourceId, old.LaunchExePath,
            ArtworkPath: old.ArtworkSource == "Steam" ? old.ArtworkPath : null);
        var info = await Task.Run(() => GameAnalyzer.Analyze(discovered, CancellationToken.None, exeOverride));
        KeepArtwork(new[] { info }, new[] { old });

        lock (_sync) _current = _current.Select(g => g.Id == gameId ? info : g).ToList();
        await SaveAsync();
        return info;
    }

    /// <summary>Forgets the user's picture; the automatic cover art (or a fresh lookup) takes over again.</summary>
    public async Task<GameInfo?> ResetArtworkAsync(string gameId)
    {
        GameInfo? game;
        string? old;
        lock (_sync)
        {
            game = _current.FirstOrDefault(g => g.Id == gameId);
            if (game is null) return null;
            old = game.CustomArtworkPath;
            game.CustomArtworkPath = null;
            game.ArtworkMissUtc = null;
        }
        DeleteArtworkFile(old);
        await SaveAsync();
        return game;
    }

    /// <summary>Makes every game without art eligible for lookup again (e.g. after a SteamGridDB key is added).</summary>
    public async Task ForgetArtworkMissesAsync()
    {
        lock (_sync)
            foreach (var g in _current) g.ArtworkMissUtc = null;
        await SaveAsync();
    }

    /// <summary>A rescan builds fresh GameInfo objects; carry over the picture chosen or downloaded last time.</summary>
    private void KeepArtwork(IEnumerable<GameInfo> fresh, IEnumerable<GameInfo> previous)
    {
        lock (_sync)
        {
            var byId = previous.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var game in fresh)
            {
                if (!byId.TryGetValue(game.Id, out var old)) continue;

                if (FileExists(old.CustomArtworkPath)) game.CustomArtworkPath = old.CustomArtworkPath;
                if (game.ArtworkPath is null && FileExists(old.ArtworkPath))
                {
                    game.ArtworkPath = old.ArtworkPath;
                    game.ArtworkSource = old.ArtworkSource;
                }
                if (game.ArtworkPath is null) game.ArtworkMissUtc = old.ArtworkMissUtc;

                // An engine found online stays until the game's own files say otherwise.
                if (game.Engine == "Unknown")
                {
                    if (old.EngineSource is not null)
                    {
                        game.Engine = old.Engine;
                        game.EngineSource = old.EngineSource;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Uses the exe the user picked as this game's main exe (or goes back to automatic with null),
    /// remembers the choice for future scans, and re-checks the game with it.
    /// </summary>
    public async Task<GameInfo?> SetExeOverrideAsync(string gameId, string? exePath)
    {
        GameInfo? old;
        lock (_sync) old = _current.FirstOrDefault(g => g.Id == gameId);
        if (old is null) return null;

        var overrides = LoadExeOverrides();
        if (exePath is null) overrides.Remove(gameId);
        else overrides[gameId] = exePath;
        await File.WriteAllTextAsync(_exeOverridesPath, JsonSerializer.Serialize(overrides, new JsonSerializerOptions { WriteIndented = true }));
        return await ReanalyzeAsync(gameId);
    }

    /// <summary>
    /// Gets each game's PCGamingWiki entry: from the cache while it's fresh (CacheDays), otherwise one request per game.
    /// onResult is called for every game, including failures, so the details pane can say what happened.
    /// An engine from the wiki is used only for games whose own files didn't reveal one.
    /// </summary>
    public async Task<IReadOnlyList<(GameInfo Game, WikiEntry Entry, bool FromCache)>> FillWikiAsync(
        PcGamingWikiClient client, WikiCache cache, PcGamingWikiSource config,
        Action<GameInfo, WikiEntry>? onResult, CancellationToken ct)
    {
        var results = new List<(GameInfo, WikiEntry, bool)>();
        var freshAfter = DateTime.UtcNow.AddDays(-Math.Max(1, config.CacheDays));
        List<GameInfo> games;
        lock (_sync) games = _current.ToList();

        var online = NetworkInterface.GetIsNetworkAvailable();
        var failuresInARow = 0;
        var anySuccess = false;
        var libraryChanged = false;
        var cacheChanged = false;

        try
        {
            // One game at a time: PCGamingWiki is a volunteer-run wiki.
            foreach (var game in games)
            {
                ct.ThrowIfCancellationRequested();
                var cached = cache.Get(game.Id);
                WikiEntry entry;
                var fromCache = false;

                if (cached is not null && cached.Status != WikiStatus.Failed && cached.FetchedUtc > freshAfter)
                {
                    entry = cached;
                    fromCache = true;
                }
                else if (!online || (!anySuccess && failuresInARow >= 3))
                {
                    entry = cached ?? new WikiEntry
                    {
                        Status = WikiStatus.Failed,
                        Message = online ? "Couldn't reach PCGamingWiki." : "This PC is offline, so PCGamingWiki wasn't checked.",
                        FetchedUtc = DateTime.UtcNow
                    };
                    fromCache = cached is not null;
                }
                else
                {
                    entry = await client.LookupAsync(game, cached?.Title, ct);
                    if (entry.Status == WikiStatus.Failed)
                    {
                        failuresInARow++;
                        // A stale answer is still better than none.
                        if (cached is not null)
                        {
                            entry = cached;
                            fromCache = true;
                        }
                    }
                    else
                    {
                        failuresInARow = 0;
                        anySuccess = true;
                        cache.Set(game.Id, entry);
                        cacheChanged = true;
                    }
                }

                if (entry.Status == WikiStatus.Found && entry.Engine is not null
                    && (game.Engine == "Unknown" || game.EngineSource is not null) && game.Engine != entry.Engine)
                {
                    lock (_sync)
                    {
                        game.Engine = entry.Engine;
                        game.EngineSource = "PCGamingWiki";
                    }
                    libraryChanged = true;
                }

                results.Add((game, entry, fromCache));
                onResult?.Invoke(game, entry);
            }
        }
        finally
        {
            if (cacheChanged) cache.Save();
            if (libraryChanged) await SaveAsync();
        }
        return results;
    }

    private Dictionary<string, string> LoadExeOverrides()
    {
        try
        {
            return File.Exists(_exeOverridesPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_exeOverridesPath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }

    private void DeleteArtworkFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        // Only ever delete our own copies, never a file the user picked from elsewhere.
        if (!PathUtil.IsSameOrInside(PathUtil.Normalize(path), PathUtil.Normalize(ArtworkDir))) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static bool FileExists(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try { return File.Exists(path); } catch { return false; }
    }

    /// <summary>
    /// Removes duplicates. Launcher results win over the drive scan; a drive-scan or installed-program result
    /// is dropped if it sits inside (or around) a game a launcher already reported.
    /// </summary>
    private static List<DiscoveredGame> Merge(IEnumerable<DiscoveredGame> found)
    {
        var accepted = new List<(DiscoveredGame Game, string Norm)>();
        foreach (var game in found.OrderBy(g => g.Source))
        {
            var norm = PathUtil.Normalize(game.InstallDir);
            if (accepted.Any(a => a.Norm == norm)) continue;

            var loose = game.Source is GameSourceKind.DriveScan or GameSourceKind.InstalledProgram;
            if (loose && accepted.Any(a => PathUtil.IsSameOrInside(norm, a.Norm) || PathUtil.IsSameOrInside(a.Norm, norm)))
                continue;

            accepted.Add((game, norm));
        }
        return accepted.Select(a => a.Game).ToList();
    }

    private List<string> LoadManualFolders()
    {
        try
        {
            return File.Exists(_manualPath)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_manualPath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }

    private async Task SaveAsync()
    {
        byte[] json;
        lock (_sync) json = JsonSerializer.SerializeToUtf8Bytes(_current, CatalogLoader.JsonOptions);

        await _saveLock.WaitAsync();
        try
        {
            var temp = _cachePath + ".tmp";
            await File.WriteAllBytesAsync(temp, json);
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (IOException) { /* the cache is a convenience; a failed save just means a rescan next time */ }
        finally
        {
            _saveLock.Release();
        }
    }
}
