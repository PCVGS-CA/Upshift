using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Upshift.Core.Detection;

namespace Upshift.Core.Services;

/// <summary>One file in a problem report.</summary>
/// <param name="Name">The file name inside the zip, e.g. "OptiScaler.ini".</param>
/// <param name="Description">What it is, for the summary shown before saving.</param>
public sealed record ReportItem(string Name, string Description, string Text);

/// <summary>
/// "Report a problem": one zip of text files the user attaches to a GitHub issue themselves. Nothing is sent anywhere.
/// Before saving, every text has the Windows user name taken out of paths (and any secret, such as the SteamGridDB key,
/// replaced), and a game's report leaves out what today's log says about other games.
/// </summary>
public static class ProblemReport
{
    /// <summary>
    /// Takes personal details out: the user's profile folder becomes %USERPROFILE%, any other "\Users\&lt;name&gt;" path
    /// part becomes "\Users\&lt;user&gt;", and each secret is replaced with "(removed)".
    /// </summary>
    public static string Scrub(string text, string userProfile, string userName, IEnumerable<string?> secrets)
    {
        if (!string.IsNullOrEmpty(userProfile))
        {
            text = Regex.Replace(text, Regex.Escape(userProfile.TrimEnd('\\')), "%USERPROFILE%", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, Regex.Escape(userProfile.TrimEnd('\\').Replace('\\', '/')), "%USERPROFILE%", RegexOptions.IgnoreCase);
        }
        if (!string.IsNullOrEmpty(userName))
        {
            text = Regex.Replace(text, @"(?i)([\\/]Users[\\/])" + Regex.Escape(userName) + @"(?=[\\/""'\s]|$)", "$1<user>");
            // Folder names built from it, e.g. Claude Code's "C--Users-<name>-…", and "<name>@" in addresses.
            text = Regex.Replace(text, @"(?i)-Users-" + Regex.Escape(userName) + "-", "-Users-<user>-");
        }
        foreach (var secret in secrets.Where(s => !string.IsNullOrWhiteSpace(s) && s!.Length >= 6))
            text = text.Replace(secret!, "(removed)", StringComparison.Ordinal);
        return text;
    }

    /// <summary>
    /// Today's Upshift log, cut to this game: each entry starts with a line like "… UNINSTALL &lt;game&gt; | …" followed by
    /// indented lines; only entries naming this game (or its folder) are kept.
    /// </summary>
    public static string LogForGame(string log, string gameName, string? folder)
    {
        var kept = new StringBuilder();
        var keep = false;
        foreach (var line in log.Split('\n'))
        {
            // "2026-10-05 10:00:00 [pid 123] UNINSTALL Name | from C:\…" starts an entry; "…]   removed x" continues it.
            var message = line.IndexOf("] ", StringComparison.Ordinal) is var i and >= 0 ? line[(i + 2)..] : line;
            if (!message.StartsWith("  ", StringComparison.Ordinal))
                keep = message.Contains(gameName, StringComparison.OrdinalIgnoreCase)
                       || (folder is not null && message.Contains(folder, StringComparison.OrdinalIgnoreCase));
            if (keep) kept.Append(line).Append('\n');
        }
        return kept.Length == 0 ? "(Nothing about this game in today's log.)\n" : kept.ToString();
    }

    /// <summary>The files in a folder (names, sizes and, for programs and DLLs, versions), never their contents.</summary>
    public static string FileList(string folder, int max = 20000)
    {
        var text = new StringBuilder();
        text.AppendLine($"Files in {folder} (name, size in bytes, version for .exe and .dll):");
        var count = 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(folder, "*", options))
            {
                if (++count > max) { text.AppendLine($"… and more (only the first {max} are listed)"); break; }
                var info = new FileInfo(file);
                var ext = info.Extension.ToLowerInvariant();
                var version = ext is ".exe" or ".dll" ? FileVersions.Read(file) : null;
                text.AppendLine($"{Path.GetRelativePath(folder, file)}\t{info.Length}" + (version is null ? "" : $"\t{version}"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text.AppendLine($"(Listing stopped: {ex.Message})"); }
        return text.ToString();
    }

    /// <summary>Writes the items (already scrubbed) into a zip, with a README listing them.</summary>
    public static void Save(string zipPath, IReadOnlyList<ReportItem> items)
    {
        var temp = zipPath + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            var readme = new StringBuilder("Upshift problem report. Attach this file to a GitHub issue; nothing in it was sent anywhere.\n\nContents:\n");
            foreach (var item in items) readme.AppendLine($"- {item.Name}: {item.Description}");
            Write(zip, "README.txt", readme.ToString());
            foreach (var item in items) Write(zip, item.Name, item.Text);
        }
        File.Move(temp, zipPath, overwrite: true);

        static void Write(ZipArchive zip, string name, string text)
        {
            using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            var bytes = new UTF8Encoding(false).GetBytes(text);
            stream.Write(bytes);
        }
    }
}
