using System.Text;

namespace Upshift.Core.Util;

/// <summary>
/// Minimal reader for Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf).
/// Values are either strings or nested dictionaries.
/// </summary>
public static class VdfParser
{
    public static Dictionary<string, object> Parse(string text)
    {
        var index = 0;
        var root = NewDict();
        ReadInto(root, text, ref index);
        return root;
    }

    public static Dictionary<string, object>? Child(this Dictionary<string, object> dict, string key) =>
        dict.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

    public static string? Value(this Dictionary<string, object> dict, string key) =>
        dict.TryGetValue(key, out var v) ? v as string : null;

    private static Dictionary<string, object> NewDict() => new(StringComparer.OrdinalIgnoreCase);

    private static void ReadInto(Dictionary<string, object> dict, string t, ref int i)
    {
        while (true)
        {
            var key = NextToken(t, ref i, out var keyQuoted);
            if (key is null || (!keyQuoted && key == "}")) return;

            var value = NextToken(t, ref i, out var valueQuoted);
            if (value is null) return;

            if (!valueQuoted && value == "{")
            {
                var child = NewDict();
                ReadInto(child, t, ref i);
                dict[key] = child;
            }
            else
            {
                dict[key] = value;
            }
        }
    }

    private static string? NextToken(string t, ref int i, out bool quoted)
    {
        quoted = false;
        while (i < t.Length)
        {
            var c = t[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '/' && i + 1 < t.Length && t[i + 1] == '/')
            {
                while (i < t.Length && t[i] != '\n') i++;
                continue;
            }

            // Platform conditionals like [$WIN32] are ignored.
            if (c == '[')
            {
                while (i < t.Length && t[i] != ']') i++;
                i++;
                continue;
            }

            if (c == '{' || c == '}')
            {
                i++;
                return c.ToString();
            }

            if (c == '"')
            {
                quoted = true;
                i++;
                var sb = new StringBuilder();
                while (i < t.Length && t[i] != '"')
                {
                    if (t[i] == '\\' && i + 1 < t.Length)
                    {
                        i++;
                        sb.Append(t[i] switch { 'n' => '\n', 't' => '\t', _ => t[i] });
                    }
                    else
                    {
                        sb.Append(t[i]);
                    }
                    i++;
                }
                i++; // closing quote
                return sb.ToString();
            }

            var start = i;
            while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] != '{' && t[i] != '}' && t[i] != '"') i++;
            return t[start..i];
        }
        return null;
    }
}
