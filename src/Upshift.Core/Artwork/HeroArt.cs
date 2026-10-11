using Microsoft.Win32;
using Upshift.Core.Models;

namespace Upshift.Core.Artwork;

/// <summary>
/// Wide art for the game page's header. Steam games only: Steam's own copy in its library cache
/// (appcache\librarycache\&lt;appid&gt;\…\library_hero.jpg) when it's there, otherwise Steam's CDN by app id, saved in
/// Upshift's artwork folder. Other games get null, and the page shows the blurred cover instead.
/// </summary>
public static class HeroArt
{
    private static readonly string[] CdnUrls =
    {
        "https://shared.steamstatic.com/store_item_assets/steam/apps/{0}/library_hero.jpg",
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/library_hero.jpg"
    };

    /// <summary>The wide art already on this PC (Steam's cache or an earlier download), without going online.</summary>
    public static string? FindLocal(GameInfo game, string artworkDir)
    {
        if (SteamAppId(game) is not { } appId) return null;
        try
        {
            var saved = SavedPath(artworkDir, game);
            if (File.Exists(saved)) return saved;
            if (SteamRoot() is not { } steam) return null;
            var cache = Path.Combine(steam, "appcache", "librarycache");
            var folder = Path.Combine(cache, appId);
            if (Directory.Exists(folder))
            {
                var direct = Path.Combine(folder, "library_hero.jpg");
                if (File.Exists(direct)) return direct;
                // Newer Steam keeps each picture in a folder named by its hash.
                var nested = Directory.EnumerateFiles(folder, "library_hero.jpg", SearchOption.AllDirectories).FirstOrDefault();
                if (nested is not null) return nested;
            }
            var flat = Path.Combine(cache, $"{appId}_library_hero.jpg");
            return File.Exists(flat) ? flat : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Downloads Steam's wide art once (a miss is remembered for a week). Null when there's none.</summary>
    public static async Task<string?> DownloadAsync(GameInfo game, string artworkDir, HttpClient http, CancellationToken ct)
    {
        if (SteamAppId(game) is not { } appId) return null;
        var saved = SavedPath(artworkDir, game);
        var miss = saved + ".miss";
        try
        {
            if (File.Exists(saved)) return saved;
            if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < TimeSpan.FromDays(7)) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            foreach (var template in CdnUrls)
            {
                using var response = await http.GetAsync(string.Format(template, appId), ct);
                if (!response.IsSuccessStatusCode) continue;
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length < 1024 || bytes[0] != 0xFF || bytes[1] != 0xD8) continue;   // a JPEG, not an error page
                await File.WriteAllBytesAsync(saved + ".tmp", bytes, ct);
                File.Move(saved + ".tmp", saved, overwrite: true);
                return saved;
            }
            await File.WriteAllTextAsync(miss, "", ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException) { }
        return null;
    }

    private static string SavedPath(string artworkDir, GameInfo game) =>
        Path.Combine(artworkDir, "hero", string.Concat(game.Id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".jpg");

    private static string? SteamAppId(GameInfo game) =>
        game.Source == GameSourceKind.Steam && game.SourceId is { Length: > 0 } id && id.All(char.IsAsciiDigit) ? id : null;

    private static string? SteamRoot()
    {
        try
        {
            using var user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (user?.GetValue("SteamPath") is string p && Directory.Exists(p)) return Path.GetFullPath(p);
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (machine?.GetValue("InstallPath") is string m && Directory.Exists(m)) return m;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return null;
    }
}
