using System.Globalization;
using System.Text;

namespace Upshift.Core.Util;

/// <summary>Fuzzy game-name matching, for picking the right result from a store or wiki search.</summary>
public static class NameMatch
{
    private static readonly string[] EditionSuffixes =
    {
        "game of the year edition", "goty edition", "definitive edition", "complete edition",
        "enhanced edition", "deluxe edition", "standard edition", "remastered"
    };

    /// <summary>1 means identical after tidying (case, punctuation, "&amp;", edition suffixes); 0 means nothing alike.</summary>
    public static double Similarity(string a, string b)
    {
        a = Tidy(a);
        b = Tidy(b);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        return 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);
    }

    /// <summary>Lower case, no punctuation or accents, "&amp;" spelled out and edition suffixes removed.</summary>
    public static string Tidy(string s)
    {
        s = s.ToLowerInvariant().Replace("&", " and ");
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        s = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var suffix in EditionSuffixes)
            if (s.EndsWith(" " + suffix, StringComparison.Ordinal)) s = s[..^(suffix.Length + 1)];
        return s;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
