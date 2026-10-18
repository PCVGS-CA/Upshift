namespace Upshift.Core.Services;

/// <summary>
/// Version order as SemVer 2 (and Velopack) sees it: 1.2.0 &lt; 1.3.0-beta.1 &lt; 1.3.0-beta.2 &lt; 1.3.0-test.1 &lt; 1.3.0.
/// A pre-release sorts below its release; pre-release parts compare one by one, numbers as numbers, numbers below words,
/// words in ordinal order ("beta" &lt; "test"); build metadata (+…) is ignored. A leading "v" is allowed.
/// </summary>
public static class SemVer
{
    /// <summary>Negative, zero or positive like CompareTo; null when either isn't a version.</summary>
    public static int? Compare(string a, string b)
    {
        if (Parse(a) is not { } x || Parse(b) is not { } y) return null;
        var byCore = x.Core.CompareTo(y.Core);
        if (byCore != 0) return byCore;
        // A release is above its own pre-releases.
        if (x.Pre.Length == 0 && y.Pre.Length == 0) return 0;
        if (x.Pre.Length == 0) return 1;
        if (y.Pre.Length == 0) return -1;
        for (var i = 0; i < Math.Min(x.Pre.Length, y.Pre.Length); i++)
        {
            var (p, q) = (x.Pre[i], y.Pre[i]);
            var pn = long.TryParse(p, out var pv);
            var qn = long.TryParse(q, out var qv);
            var c = pn && qn ? pv.CompareTo(qv) : pn ? -1 : qn ? 1 : string.CompareOrdinal(p, q);
            if (c != 0) return Math.Sign(c);
        }
        return x.Pre.Length.CompareTo(y.Pre.Length);
    }

    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

    private static (Version Core, string[] Pre)? Parse(string text)
    {
        var v = text.Trim().TrimStart('v', 'V');
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        var dash = v.IndexOf('-');
        var core = dash >= 0 ? v[..dash] : v;
        var pre = dash >= 0 ? v[(dash + 1)..].Split('.') : Array.Empty<string>();
        if (!Version.TryParse(core, out var version) || pre.Any(string.IsNullOrEmpty)) return null;
        // 1.3 and 1.3.0 are the same version.
        return (new Version(version.Major, version.Minor, Math.Max(0, version.Build)), pre);
    }
}
