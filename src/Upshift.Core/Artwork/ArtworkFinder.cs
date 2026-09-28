using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Upshift.Core.Catalog;
using Upshift.Core.Models;
using Upshift.Core.Util;

namespace Upshift.Core.Artwork;

public enum ArtworkOutcome
{
    Found,
    /// <summary>Every source answered and none had art.</summary>
    NotFound,
    /// <summary>At least one source couldn't be reached, so the answer isn't final.</summary>
    Unreachable
}

/// <param name="AnyResponse">False when no server answered at all, which usually means the PC is offline.</param>
public sealed record ArtworkResult(ArtworkOutcome Outcome, string? Path, string? Source, IReadOnlyList<string> Trace, bool AnyResponse);

/// <summary>
/// Looks up cover art online for one game, trying GOG, Epic, the Steam store and SteamGridDB in turn.
/// No sign-in is needed for any of them. Every URL comes from the artworkSources section of catalog.json.
/// Steam's local cache is handled earlier, by the Steam scan itself.
/// </summary>
public sealed class ArtworkFinder : IDisposable
{
    private const long MaxImageBytes = 15 * 1024 * 1024;

    private readonly ArtworkSources _config;
    private readonly string _artworkDir;
    private readonly Func<string?> _steamGridDbKey;
    private readonly HttpClient _http;
    private readonly Lazy<EpicCatalog> _epic;

    public ArtworkFinder(ArtworkSources config, string artworkDir, Func<string?> steamGridDbKey)
    {
        _config = config;
        _artworkDir = artworkDir;
        _steamGridDbKey = steamGridDbKey;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(5, config.TimeoutSeconds)) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);
        _epic = new Lazy<EpicCatalog>(() => EpicCatalog.Load(config.Epic));
    }

    public string ArtworkDir => _artworkDir;

    public void Dispose() => _http.Dispose();

    public async Task<ArtworkResult> FindAsync(GameInfo game, CancellationToken ct)
    {
        var lookup = new Lookup(game, new List<string>());

        foreach (var (source, find) in Sources(game))
        {
            ct.ThrowIfCancellationRequested();
            var path = await find(lookup, ct);
            if (path is not null) return new ArtworkResult(ArtworkOutcome.Found, path, source, lookup.Trace, true);
        }

        return new ArtworkResult(lookup.Unreachable ? ArtworkOutcome.Unreachable : ArtworkOutcome.NotFound, null, null, lookup.Trace, lookup.GotResponse);
    }

    private IEnumerable<(string Source, Func<Lookup, CancellationToken, Task<string?>> Find)> Sources(GameInfo game)
    {
        if (GogId(game) is not null) yield return ("GOG", FromGogAsync);
        if (game.Source == GameSourceKind.Epic) yield return ("Epic", FromEpicAsync);
        yield return ("Steam store", FromSteamSearchAsync);
        if (!string.IsNullOrWhiteSpace(_steamGridDbKey())) yield return ("SteamGridDB", FromSteamGridDbAsync);
    }

    // ---------------- GOG ----------------

    /// <summary>GOG's own games, and GOG games installed through Heroic (whose ids are numeric).</summary>
    private static string? GogId(GameInfo game)
    {
        if (string.IsNullOrWhiteSpace(game.SourceId)) return null;
        if (game.Source == GameSourceKind.Gog) return game.SourceId;
        if (game.Source == GameSourceKind.Heroic && game.SourceId.All(char.IsAsciiDigit)) return game.SourceId;
        return null;
    }

    private async Task<string?> FromGogAsync(Lookup lookup, CancellationToken ct)
    {
        var cfg = _config.Gog;
        var id = GogId(lookup.Game)!;

        if (!string.IsNullOrWhiteSpace(cfg.GamesDbUrl))
        {
            using var doc = await GetJsonAsync(lookup, "GOG gamesdb", Fill(cfg.GamesDbUrl, ("id", id)), ct);
            var format = doc is null ? null
                : Str(Prop(Prop(Prop(doc.RootElement, "game"), "vertical_cover"), "url_format"));
            if (format is not null)
            {
                // Try the configured size first, then the original image.
                foreach (var formatter in new[] { cfg.CoverFormatter, "" }.Distinct())
                {
                    var url = format.Replace("{formatter}", formatter).Replace("{ext}", cfg.CoverExtension);
                    var path = await DownloadAsync(lookup, "GOG gamesdb", url, "gog", ct);
                    if (path is not null) return path;
                }
            }
            else if (doc is not null) lookup.Log("GOG gamesdb: no vertical_cover for this game");
        }

        if (!string.IsNullOrWhiteSpace(cfg.ApiUrl))
        {
            using var doc = await GetJsonAsync(lookup, "GOG api", Fill(cfg.ApiUrl, ("id", id)), ct);
            var href = doc is null ? null : Str(Prop(Prop(Prop(doc.RootElement, "_links"), cfg.ApiImageLink), "href"));
            if (href is not null) return await DownloadAsync(lookup, "GOG api", href, "gog", ct);
            if (doc is not null) lookup.Log($"GOG api: no {cfg.ApiImageLink} link");
        }
        return null;
    }

    // ---------------- Epic ----------------

    private async Task<string?> FromEpicAsync(Lookup lookup, CancellationToken ct)
    {
        var epic = _epic.Value;
        if (epic.Problem is not null)
        {
            lookup.Log($"Epic: {epic.Problem}");
            return null;
        }

        var url = epic.FindImage(lookup.Game, _config.Epic.ImageTypes);
        if (url is null)
        {
            lookup.Log("Epic: no matching catalog entry or image");
            return null;
        }

        var query = _config.Epic.ImageQuery;
        if (!string.IsNullOrEmpty(query) && !url.Contains('?')) url += query;
        return await DownloadAsync(lookup, "Epic", url, "epic", ct);
    }

    // ---------------- Steam store search ----------------

    private async Task<string?> FromSteamSearchAsync(Lookup lookup, CancellationToken ct)
    {
        var cfg = _config.SteamSearch;
        if (string.IsNullOrWhiteSpace(cfg.SearchUrl) || cfg.ImageUrls.Count == 0) return null;

        using var doc = await GetJsonAsync(lookup, "Steam search", Fill(cfg.SearchUrl, ("name", SearchTerm(lookup.Game.Name))), ct);
        if (doc is null) return null;

        var items = Prop(doc.RootElement, "items");
        if (items is not { ValueKind: JsonValueKind.Array } list)
        {
            lookup.Log("Steam search: no results");
            return null;
        }

        var best = list.EnumerateArray()
            .Where(i => Str(Prop(i, "type")) == "app" && Str(Prop(i, "name")) is not null)
            .Select(i => (Id: Prop(i, "id")?.ToString(), Name: Str(Prop(i, "name"))!, Score: NameMatch.Similarity(lookup.Game.Name, Str(Prop(i, "name"))!)))
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (best.Id is null || best.Score < cfg.MinNameSimilarity)
        {
            lookup.Log(best.Id is null
                ? "Steam search: no results"
                : $"Steam search: closest was \"{best.Name}\" ({best.Score:0.00}), below {cfg.MinNameSimilarity:0.00}");
            return null;
        }

        lookup.Log($"Steam search: matched \"{best.Name}\" (app {best.Id}, {best.Score:0.00})");
        foreach (var template in cfg.ImageUrls)
        {
            var path = await DownloadAsync(lookup, "Steam CDN", Fill(template, ("appid", best.Id)), "steam", ct);
            if (path is not null) return path;
        }
        return null;
    }

    // ---------------- SteamGridDB ----------------

    private async Task<string?> FromSteamGridDbAsync(Lookup lookup, CancellationToken ct)
    {
        var cfg = _config.SteamGridDb;
        var key = _steamGridDbKey()?.Trim();
        if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(cfg.SearchUrl) || string.IsNullOrWhiteSpace(cfg.GridsUrl)) return null;
        var auth = new AuthenticationHeaderValue("Bearer", key);

        using var search = await GetJsonAsync(lookup, "SteamGridDB search", Fill(cfg.SearchUrl, ("name", SearchTerm(lookup.Game.Name))), ct, auth);
        if (search is null) return null;

        var best = Prop(search.RootElement, "data") is { ValueKind: JsonValueKind.Array } data
            ? data.EnumerateArray()
                .Select(g => (Id: Prop(g, "id")?.ToString(), Name: Str(Prop(g, "name")) ?? "", Score: NameMatch.Similarity(lookup.Game.Name, Str(Prop(g, "name")) ?? "")))
                .OrderByDescending(x => x.Score)
                .FirstOrDefault()
            : default;

        if (best.Id is null || best.Score < _config.SteamSearch.MinNameSimilarity)
        {
            lookup.Log(best.Id is null ? "SteamGridDB: no results" : $"SteamGridDB: closest was \"{best.Name}\" ({best.Score:0.00})");
            return null;
        }

        using var grids = await GetJsonAsync(lookup, "SteamGridDB grids", Fill(cfg.GridsUrl, ("id", best.Id)), ct, auth);
        var url = grids is not null && Prop(grids.RootElement, "data") is { ValueKind: JsonValueKind.Array } gridList
            ? gridList.EnumerateArray().Select(g => Str(Prop(g, "url"))).FirstOrDefault(u => u is not null)
            : null;

        if (url is null)
        {
            if (grids is not null) lookup.Log("SteamGridDB: no 600x900 grids");
            return null;
        }
        return await DownloadAsync(lookup, "SteamGridDB", url, "steamgriddb", ct);
    }

    // ---------------- HTTP helpers ----------------

    private async Task<JsonDocument?> GetJsonAsync(Lookup lookup, string label, string url, CancellationToken ct,
        AuthenticationHeaderValue? auth = null)
    {
        using var response = await SendAsync(lookup, label, url, ct, auth);
        if (response is null) return null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (JsonException)
        {
            lookup.Log($"{label}: reply wasn't JSON");
            return null;
        }
    }

    private async Task<string?> DownloadAsync(Lookup lookup, string label, string url, string sourceKey, CancellationToken ct)
    {
        using var response = await SendAsync(lookup, label, url, ct);
        if (response is null) return null;

        if (response.Content.Headers.ContentLength > MaxImageBytes)
        {
            lookup.Log($"{label}: image too large");
            return null;
        }

        byte[] bytes;
        try { bytes = await response.Content.ReadAsByteArrayAsync(ct); }
        catch (HttpRequestException) { lookup.Unreachable = true; lookup.Log($"{label}: download interrupted"); return null; }

        var ext = ImageExtension(bytes);
        if (ext is null || bytes.Length < 1024)
        {
            lookup.Log($"{label}: reply wasn't an image");
            return null;
        }

        Directory.CreateDirectory(_artworkDir);
        foreach (var old in Directory.EnumerateFiles(_artworkDir, $"{lookup.Game.Id}-{sourceKey}.*")) TryDelete(old);

        var path = Path.Combine(_artworkDir, $"{lookup.Game.Id}-{sourceKey}{ext}");
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, ct);
        File.Move(temp, path, overwrite: true);
        lookup.Log($"{label}: saved {Path.GetFileName(path)} ({bytes.Length / 1024} KB) from {url}");
        return path;
    }

    private async Task<HttpResponseMessage?> SendAsync(Lookup lookup, string label, string url, CancellationToken ct,
        AuthenticationHeaderValue? auth = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (auth is not null) request.Headers.Authorization = auth;

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            lookup.Unreachable = true;
            lookup.Log($"{label}: couldn't connect ({ex.HttpRequestError})");
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            lookup.Unreachable = true;
            lookup.Log($"{label}: timed out");
            return null;
        }

        lookup.GotResponse = true;
        if (response.IsSuccessStatusCode) return response;

        // Rate limits and server errors aren't a real "no art"; try again another time.
        var code = (int)response.StatusCode;
        if (code == 429 || code >= 500) lookup.Unreachable = true;
        lookup.Log($"{label}: HTTP {code} from {url}");
        response.Dispose();
        return null;
    }

    // ---------------- Small helpers ----------------

    private static string Fill(string template, params (string Key, string Value)[] values)
    {
        foreach (var (key, value) in values)
            template = template.Replace("{" + key + "}", Uri.EscapeDataString(value));
        return template;
    }

    private static JsonElement? Prop(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var p) ? p : null;

    private static string? Str(JsonElement? e) => e is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    private static string? ImageExtension(byte[] b)
    {
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
        if (b.Length > 12 && Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && Encoding.ASCII.GetString(b, 8, 4) == "WEBP") return ".webp";
        return null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The name without trademark symbols, which store searches tend to choke on.</summary>
    private static string SearchTerm(string name) =>
        name.Replace("™", "").Replace("®", "").Replace("©", "").Trim();

    private sealed class Lookup
    {
        public Lookup(GameInfo game, List<string> trace)
        {
            Game = game;
            Trace = trace;
        }

        public GameInfo Game { get; }
        public List<string> Trace { get; }
        public bool Unreachable { get; set; }
        public bool GotResponse { get; set; }
        public void Log(string line) => Trace.Add(line);
    }
}
