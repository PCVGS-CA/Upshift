using System.Text.Json;
using Upshift.Core.Install;

namespace Upshift.Core.Services;

/// <summary>A game's history that outlasts its OptiScaler record: uninstalls and their results, and when Upshift last
/// started the game. Kept in history\&lt;game&gt;.json in Upshift's data folder.</summary>
public sealed class GameHistory
{
    public List<HistoryEntry> Entries { get; set; } = new();
    /// <summary>When Upshift last started the game (Play, or found it for Measure).</summary>
    public DateTime? LastLaunchUtc { get; set; }
}

public sealed class HistoryEntry
{
    public DateTime Utc { get; set; }
    public string Text { get; set; } = "";
    public string? Detail { get; set; }
}

public sealed class GameHistoryStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _dir;
    private readonly object _sync = new();

    public GameHistoryStore(string dataDir) => _dir = Path.Combine(dataDir, "history");

    private string PathFor(string gameId) =>
        Path.Combine(_dir, string.Concat(gameId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json");

    public GameHistory Get(string gameId)
    {
        lock (_sync)
        {
            try
            {
                var path = PathFor(gameId);
                return File.Exists(path) ? JsonSerializer.Deserialize<GameHistory>(File.ReadAllText(path), Json) ?? new() : new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
        }
    }

    public void Add(string gameId, string text, string? detail = null) =>
        Change(gameId, h => h.Entries.Add(new HistoryEntry { Utc = DateTime.UtcNow, Text = text, Detail = detail }));

    public void Launched(string gameId) => Change(gameId, h => h.LastLaunchUtc = DateTime.UtcNow);

    /// <summary>The entries as change-history lines.</summary>
    public IEnumerable<ChangeEntry> Changes(string gameId) => Get(gameId).Entries.Select(e => new ChangeEntry(e.Utc, e.Text, e.Detail));

    private void Change(string gameId, Action<GameHistory> change)
    {
        lock (_sync)
        {
            try
            {
                var h = Get(gameId);
                change(h);
                Directory.CreateDirectory(_dir);
                var path = PathFor(gameId);
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(h, Json));
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* history is a convenience */ }
        }
    }
}
