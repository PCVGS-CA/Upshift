using System.Text;
using System.Text.RegularExpressions;
using Upshift.Core.Catalog;
using Upshift.Core.Util;

namespace Upshift.Core.Wiki;

/// <summary>One row of the OptiScaler wiki's Compatibility List.</summary>
public sealed record CompatEntry(string Name, string? Page, string Status, string Inputs, bool OptiPatcher, string Notes);

/// <summary>A game's own wiki page (AsciiDoc table: Last Tested Version, Filename, Upscaler Inputs, FG…, Known Issues, Notes).</summary>
public sealed class GamePage
{
    public string Page { get; set; } = "";
    public string? LastTested { get; set; }
    public List<string> Filenames { get; set; } = new();
    public string? UpscalerInputs { get; set; }
    /// <summary>"FG Inputs" / "FG-Settings" bullets.</summary>
    public List<string> FrameGen { get; set; } = new();
    public List<string> Settings { get; set; } = new();
    public List<string> KnownIssues { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

/// <summary>
/// Reads the OptiScaler wiki as raw text from its git repo (never the HTML), caches every file and refreshes it
/// after RefreshDays. When offline, the cached copy is used. The Compatibility List is markdown; game pages are AsciiDoc.
/// </summary>
public sealed partial class OptiScalerWikiClient : IDisposable
{
    private readonly OptiScalerWikiSource _config;
    private readonly string _cacheDir;
    private readonly HttpClient _http;

    public OptiScalerWikiClient(OptiScalerWikiSource config, string dataDir)
    {
        _config = config;
        _cacheDir = Path.Combine(dataDir, "optiscaler-wiki");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);
    }

    public void Dispose() => _http.Dispose();

    public string? WebPageUrl(string? page) =>
        page is null ? _config.WebListUrl : _config.WebPageUrl?.Replace("{page}", Uri.EscapeDataString(page).Replace("%2D", "-"));

    public async Task<IReadOnlyList<CompatEntry>> GetListAsync(CancellationToken ct)
    {
        var text = await GetCachedAsync("Compatibility-List.md", _config.CompatibilityListUrl, ct);
        return text is null ? Array.Empty<CompatEntry>() : ParseCompatibilityList(text);
    }

    /// <summary>Only uses the cache (no network), for showing suggestions straight away.</summary>
    public IReadOnlyList<CompatEntry> GetCachedList()
    {
        var path = Path.Combine(_cacheDir, "Compatibility-List.md");
        return File.Exists(path) ? ParseCompatibilityList(File.ReadAllText(path)) : Array.Empty<CompatEntry>();
    }

    public async Task<GamePage?> GetPageAsync(string page, CancellationToken ct)
    {
        var text = await GetCachedAsync(Path.Combine("pages", SafeName(page) + ".asciidoc"),
            _config.PageUrl?.Replace("{page}", Uri.EscapeDataString(page)), ct);
        return text is null ? null : ParsePage(page, text);
    }

    public GamePage? GetCachedPage(string page)
    {
        var path = Path.Combine(_cacheDir, "pages", SafeName(page) + ".asciidoc");
        return File.Exists(path) && new FileInfo(path).Length > 0 ? ParsePage(page, File.ReadAllText(path)) : null;
    }

    /// <summary>The cached text when it's fresh, else a new download (falling back to the old copy when offline).</summary>
    private async Task<string?> GetCachedAsync(string relative, string? url, CancellationToken ct)
    {
        var path = Path.Combine(_cacheDir, relative);
        var fresh = File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(Math.Max(1, _config.RefreshDays));
        if (fresh) return ReadOrNull(path);
        if (url is null) return ReadOrNull(path);

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Remember "no such page" for a week too (an empty file).
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "", ct);
                return null;
            }
            if (!response.IsSuccessStatusCode) return ReadOrNull(path);
            var text = await response.Content.ReadAsStringAsync(ct);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path + ".tmp", text, ct);
            File.Move(path + ".tmp", path, overwrite: true);
            return text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return ReadOrNull(path);
        }
    }

    private static string? ReadOrNull(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0 ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
    }

    private static string SafeName(string page) =>
        string.Concat(page.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // ---------------- Compatibility List (markdown table) ----------------

    public static List<CompatEntry> ParseCompatibilityList(string markdown)
    {
        var entries = new List<CompatEntry>();
        var inTable = false;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("| Game |", StringComparison.OrdinalIgnoreCase)) { inTable = true; continue; }
            if (!inTable) continue;
            // The table ends at the first blank line. Rows missing their leading "|" still render on GitHub, so accept them.
            if (line.Length == 0) { if (entries.Count > 0) break; continue; }
            if (!line.Contains('|')) continue;
            if (line.StartsWith("| ----") || line.StartsWith("|---")) continue;

            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToList();
            if (cells.Count < 5) continue;

            var nameCell = cells[0];
            string name, page = null!;
            var link = CellLink().Match(nameCell);
            if (link.Success)
            {
                name = link.Groups[1].Value.Trim();
                page = Uri.UnescapeDataString(link.Groups[2].Value.Trim());
                if (page.StartsWith("http", StringComparison.OrdinalIgnoreCase)) page = page[(page.LastIndexOf('/') + 1)..];
                if (page.Contains('#')) page = page[..page.IndexOf('#')];
            }
            else name = nameCell;

            var status = cells[1].Contains('❌') ? "not working" : cells[1].Contains('✅') ? "working" : "partly working";
            entries.Add(new CompatEntry(StripMarkdown(name), string.IsNullOrWhiteSpace(page) ? null : page, status,
                StripMarkdown(cells[2]), cells[3].Contains('✨'), StripMarkdown(cells[4])));
        }
        return entries;
    }

    private static string StripMarkdown(string s)
    {
        s = MdLink().Replace(s, "$1");
        s = s.Replace("<br>", " ").Replace("**", "").Replace("`", "");
        s = Regex.Replace(s, @"(?<!\w)_|_(?!\w)", "");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    // ---------------- Game pages (AsciiDoc) ----------------

    public static GamePage ParsePage(string page, string asciidoc)
    {
        var result = new GamePage { Page = page };
        string? field = null;
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in asciidoc.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var label = FieldLabel().Match(line);
            if (label.Success)
            {
                field = label.Groups[1].Value.Trim();
                values[field] = new List<string>();
                continue;
            }
            if (field is null || line.StartsWith("|===") || line.StartsWith("[cols")) continue;

            var text = line.StartsWith("a|") ? line[2..] : line.StartsWith('|') ? line[1..] : line;
            if (string.IsNullOrWhiteSpace(text) || text.Trim() is "-" or "+" or "---") continue;
            if (text.TrimStart().StartsWith('.')) continue; // block titles like ".For example:"

            var bullet = BulletPrefix().Match(text);
            var depth = bullet.Success ? bullet.Groups[1].Value.Length : 0;
            var clean = CleanAsciiDoc(bullet.Success ? text[bullet.Length..] : text);
            if (clean.Length == 0) continue;

            var list = values[field];
            var continuation = !bullet.Success && !line.StartsWith('|') && !line.StartsWith("a|");
            // Nested bullets and plain continuation lines ("or") belong to the line above.
            if (list.Count > 0 && depth > 1) list[^1] = $"{list[^1]} — {clean}";
            else if (list.Count > 0 && continuation) list[^1] = $"{list[^1]} {clean}";
            else list.Add(clean);
        }

        List<string> Get(params string[] names) =>
            names.SelectMany(n => values.TryGetValue(n, out var v) ? v : new List<string>()).ToList();

        result.LastTested = Get("Last Tested Version").FirstOrDefault()?.TrimStart('v', 'V');
        result.Filenames = Get("Filename").SelectMany(f => Regex.Matches(f, @"[\w.]+\.(?:dll|asi)", RegexOptions.IgnoreCase).Select(m => m.Value)).ToList();
        result.UpscalerInputs = Get("Upscaler Inputs").FirstOrDefault();
        result.FrameGen = Get("FG Inputs", "FG-Settings", "FG Settings");
        result.Settings = Get("Settings", "Game Settings");
        result.KnownIssues = Get("Known Issues");
        result.Notes = Get("Notes");
        return result;
    }

    /// <summary>AsciiDoc markup to plain text. Struck-through text is dropped: it's advice the wiki has withdrawn.</summary>
    public static string CleanAsciiDoc(string s)
    {
        s = Regex.Replace(s, @"\+\+\+<s>.*?</s>\+\+\+", "", RegexOptions.Singleline);
        s = s.Replace("+++", "");
        s = AdocLink().Replace(s, m => m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[1].Value);

        // Keep `code` intact (file names like libxess_fg.dll); strip *bold* and _italic_ elsewhere.
        var parts = s.Split('`');
        var sb = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 1) { sb.Append(parts[i]); continue; }
            sb.Append(Regex.Replace(parts[i], @"(?<![\w])[*_]+|[*_]+(?![\w])", ""));
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim().Trim('-').Trim();
    }

    [GeneratedRegex(@"^\[(.+?)\]\((.+?)\)")]
    private static partial Regex CellLink();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MdLink();

    [GeneratedRegex(@"^\|\*\*(.+?)\*\*\s*$")]
    private static partial Regex FieldLabel();

    [GeneratedRegex(@"^\s*(\*+)\s+")]
    private static partial Regex BulletPrefix();

    [GeneratedRegex(@"(https?://\S+?)\[([^\]]*)\]")]
    private static partial Regex AdocLink();
}

/// <summary>Matches library games to Compatibility List entries by name.</summary>
public static partial class WikiMatcher
{
    private static readonly string[] EditionWords =
    {
        "game of the year edition", "goty edition", "goty", "complete edition", "definitive edition", "enhanced edition",
        "deluxe edition", "ultimate edition", "gold edition", "standard edition", "directors cut", "director s cut"
    };

    /// <summary>Lower case, no punctuation, no "(2024)"-style brackets, no trademark signs, no edition words.</summary>
    public static string Key(string name)
    {
        var s = Regex.Replace(name, @"\([^)]*\)", " ");
        s = s.Replace("™", "").Replace("®", "").Replace("©", "").Replace("&", " and ");
        s = NameMatch.Tidy(s);
        foreach (var words in EditionWords)
            s = Regex.Replace(s, $@"\b{Regex.Escape(words)}\b", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Exact name match first. Otherwise, when the game's name starts with an entry's name (at least three words, and
    /// what follows isn't a number: "Dino Crisis 2" never matches "Dino Crisis"), the longest such entry. Last, a very close spelling.
    /// </summary>
    public static CompatEntry? Match(string gameName, IReadOnlyList<CompatEntry> entries)
    {
        var key = Key(gameName);
        if (key.Length == 0) return null;
        var keyed = entries.Select(e => (Entry: e, Key: Key(e.Name))).Where(e => e.Key.Length > 0).ToList();

        var exact = keyed.Where(e => e.Key == key).Select(e => e.Entry).OrderByDescending(e => e.Page is not null).FirstOrDefault();
        if (exact is not null) return exact;

        var prefix = keyed
            .Where(e => e.Key.Split(' ').Length >= 3 && key.StartsWith(e.Key + " ", StringComparison.Ordinal))
            .Where(e => !char.IsDigit(key[e.Key.Length + 1]) && !RomanNumeral().IsMatch(key[(e.Key.Length + 1)..]))
            .OrderByDescending(e => e.Key.Length)
            .Select(e => e.Entry)
            .FirstOrDefault();
        if (prefix is not null) return prefix;

        var close = keyed.Select(e => (e.Entry, Score: NameMatch.Similarity(key, e.Key)))
            .Where(e => e.Score >= 0.92).OrderByDescending(e => e.Score).FirstOrDefault();
        return close.Entry;
    }

    [GeneratedRegex(@"^(i|ii|iii|iv|v|vi|vii|viii|ix|x)(\s|$)")]
    private static partial Regex RomanNumeral();
}
