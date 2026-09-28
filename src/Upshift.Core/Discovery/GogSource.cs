using Microsoft.Win32;
using Upshift.Core.Models;

namespace Upshift.Core.Discovery;

public sealed class GogSource : IGameSource
{
    public string DisplayName => "GOG";

    public IReadOnlyList<DiscoveredGame> Find(CancellationToken ct)
    {
        var results = new List<DiscoveredGame>();
        try
        {
            using var games = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                .OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (games is null) return results;

            foreach (var id in games.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                using var key = games.OpenSubKey(id);
                var name = key?.GetValue("gameName") as string;
                var path = key?.GetValue("path") as string;
                var exe = key?.GetValue("exe") as string;
                if (name is null || path is null || !Directory.Exists(path)) continue;

                results.Add(new DiscoveredGame(name, path, GameSourceKind.Gog, id,
                    exe is not null && File.Exists(exe) ? exe : null));
            }
        }
        catch { }
        return results;
    }
}
