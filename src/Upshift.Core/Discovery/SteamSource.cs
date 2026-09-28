using Microsoft.Win32;
using Upshift.Core.Models;
using Upshift.Core.Util;

namespace Upshift.Core.Discovery;

public sealed class SteamSource : IGameSource
{
    public string DisplayName => "Steam";

    private static readonly string[] NotGames =
    {
        "Steamworks Common Redistributables", "Proton", "Steam Linux Runtime", "SteamVR"
    };

    public IReadOnlyList<DiscoveredGame> Find(CancellationToken ct)
    {
        var results = new List<DiscoveredGame>();
        var steamRoot = FindSteamRoot();
        if (steamRoot is null) return results;

        foreach (var library in FindLibraries(steamRoot))
        {
            ct.ThrowIfCancellationRequested();
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps)) continue;

            foreach (var manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                try
                {
                    var app = VdfParser.Parse(File.ReadAllText(manifest)).Child("AppState");
                    if (app is null) continue;

                    var appId = app.Value("appid");
                    var name = app.Value("name");
                    var installDir = app.Value("installdir");
                    if (appId is null || name is null || installDir is null) continue;
                    if (NotGames.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase))) continue;

                    var full = Path.Combine(steamapps, "common", installDir);
                    if (!Directory.Exists(full)) continue;

                    results.Add(new DiscoveredGame(name, full, GameSourceKind.Steam, appId,
                        ArtworkPath: FindArtwork(steamRoot, appId)));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return results;
    }

    private static string? FindSteamRoot()
    {
        try
        {
            using var user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (user?.GetValue("SteamPath") is string p && Directory.Exists(p)) return Path.GetFullPath(p);

            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                .OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (machine?.GetValue("InstallPath") is string m && Directory.Exists(m)) return m;
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> FindLibraries(string steamRoot)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return libraries;

        try
        {
            var folders = VdfParser.Parse(File.ReadAllText(vdf)).Child("libraryfolders");
            if (folders is null) return libraries;
            foreach (var entry in folders.Values.OfType<Dictionary<string, object>>())
            {
                var path = entry.Value("path");
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) libraries.Add(path);
            }
        }
        catch { }
        return libraries;
    }

    /// <summary>Steam keeps cover art for installed games in its local cache.</summary>
    private static string? FindArtwork(string steamRoot, string appId)
    {
        try
        {
            var cache = Path.Combine(steamRoot, "appcache", "librarycache");
            var flat = Path.Combine(cache, $"{appId}_library_600x900.jpg");
            if (File.Exists(flat)) return flat;

            var perApp = Path.Combine(cache, appId);
            if (Directory.Exists(perApp))
            {
                return Directory.EnumerateFiles(perApp, "library_600x900*.jpg", SearchOption.AllDirectories).FirstOrDefault()
                    ?? Directory.EnumerateFiles(perApp, "library_capsule*.jpg", SearchOption.AllDirectories).FirstOrDefault();
            }
        }
        catch { }
        return null;
    }
}
