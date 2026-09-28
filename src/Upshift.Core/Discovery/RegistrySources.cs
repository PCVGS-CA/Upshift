using Microsoft.Win32;
using Upshift.Core.Detection;
using Upshift.Core.Models;
using Upshift.Core.Util;

namespace Upshift.Core.Discovery;

/// <summary>EA app, Ubisoft Connect, Battle.net and any other installed program that looks like a game.</summary>
public sealed class RegistrySources : IGameSource
{
    public string DisplayName => "EA, Ubisoft, Battle.net and other installed games";

    public IReadOnlyList<DiscoveredGame> Find(CancellationToken ct)
    {
        var results = new List<DiscoveredGame>();
        var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);

        // EA app / Origin
        ForEachSubKey(hklm32, @"SOFTWARE\EA Games", (name, key) =>
        {
            if (key.GetValue("Install Dir") is string dir && Directory.Exists(dir))
                results.Add(new DiscoveredGame(name, dir.TrimEnd('\\'), GameSourceKind.EA, name));
        });

        // Ubisoft Connect
        ForEachSubKey(hklm32, @"SOFTWARE\Ubisoft\Launcher\Installs", (id, key) =>
        {
            if (key.GetValue("InstallDir") is string dir && Directory.Exists(dir))
            {
                dir = dir.Replace('/', '\\').TrimEnd('\\');
                results.Add(new DiscoveredGame(PathUtil.PrettyName(dir), dir, GameSourceKind.Ubisoft, id));
            }
        });

        // Everything in Programs and Features that looks like a game (Battle.net, Rockstar, standalone installers...).
        var uninstallRoots = new[]
        {
            (Hive: RegistryHive.LocalMachine, View: RegistryView.Registry64),
            (Hive: RegistryHive.LocalMachine, View: RegistryView.Registry32),
            (Hive: RegistryHive.CurrentUser, View: RegistryView.Default)
        };

        foreach (var (hive, view) in uninstallRoots)
        {
            ct.ThrowIfCancellationRequested();
            var baseKey = RegistryKey.OpenBaseKey(hive, view);
            ForEachSubKey(baseKey, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", (_, key) =>
            {
                ct.ThrowIfCancellationRequested();
                if (key.GetValue("InstallLocation") is not string dir || string.IsNullOrWhiteSpace(dir)) return;
                dir = dir.Trim('"').TrimEnd('\\');
                if (!Directory.Exists(dir) || dir.Length <= 3) return;

                var name = key.GetValue("DisplayName") as string ?? PathUtil.PrettyName(dir);
                var publisher = key.GetValue("Publisher") as string ?? "";
                if (!LooksLikeGameFolder(dir, ct)) return;

                var source = publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase)
                    ? GameSourceKind.BattleNet
                    : GameSourceKind.InstalledProgram;
                // Battle.net games are started by their uid ("--uid=prometheus" in the uninstall command).
                var uid = source == GameSourceKind.BattleNet ? Launch.GameLauncher.BattleNetUid(key.GetValue("UninstallString") as string) : null;
                results.Add(new DiscoveredGame(name, dir, source, uid));
            });
        }

        return results;
    }

    private static bool LooksLikeGameFolder(string dir, CancellationToken ct)
    {
        foreach (var entry in FileWalker.Walk(dir, 3, 3000, ct, Fingerprints.SkipInsideGame))
        {
            if (!entry.IsDirectory && Fingerprints.IsGameMarker(Path.GetFileName(entry.Path))) return true;
        }
        return false;
    }

    private static void ForEachSubKey(RegistryKey baseKey, string path, Action<string, RegistryKey> action)
    {
        try
        {
            using var parent = baseKey.OpenSubKey(path);
            if (parent is null) return;
            foreach (var name in parent.GetSubKeyNames())
            {
                try
                {
                    using var key = parent.OpenSubKey(name);
                    if (key is not null) action(name, key);
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
    }
}
