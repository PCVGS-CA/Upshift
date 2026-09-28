using System.Text.Json;
using Upshift.Core.Hardware;
using Upshift.Core.Models;

namespace Upshift.Core.Wiki;

/// <summary>The user's answer to "Wrong game?": a different wiki entry, or suggestions turned off for the game.</summary>
public sealed class WikiMatchChoice
{
    public string? EntryName { get; set; }
    public bool Off { get; set; }
}

/// <summary>
/// Suggestions for every game, from the cached OptiScaler wiki (no network when showing them). RefreshAsync updates
/// the Compatibility List and the pages of matched games in the background (weekly, see OptiScalerWikiSource.RefreshDays).
/// </summary>
public sealed class SuggestionService
{
    private readonly OptiScalerWikiClient _client;
    private readonly string _choicesPath;
    private readonly Func<IReadOnlyDictionary<string, List<string>>> _iniKeys;
    private readonly object _sync = new();
    private Dictionary<string, WikiMatchChoice> _choices;
    private IReadOnlyList<CompatEntry> _entries;

    public SuggestionService(OptiScalerWikiClient client, string dataDir, Func<IReadOnlyDictionary<string, List<string>>> iniKeys)
    {
        _client = client;
        _iniKeys = iniKeys;
        _choicesPath = Path.Combine(dataDir, "wiki-matches.json");
        _choices = LoadChoices();
        _entries = client.GetCachedList();
    }

    public IReadOnlyList<CompatEntry> Entries
    {
        get { lock (_sync) return _entries; }
    }

    public WikiMatchChoice? ChoiceFor(string gameId)
    {
        lock (_sync) return _choices.GetValueOrDefault(gameId);
    }

    /// <summary>Remembers the user's pick (an entry name), "off", or null to go back to automatic matching.</summary>
    public void SetChoice(string gameId, WikiMatchChoice? choice)
    {
        lock (_sync)
        {
            if (choice is null) _choices.Remove(gameId);
            else _choices[gameId] = choice;
            try { File.WriteAllText(_choicesPath, JsonSerializer.Serialize(_choices, new JsonSerializerOptions { WriteIndented = true })); }
            catch (IOException) { }
        }
    }

    /// <summary>The matched wiki entry, honouring the user's choice. Null when there's none or suggestions are off.</summary>
    public CompatEntry? EntryFor(GameInfo game)
    {
        var choice = ChoiceFor(game.Id);
        if (choice?.Off == true) return null;
        var entries = Entries;
        if (choice?.EntryName is { } name) return entries.FirstOrDefault(e => e.Name == name);
        return WikiMatcher.Match(game.Name, entries);
    }

    public bool SuggestionsOff(string gameId) => ChoiceFor(gameId)?.Off == true;

    /// <summary>The card for a game, from cached data only.</summary>
    public GameSuggestions For(GameInfo game, GpuInfo? gpu)
    {
        var entry = EntryFor(game);
        if (entry is null) return SuggestionBuilder.General(game);
        var page = entry.Page is null ? null : _client.GetCachedPage(entry.Page);
        return SuggestionBuilder.FromWiki(entry, page, game, gpu, _iniKeys(), _client.WebPageUrl(entry.Page));
    }

    /// <summary>Refreshes the list and the pages of the given games when the cache is older than a week. Quiet when offline.</summary>
    public async Task RefreshAsync(IEnumerable<GameInfo> games, CancellationToken ct)
    {
        var list = await _client.GetListAsync(ct);
        if (list.Count > 0) lock (_sync) _entries = list;

        foreach (var game in games)
        {
            ct.ThrowIfCancellationRequested();
            if (EntryFor(game)?.Page is { } page) await _client.GetPageAsync(page, ct);
        }
    }

    private Dictionary<string, WikiMatchChoice> LoadChoices()
    {
        try
        {
            return File.Exists(_choicesPath)
                ? JsonSerializer.Deserialize<Dictionary<string, WikiMatchChoice>>(File.ReadAllText(_choicesPath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return new(); }
    }
}

/// <summary>Per-game install preferences, e.g. the loading name a suggestion picked for the next install.</summary>
public sealed class InstallPreferences
{
    private readonly string _path;
    private readonly Dictionary<string, string> _proxy;

    public InstallPreferences(string dataDir)
    {
        _path = Path.Combine(dataDir, "install-preferences.json");
        try
        {
            _proxy = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { _proxy = new(); }
    }

    public string? ProxyFor(string gameId) => _proxy.GetValueOrDefault(gameId);

    public void SetProxy(string gameId, string? proxy)
    {
        if (proxy is null) _proxy.Remove(gameId);
        else _proxy[gameId] = proxy;
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_proxy, new JsonSerializerOptions { WriteIndented = true })); }
        catch (IOException) { }
    }
}
