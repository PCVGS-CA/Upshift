using System.Text.Json;
using Upshift.Core.Catalog;
using Upshift.Core.Models;
using Upshift.Core.Util;

namespace Upshift.Core.Artwork;

/// <summary>
/// The Epic Games Launcher's own offline catalog (catcache.bin, base64-encoded JSON) plus the install manifests (.item)
/// that link each installed game to its catalog entry.
/// </summary>
internal sealed class EpicCatalog
{
    private readonly Dictionary<string, (string ItemId, string? Namespace)> _byAppName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string ItemId, string? Namespace)> _byInstallDir = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string? Namespace, Dictionary<string, string> Images)>> _items = new(StringComparer.OrdinalIgnoreCase);

    public string? Problem { get; private set; }

    public static EpicCatalog Load(EpicArtworkSource cfg)
    {
        var catalog = new EpicCatalog();
        var cachePath = Expand(cfg.CatalogCachePath);
        var manifests = Expand(cfg.ManifestsPath);

        if (cachePath is null || !File.Exists(cachePath))
        {
            catalog.Problem = "catcache.bin not found";
            return catalog;
        }

        try
        {
            if (manifests is not null && Directory.Exists(manifests))
                foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
                    catalog.ReadManifest(file);

            var json = Convert.FromBase64String(File.ReadAllText(cachePath).Trim());
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                catalog.Problem = "catcache.bin isn't in the expected format";
                return catalog;
            }

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var id = Str(entry, "id");
                if (id is null || !entry.TryGetProperty("keyImages", out var images) || images.ValueKind != JsonValueKind.Array) continue;

                var byType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var image in images.EnumerateArray())
                {
                    var type = Str(image, "type");
                    var url = Str(image, "url");
                    if (type is not null && url is not null) byType.TryAdd(type, url);
                }

                if (!catalog._items.TryGetValue(id, out var list)) catalog._items[id] = list = new();
                list.Add((Str(entry, "namespace"), byType));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            catalog.Problem = $"couldn't read the launcher catalog ({ex.GetType().Name})";
        }
        return catalog;
    }

    /// <summary>The first image URL of the wanted types for this game, or null.</summary>
    public string? FindImage(GameInfo game, IReadOnlyList<string> imageTypes)
    {
        (string ItemId, string? Namespace) link = default;
        var found = (game.SourceId is not null && _byAppName.TryGetValue(game.SourceId, out link))
                    || _byInstallDir.TryGetValue(PathUtil.Normalize(game.InstallDir), out link);
        if (!found || !_items.TryGetValue(link.ItemId, out var entries)) return null;

        var entry = entries.FirstOrDefault(e => link.Namespace is null || string.Equals(e.Namespace, link.Namespace, StringComparison.OrdinalIgnoreCase));
        if (entry.Images is null) return null;

        foreach (var type in imageTypes)
            if (entry.Images.TryGetValue(type, out var url)) return url;
        return null;
    }

    private void ReadManifest(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var itemId = Str(root, "CatalogItemId");
            if (itemId is null) return;

            var link = (itemId, Str(root, "CatalogNamespace"));
            if (Str(root, "AppName") is { } appName) _byAppName[appName] = link;
            if (Str(root, "InstallLocation") is { } location) _byInstallDir[PathUtil.Normalize(location)] = link;
        }
        catch (JsonException) { }
        catch (IOException) { }
    }

    private static string? Expand(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));

    private static string? Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
