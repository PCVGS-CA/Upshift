namespace Upshift.Core.Catalog;

/// <summary>One line of catalog.json's "dlssVersionNames": versions from <see cref="From"/> up (or these files) have this name.</summary>
public sealed class DlssVersionName
{
    /// <summary>When set, the line applies to these DLL names only, whatever their version (nvngx_dlssnr.dll is DLSS 5).</summary>
    public List<string> Files { get; set; } = new();

    /// <summary>The lowest version with this name, e.g. "310.5". Lines are checked in order; the first match wins.</summary>
    public string? From { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>Turns a DLSS DLL version into what NVIDIA calls it: "310.9.1" → "310.9.1 (DLSS 4.5)".</summary>
public static class DlssNames
{
    /// <summary>"DLSS 4.5" for 310.9.1, or null when the version can't be read or no line matches.</summary>
    public static string? Name(IReadOnlyList<DlssVersionName> table, string? version, string? fileName = null)
    {
        var file = fileName is null ? null : Path.GetFileName(fileName);
        foreach (var line in table.Where(l => l.Files.Count > 0))
            if (file is not null && line.Files.Contains(file, StringComparer.OrdinalIgnoreCase)) return line.Name;

        if (Parse(version) is not { } v) return null;
        foreach (var line in table.Where(l => l.Files.Count == 0 && l.From is not null))
            if (Parse(line.From) is { } from && v >= from) return line.Name;
        return null;
    }

    /// <summary>"310.9.1 (DLSS 4.5)"; just the version when it has no name.</summary>
    public static string Label(IReadOnlyList<DlssVersionName> table, string version, string? fileName = null) =>
        Name(table, version, fileName) is { } name ? $"{version} ({name})" : version;

    private static Version? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var text = s.Trim().TrimStart('v', 'V');
        var end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
        text = text[..end].Trim('.');
        if (text.Length == 0) return null;
        if (!text.Contains('.')) text += ".0";
        return Version.TryParse(text, out var v) ? v : null;
    }
}
