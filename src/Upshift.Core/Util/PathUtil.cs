using System.Security.Cryptography;
using System.Text;

namespace Upshift.Core.Util;

public static class PathUtil
{
    /// <summary>Full path, no trailing slash, lower case. Use only for comparing, never for display.</summary>
    public static string Normalize(string path)
    {
        try { path = Path.GetFullPath(path); } catch { /* keep as given */ }
        return path.TrimEnd('\\', '/').ToLowerInvariant();
    }

    /// <summary>True when child is the same folder as parent or somewhere inside it. Both must be normalized.</summary>
    public static bool IsSameOrInside(string child, string parent) =>
        child == parent || child.StartsWith(parent + "\\", StringComparison.Ordinal);

    /// <summary>A short id that stays the same for the same install folder across scans.</summary>
    public static string StableId(string installDir)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(Normalize(installDir)));
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }

    /// <summary>Folder name turned into something readable ("Hades_II" becomes "Hades II").</summary>
    public static string PrettyName(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) return dir;
        return name.Replace('_', ' ').Trim();
    }

    public static bool SafeDirectoryExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Directory.Exists(path); } catch { return false; }
    }
}
