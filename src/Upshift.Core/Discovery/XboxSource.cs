using System.Text;
using System.Xml.Linq;
using Upshift.Core.Models;
using Upshift.Core.Util;

namespace Upshift.Core.Discovery;

/// <summary>
/// Xbox app / Game Pass games installed to an "XboxGames" folder (the default since 2021).
/// Older installs inside the locked WindowsApps folder can't be modified and are skipped.
/// </summary>
public sealed class XboxSource : IGameSource
{
    public string DisplayName => "Xbox app";

    public IReadOnlyList<DiscoveredGame> Find(CancellationToken ct)
    {
        var results = new List<DiscoveredGame>();

        foreach (var root in FindGamingRoots())
        {
            IEnumerable<string> folders;
            try { folders = Directory.EnumerateDirectories(root).ToList(); }
            catch { continue; }

            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                var content = Path.Combine(folder, "Content");
                if (!Directory.Exists(content)) continue;

                var (name, exe) = ReadConfig(content);
                results.Add(new DiscoveredGame(name ?? PathUtil.PrettyName(folder), content, GameSourceKind.Xbox,
                    ExePath: exe));
            }
        }
        return results;
    }

    private static IEnumerable<string> FindGamingRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var driveRoot = drive.RootDirectory.FullName;

                // .GamingRoot = "RGBX" + 4 bytes + UTF-16 folder paths separated by nulls.
                var marker = Path.Combine(driveRoot, ".GamingRoot");
                if (File.Exists(marker))
                {
                    var bytes = File.ReadAllBytes(marker);
                    if (bytes.Length > 8 && bytes[0] == 'R' && bytes[1] == 'G' && bytes[2] == 'B' && bytes[3] == 'X')
                    {
                        var text = Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8);
                        foreach (var part in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var candidate = Path.Combine(driveRoot, part.TrimStart('\\'));
                            if (Directory.Exists(candidate)) roots.Add(candidate);
                        }
                    }
                }

                var fallback = Path.Combine(driveRoot, "XboxGames");
                if (Directory.Exists(fallback)) roots.Add(fallback);
            }
            catch { }
        }
        return roots;
    }

    /// <summary>MicrosoftGame.config holds the display name and the main executable.</summary>
    private static (string? Name, string? Exe) ReadConfig(string content)
    {
        try
        {
            var file = Path.Combine(content, "MicrosoftGame.config");
            if (!File.Exists(file)) return (null, null);

            var doc = XDocument.Load(file);
            var name = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals")
                ?.Attribute("DefaultDisplayName")?.Value;
            if (name is not null && name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) name = null;

            var exeName = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Executable")
                ?.Attribute("Name")?.Value;
            var exe = exeName is null ? null : Path.Combine(content, exeName);
            return (name, exe is not null && File.Exists(exe) ? exe : null);
        }
        catch
        {
            return (null, null);
        }
    }
}
