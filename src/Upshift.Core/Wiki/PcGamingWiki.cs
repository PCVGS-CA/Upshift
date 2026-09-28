using System.Text.Json;
using System.Text.RegularExpressions;
using Upshift.Core.Catalog;
using Upshift.Core.Models;
using Upshift.Core.Util;

namespace Upshift.Core.Wiki;

public enum WikiStatus
{
    /// <summary>The game's page was read.</summary>
    Found,
    /// <summary>PCGamingWiki answered but has no page for this game.</summary>
    NoPage,
    /// <summary>PCGamingWiki couldn't be reached or gave an error. Not cached; tried again next time.</summary>
    Failed
}

/// <summary>What PCGamingWiki says about one game. Cached per game for a while (see PcGamingWikiSource.CacheDays).</summary>
public sealed class WikiEntry
{
    public WikiStatus Status { get; set; }
    public string? Title { get; set; }
    public string? PageUrl { get; set; }
    public string? Engine { get; set; }

    /// <summary>The infobox's "upscaling" value: "true", "false", "unknown", "n/a", "hackable" or empty.</summary>
    public string? Upscaling { get; set; }
    public List<string> UpscalingTech { get; set; } = new();
    public string? UpscalingNotes { get; set; }

    public string? FrameGen { get; set; }
    public List<string> FrameGenTech { get; set; } = new();
    public string? FrameGenNotes { get; set; }

    /// <summary>Why the lookup failed, in words a person can read.</summary>
    public string? Message { get; set; }
    public DateTime FetchedUtc { get; set; }
}

/// <summary>
/// Reads a game's PCGamingWiki page through the MediaWiki API (action=parse, wikitext), never the HTML,
/// so changes to the site's layout don't matter. One request per game when the name matches a page title;
/// otherwise one search request finds the page first.
/// </summary>
public sealed partial class PcGamingWikiClient : IDisposable
{
    private readonly PcGamingWikiSource _config;
    private readonly HttpClient _http;

    public PcGamingWikiClient(PcGamingWikiSource config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(5, config.TimeoutSeconds)) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>How many requests this client has sent, for diagnostics.</summary>
    public int RequestCount => _requests;
    private int _requests;

    private Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        return _http.GetAsync(url, ct);
    }

    /// <param name="knownTitle">The page title found last time, so no search is needed.</param>
    public async Task<WikiEntry> LookupAsync(GameInfo game, string? knownTitle, CancellationToken ct)
    {
        try
        {
            var title = knownTitle ?? game.Name.Replace("™", "").Replace("®", "").Trim();
            var (entry, missing) = await ParsePageAsync(title, ct);
            if (!missing || knownTitle is not null) return entry;

            // Launcher names often carry an edition ("... - Game of the Year Edition"); search for the closest page.
            var found = await SearchTitleAsync(game.Name, ct);
            if (found is null)
                return new WikiEntry { Status = WikiStatus.NoPage, Message = "No PCGamingWiki page with this game's name.", FetchedUtc = DateTime.UtcNow };
            return (await ParsePageAsync(found, ct)).Entry;
        }
        catch (HttpRequestException ex)
        {
            return Failed($"Couldn't reach PCGamingWiki ({ex.HttpRequestError}).");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed("PCGamingWiki didn't answer in time.");
        }
        catch (JsonException)
        {
            return Failed("PCGamingWiki sent a reply that couldn't be read.");
        }
    }

    private async Task<(WikiEntry Entry, bool Missing)> ParsePageAsync(string title, CancellationToken ct)
    {
        var url = _config.WikitextUrl!.Replace("{title}", Uri.EscapeDataString(title));
        using var response = await GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return (Failed($"PCGamingWiki answered HTTP {(int)response.StatusCode}."), false);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
            if (code == "missingtitle")
                return (new WikiEntry { Status = WikiStatus.NoPage, Message = "No PCGamingWiki page with this game's name.", FetchedUtc = DateTime.UtcNow }, true);
            return (Failed($"PCGamingWiki returned an error ({code})."), false);
        }

        var parse = root.GetProperty("parse");
        var pageTitle = parse.TryGetProperty("title", out var t) ? t.GetString() ?? title : title;
        var wikitext = parse.TryGetProperty("wikitext", out var w) ? w.GetString() ?? "" : "";
        return (FromWikitext(pageTitle, wikitext), false);
    }

    private async Task<string?> SearchTitleAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.SearchUrl)) return null;
        var term = NameMatch.Tidy(name);
        if (term.Length == 0) return null;

        using var response = await GetAsync(_config.SearchUrl.Replace("{name}", Uri.EscapeDataString(term)), ct);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("search", out var hits)
            || hits.ValueKind != JsonValueKind.Array) return null;

        // Ties (e.g. "X" and "X - Complete Edition" both tidy to "x"): the shorter title is the main page.
        var best = hits.EnumerateArray()
            .Select(h => h.TryGetProperty("title", out var ti) ? ti.GetString() : null)
            .Where(ti => !string.IsNullOrEmpty(ti))
            .Select(ti => (Title: ti!, Score: NameMatch.Similarity(name, ti!)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Title.Length)
            .FirstOrDefault();
        return best.Title is not null && best.Score >= _config.MinNameSimilarity ? best.Title : null;
    }

    private WikiEntry FromWikitext(string title, string wikitext)
    {
        var fields = InfoboxFields(wikitext);
        return new WikiEntry
        {
            Status = WikiStatus.Found,
            Title = title,
            PageUrl = (_config.PageUrl ?? "https://www.pcgamingwiki.com/wiki/{title}")
                .Replace("{title}", Uri.EscapeDataString(title.Replace(' ', '_')).Replace("%3A", ":")),
            Engine = EngineFromWikitext(wikitext),
            Upscaling = fields.GetValueOrDefault("upscaling"),
            UpscalingTech = TechList(fields.GetValueOrDefault("upscaling tech")),
            UpscalingNotes = NullIfEmpty(fields.GetValueOrDefault("upscaling notes")),
            FrameGen = fields.GetValueOrDefault("framegen"),
            FrameGenTech = TechList(fields.GetValueOrDefault("framegen tech")),
            FrameGenNotes = NullIfEmpty(fields.GetValueOrDefault("framegen notes")),
            FetchedUtc = DateTime.UtcNow
        };
    }

    private static WikiEntry Failed(string message) =>
        new() { Status = WikiStatus.Failed, Message = message, FetchedUtc = DateTime.UtcNow };

    // ---------------- Wikitext parsing ----------------

    private static readonly string[] WantedFields =
        { "upscaling", "upscaling tech", "upscaling notes", "framegen", "framegen tech", "framegen notes" };

    /// <summary>The first "|name = value" line for each wanted field (they live in the page's {{Video}} template).</summary>
    public static Dictionary<string, string> InfoboxFields(string wikitext)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in FieldLine().Matches(wikitext))
        {
            var name = Regex.Replace(m.Groups[1].Value.Trim(), @"\s+", " ");
            if (WantedFields.Contains(name, StringComparer.OrdinalIgnoreCase) && !result.ContainsKey(name))
                result[name] = CleanMarkup(m.Groups[2].Value);
        }
        return result;
    }

    /// <summary>"DLSS 3.5, FSR 3.0, XeSS 2, TSR" as a list, with consistent capitals ("fsr fg" becomes "FSR FG").</summary>
    public static List<string> TechList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(PrettyTech)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static readonly Dictionary<string, string> KnownWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dlss"] = "DLSS", ["dlaa"] = "DLAA", ["fsr"] = "FSR", ["xess"] = "XeSS", ["tsr"] = "TSR", ["taau"] = "TAAU",
        ["fg"] = "FG", ["mfg"] = "MFG", ["nis"] = "NIS", ["rsr"] = "RSR", ["afmf"] = "AFMF", ["pssr"] = "PSSR", ["metalfx"] = "MetalFX"
    };

    private static string PrettyTech(string token) =>
        string.Join(' ', token.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => KnownWords.TryGetValue(word, out var known) ? known : word));

    /// <summary>
    /// The first engine in the infobox, e.g. {{Infobox game/row/engine|REDengine|name=REDengine 3}} gives "REDengine 3"
    /// and {{Infobox game/row/engine|RenderWare}} gives "RenderWare".
    /// </summary>
    public static string? EngineFromWikitext(string wikitext)
    {
        var m = EngineRow().Match(wikitext);
        if (!m.Success) return null;

        var args = m.Groups[1].Value.Split('|').Select(a => a.Trim()).ToList();
        var name = args.FirstOrDefault(a => a.StartsWith("name=", StringComparison.OrdinalIgnoreCase))?[5..].Trim();
        var engine = string.IsNullOrEmpty(name) ? args.FirstOrDefault(a => a.Length > 0 && !a.Contains('=')) : name;
        return string.IsNullOrWhiteSpace(engine) ? null : engine;
    }

    /// <summary>Wiki links, references, simple templates and HTML tags turned into plain text.</summary>
    public static string CleanMarkup(string s)
    {
        s = RefTag().Replace(s, "");
        s = Template().Replace(s, "");
        s = PipedLink().Replace(s, "$1");
        s = PlainLink().Replace(s, "$1");
        s = s.Replace("<br>", " ").Replace("<br/>", " ").Replace("<br />", " ");
        s = HtmlTag().Replace(s, "");
        s = s.Replace("'''", "").Replace("''", "");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    [GeneratedRegex(@"^\s*\|\s*([a-z][a-z ]*?)\s*=[ \t]*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex FieldLine();

    [GeneratedRegex(@"\{\{\s*Infobox game/row/engine\s*\|([^}]*)\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex EngineRow();

    [GeneratedRegex(@"<ref[^>]*/>|<ref[^>]*>.*?</ref>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RefTag();

    [GeneratedRegex(@"\{\{[^{}]*\}\}")]
    private static partial Regex Template();

    [GeneratedRegex(@"\[\[[^\]|]*\|([^\]]*)\]\]")]
    private static partial Regex PipedLink();

    [GeneratedRegex(@"\[\[([^\]]*)\]\]")]
    private static partial Regex PlainLink();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();
}

/// <summary>PCGamingWiki answers per game id, kept in wiki-cache.json so rescans and restarts don't ask again.</summary>
public sealed class WikiCache
{
    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<string, WikiEntry> _entries;

    public WikiCache(string dataDir)
    {
        _path = Path.Combine(dataDir, "wiki-cache.json");
        _entries = Load();
    }

    public WikiEntry? Get(string gameId)
    {
        lock (_sync) return _entries.GetValueOrDefault(gameId);
    }

    public void Set(string gameId, WikiEntry entry)
    {
        lock (_sync) _entries[gameId] = entry;
    }

    public void Save()
    {
        string json;
        lock (_sync) json = JsonSerializer.Serialize(_entries, CatalogLoader.JsonOptions);
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException) { /* only a cache */ }
        catch (UnauthorizedAccessException) { }
    }

    private Dictionary<string, WikiEntry> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, WikiEntry>>(File.ReadAllText(_path), CatalogLoader.JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }
}
