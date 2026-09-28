namespace Upshift.Core.Util;

public readonly record struct WalkEntry(string Path, bool IsDirectory, int Depth);

/// <summary>
/// Walks a folder tree without following junctions/symlinks, skipping folders we can't read,
/// and stopping after a set depth or entry count so a huge folder can't stall a scan.
/// </summary>
public static class FileWalker
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline
    };

    public static IEnumerable<WalkEntry> Walk(
        string root,
        int maxDepth,
        int maxEntries,
        CancellationToken ct,
        Func<string, bool>? skipDirectoryName = null)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        var count = 0;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();

            List<string> files;
            List<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(dir, "*", Options).ToList();
                dirs = Directory.EnumerateDirectories(dir, "*", Options).ToList();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                if (++count > maxEntries) yield break;
                yield return new WalkEntry(file, false, depth);
            }

            foreach (var sub in dirs)
            {
                if (++count > maxEntries) yield break;
                yield return new WalkEntry(sub, true, depth + 1);

                if (depth + 1 > maxDepth) continue;
                if (skipDirectoryName?.Invoke(Path.GetFileName(sub)) == true) continue;
                stack.Push((sub, depth + 1));
            }
        }
    }
}
