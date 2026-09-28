using Upshift.Core.Detection;
using Upshift.Core.Models;
using Upshift.Core.Util;

namespace Upshift.Core.Discovery;

/// <summary>
/// Walks every fixed drive looking for game fingerprints, so games from anywhere
/// (copied folders, standalone installers, other launchers) still show up.
/// </summary>
public sealed class DriveScanSource
{
    private const int MaxDepth = 10;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.System
    };

    /// <summary>Folder names that are part of a game's layout, not the game's own folder.</summary>
    private static readonly HashSet<string> InnerFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin64", "x64", "win64", "win32", "binaries", "retail", "_retail_", "game", "release", "runtime"
    };

    /// <summary>Folders that hold many games; a game root is never one of these.</summary>
    private static readonly HashSet<string> ContainerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Games", "SteamLibrary", "Program Files", "Program Files (x86)", "common", "steamapps",
        "XboxGames", "Epic Games", "GOG Games", "GOG Galaxy", "Ubisoft Game Launcher", "EA Games"
    };

    public IReadOnlyList<DiscoveredGame> Find(CancellationToken ct, IProgress<string>? progress)
    {
        var results = new List<DiscoveredGame>();
        var found = new List<string>(); // normalized roots

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            }
            catch { continue; }

            ScanTree(drive.RootDirectory.FullName, results, found, ct, progress);
        }
        return results;
    }

    private static void ScanTree(string start, List<DiscoveredGame> results, List<string> found,
        CancellationToken ct, IProgress<string>? progress)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((start, 0));
        var visited = 0;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();

            var norm = PathUtil.Normalize(dir);
            if (found.Any(root => PathUtil.IsSameOrInside(norm, root))) continue;

            if (++visited % 400 == 0) progress?.Report($"Scanning drives… {Shorten(dir)}");

            List<string> files;
            List<string> subdirs;
            try
            {
                files = Directory.EnumerateFiles(dir, "*", Options).ToList();
                subdirs = Directory.EnumerateDirectories(dir, "*", Options).ToList();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (files.Any(f => Fingerprints.IsGameMarker(Path.GetFileName(f))))
            {
                var root = ResolveGameRoot(dir);
                var rootNorm = PathUtil.Normalize(root);
                if (!found.Any(r => PathUtil.IsSameOrInside(rootNorm, r)))
                {
                    found.Add(rootNorm);
                    results.Add(new DiscoveredGame(PathUtil.PrettyName(root), root, GameSourceKind.DriveScan));
                }
                continue; // never walk inside a game we've already found
            }

            if (depth >= MaxDepth) continue;
            foreach (var sub in subdirs)
            {
                if (Fingerprints.SkipOnDriveScan(Path.GetFileName(sub))) continue;
                stack.Push((sub, depth + 1));
            }
        }
    }

    /// <summary>From the folder where a fingerprint was found, walk up to the game's own folder.</summary>
    public static string ResolveGameRoot(string markerDir)
    {
        var parts = markerDir.TrimEnd('\\').Split('\\');

        // Unreal layout: <Root>\<Project>\Binaries\Win64, <Root>\Engine\..., <Root>\<Project>\Plugins\...
        for (var i = 1; i < parts.Length; i++)
        {
            var seg = parts[i];
            if (seg.Equals("Engine", StringComparison.OrdinalIgnoreCase))
                return Guard(string.Join('\\', parts[..i]), markerDir);

            if (seg.Equals("Binaries", StringComparison.OrdinalIgnoreCase)
                || seg.Equals("Plugins", StringComparison.OrdinalIgnoreCase))
            {
                var project = string.Join('\\', parts[..i]);
                var parent = Path.GetDirectoryName(project);
                var root = parent is not null && Directory.Exists(Path.Combine(parent, "Engine")) ? parent : project;
                return Guard(root, markerDir);
            }
        }

        // Everything else: step up past folders like bin\x64.
        var current = markerDir.TrimEnd('\\');
        while (InnerFolderNames.Contains(Path.GetFileName(current)))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null || Path.GetPathRoot(parent) == parent) break;
            current = parent;
        }
        return Guard(current, markerDir);
    }

    private static string Guard(string root, string fallback)
    {
        if (string.IsNullOrEmpty(root) || Path.GetPathRoot(root) == root + "\\" || Path.GetPathRoot(root) == root)
            return fallback;
        return ContainerNames.Contains(Path.GetFileName(root)) ? fallback : root;
    }

    private static string Shorten(string path) =>
        path.Length <= 70 ? path : path[..30] + "…" + path[^35..];
}
