using System.Text.Json;
using Upshift.Core.Models;
using Upshift.Core.Util;

namespace Upshift.Core.Discovery;

/// <summary>Heroic Games Launcher (Epic and GOG libraries) and the itch.io app.</summary>
public sealed class HeroicAndItchSource : IGameSource
{
    public string DisplayName => "Heroic and itch.io";

    public IReadOnlyList<DiscoveredGame> Find(CancellationToken ct)
    {
        var results = new List<DiscoveredGame>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Heroic: Epic games (via Legendary)
        TryReadJson(Path.Combine(appData, "heroic", "legendaryConfig", "legendary", "installed.json"), root =>
        {
            if (root.ValueKind != JsonValueKind.Object) return;
            foreach (var game in root.EnumerateObject())
            {
                var v = game.Value;
                var path = Str(v, "install_path");
                if (path is null || !Directory.Exists(path)) continue;
                var exe = Str(v, "executable");
                results.Add(new DiscoveredGame(Str(v, "title") ?? PathUtil.PrettyName(path), path,
                    GameSourceKind.Heroic, game.Name, exe is null ? null : Path.Combine(path, exe)));
            }
        });

        // Heroic: GOG games
        TryReadJson(Path.Combine(appData, "heroic", "gog_store", "installed.json"), root =>
        {
            if (!root.TryGetProperty("installed", out var list) || list.ValueKind != JsonValueKind.Array) return;
            foreach (var v in list.EnumerateArray())
            {
                var path = Str(v, "install_path");
                if (path is null || !Directory.Exists(path)) continue;
                results.Add(new DiscoveredGame(PathUtil.PrettyName(path), path, GameSourceKind.Heroic, Str(v, "appName")));
            }
        });

        // itch.io app
        var itch = Path.Combine(appData, "itch", "apps");
        if (Directory.Exists(itch))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(itch))
                {
                    ct.ThrowIfCancellationRequested();
                    results.Add(new DiscoveredGame(PathUtil.PrettyName(dir), dir, GameSourceKind.Itch));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return results;
    }

    private static void TryReadJson(string file, Action<JsonElement> read)
    {
        if (!File.Exists(file)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            read(doc.RootElement);
        }
        catch (JsonException) { }
        catch (IOException) { }
    }

    private static string? Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
