using System.Text.Json;
using SharpCompress.Archives;
using Upshift.Core.Catalog;
using Upshift.Core.Install;

namespace Upshift.Core.UserFiles;

public enum ReleaseMatch { Matches, Different, NotChecked }

/// <summary>Whether a file the user picked is the one from a release, and the sentence that says so.</summary>
public sealed record ReleaseFileResult(ReleaseMatch Match, string Message, string? ArchivePath = null);

/// <summary>
/// Checks a file the user picked (the modified FSR 4.1.1b DLL) against the release it should come from. The release
/// lists only its archive's SHA-256, and Upshift never downloads the file, so the check uses the archive the user
/// downloaded: found by name in the picked file's folder (and the two above it), Downloads or Desktop, confirmed by
/// size and SHA-256, then the DLL inside is compared with the picked one. A confirmed file's hash is remembered, so
/// picking it again later works without the archive.
/// </summary>
public static class ReleaseFileCheck
{
    private const string KnownName = "release-files.json";

    public static ReleaseFileResult Check(string pickedFile, UserFileRelease release, IEnumerable<string> searchFolders, string userFilesDir)
    {
        string picked;
        try { picked = OptiScalerInstaller.Sha256(pickedFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(ReleaseMatch.NotChecked, $"Upshift couldn't read the file: {ex.Message}"); }

        var known = LoadKnown(userFilesDir);
        if (known.TryGetValue(release.ArchiveSha256, out var knownSha))
        {
            if (knownSha.Equals(picked, StringComparison.OrdinalIgnoreCase))
                return new(ReleaseMatch.Matches, $"This is the {release.FileName} from the {release.Name} release (checked earlier against its download).");
            return new(ReleaseMatch.Different, Different(release));
        }

        foreach (var archive in FindArchives(release, searchFolders))
        {
            string? inner;
            try { inner = HashInArchive(archive, release.FileName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException) { continue; }
            if (inner is null) continue;
            known[release.ArchiveSha256] = inner;
            SaveKnown(userFilesDir, known);
            return inner.Equals(picked, StringComparison.OrdinalIgnoreCase)
                ? new(ReleaseMatch.Matches, $"This is the {release.FileName} from the {release.Name} release (checked against {Path.GetFileName(archive)}).", archive)
                : new(ReleaseMatch.Different, Different(release), archive);
        }
        return new(ReleaseMatch.NotChecked,
            $"Upshift couldn't check this file against the {release.Name} release: the release only publishes its download's fingerprint, " +
            $"and {release.ArchiveName} wasn't found in Downloads, on the Desktop or next to the file. Keep that download there and choose the file again to check it.");
    }

    /// <summary>From what's remembered only (no searching): whether this hash is the release's file, or NotChecked.</summary>
    public static ReleaseMatch Remembered(string sha256, UserFileRelease release, string userFilesDir) =>
        LoadKnown(userFilesDir).TryGetValue(release.ArchiveSha256, out var known)
            ? known.Equals(sha256, StringComparison.OrdinalIgnoreCase) ? ReleaseMatch.Matches : ReleaseMatch.Different
            : ReleaseMatch.NotChecked;

    private static string Different(UserFileRelease release) =>
        $"This isn't the {release.FileName} from the {release.Name} release. It may be another version or a different file. " +
        "It was added anyway; if it isn't the one you meant, remove it and choose the file from the release.";

    /// <summary>Candidate archives: the release's name (with or without a browser's " (1)"), the right size and SHA-256.</summary>
    private static IEnumerable<string> FindArchives(UserFileRelease release, IEnumerable<string> folders)
    {
        var stem = Path.GetFileNameWithoutExtension(release.ArchiveName);
        var ext = Path.GetExtension(release.ArchiveName);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> files;
            try { files = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, stem + "*" + ext).ToList() : Enumerable.Empty<string>(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                if (!seen.Add(Path.GetFullPath(file))) continue;
                bool ok;
                try { ok = new FileInfo(file).Length == release.ArchiveSize && OptiScalerInstaller.Sha256(file).Equals(release.ArchiveSha256, StringComparison.OrdinalIgnoreCase); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ok = false; }
                if (ok) yield return file;
            }
        }
    }

    /// <summary>SHA-256 of the named file inside the archive (read in memory; nothing is extracted), or null.</summary>
    private static string? HashInArchive(string archive, string fileName)
    {
        using var opened = ArchiveFactory.Open(archive);
        foreach (var entry in opened.Entries.Where(e => !e.IsDirectory && e.Key is not null))
        {
            if (!Path.GetFileName(entry.Key!.Replace('/', '\\')).Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = entry.OpenEntryStream();
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        return null;
    }

    private static Dictionary<string, string> LoadKnown(string dir)
    {
        try
        {
            var path = Path.Combine(dir, KnownName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    private static void SaveKnown(string dir, Dictionary<string, string> known)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, KnownName), JsonSerializer.Serialize(known, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
