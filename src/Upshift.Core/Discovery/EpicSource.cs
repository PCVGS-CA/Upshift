using System.Text.Json;
using Upshift.Core.Models;

namespace Upshift.Core.Discovery;

public sealed class EpicSource : IGameSource
{
    public string DisplayName => "Epic Games";

    public IReadOnlyList<DiscoveredGame> Find(CancellationToken ct)
    {
        var results = new List<DiscoveredGame>();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) return results;

        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;

                var name = GetString(root, "DisplayName");
                var location = GetString(root, "InstallLocation");
                var exe = GetString(root, "LaunchExecutable");
                var appName = GetString(root, "AppName");
                if (name is null || location is null || !Directory.Exists(location)) continue;

                // Skip Unreal Engine installs and plugins; they are not games.
                if (appName?.StartsWith("UE_", StringComparison.OrdinalIgnoreCase) == true) continue;
                if (root.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array
                    && cats.EnumerateArray().Any(c => c.GetString() is "engines" or "plugins")) continue;
                if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) continue;

                var exePath = string.IsNullOrWhiteSpace(exe) ? null : Path.Combine(location, exe);
                results.Add(new DiscoveredGame(name, location, GameSourceKind.Epic, appName, exePath));
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        return results;
    }

    private static string? GetString(JsonElement e, string property) =>
        e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
