using System.Text;

namespace Upshift.Core.Install;

/// <summary>
/// Minimal INI editor for OptiScaler.ini: reads and changes "Key=value" lines inside "[Section]" blocks and leaves
/// every other line (comments, blank lines, order, line endings) exactly as it was.
/// </summary>
public sealed class IniFile
{
    private readonly List<string> _lines;
    private readonly string _newline;
    private readonly bool _trailingNewline;

    private IniFile(string text)
    {
        _newline = text.Contains("\r\n") ? "\r\n" : "\n";
        _trailingNewline = text.EndsWith('\n');
        _lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (_trailingNewline && _lines.Count > 0 && _lines[^1].Length == 0) _lines.RemoveAt(_lines.Count - 1);
    }

    public static IniFile Load(string path) => new(File.ReadAllText(path));
    public static IniFile Parse(string text) => new(text);

    public string? Get(string section, string key)
    {
        var index = FindKey(section, key);
        return index < 0 ? null : _lines[index][(_lines[index].IndexOf('=') + 1)..].Trim();
    }

    /// <summary>True when the section exists and already has this key (used to refuse keys OptiScaler doesn't know).</summary>
    public bool Has(string section, string key) => FindKey(section, key) >= 0;

    /// <summary>Every "Key=" and the sections it appears in, e.g. "Dxgi" → ["Spoofing"].</summary>
    public Dictionary<string, List<string>> KeyIndex()
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        foreach (var raw in _lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            if (section is null || line.StartsWith(';') || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (!index.TryGetValue(key, out var list)) index[key] = list = new();
            if (!list.Contains(section, StringComparer.OrdinalIgnoreCase)) list.Add(section);
        }
        return index;
    }

    /// <summary>Every "Key=value" line with its section, in file order.</summary>
    public IEnumerable<(string Section, string Key, string Value)> Entries()
    {
        string? section = null;
        foreach (var raw in _lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            if (section is null || line.StartsWith(';') || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            yield return (section, line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
    }

    /// <summary>Changes the value on an existing key line; adds the key at the end of its section if it isn't there.</summary>
    public void Set(string section, string key, string value)
    {
        var index = FindKey(section, key);
        if (index >= 0)
        {
            var eq = _lines[index].IndexOf('=');
            _lines[index] = _lines[index][..(eq + 1)] + value;
            return;
        }

        var header = FindSection(section);
        if (header < 0)
        {
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }
        var insertAt = header + 1;
        for (var i = header + 1; i < _lines.Count && !IsSectionHeader(_lines[i]); i++)
            if (_lines[i].Trim().Length > 0 && !_lines[i].TrimStart().StartsWith(';')) insertAt = i + 1;
        _lines.Insert(insertAt, $"{key}={value}");
    }

    public void Save(string path)
    {
        var text = string.Join(_newline, _lines) + (_trailingNewline ? _newline : "");
        var temp = path + ".upshift-tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public override string ToString() => string.Join(_newline, _lines) + (_trailingNewline ? _newline : "");

    private int FindSection(string section)
    {
        for (var i = 0; i < _lines.Count; i++)
            if (IsSectionHeader(_lines[i]) && _lines[i].Trim()[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private int FindKey(string section, string key)
    {
        var header = FindSection(section);
        if (header < 0) return -1;
        for (var i = header + 1; i < _lines.Count && !IsSectionHeader(_lines[i]); i++)
        {
            var line = _lines[i].TrimStart();
            if (line.StartsWith(';') || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static bool IsSectionHeader(string line)
    {
        var t = line.Trim();
        return t.StartsWith('[') && t.EndsWith(']');
    }
}
