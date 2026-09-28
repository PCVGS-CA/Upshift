using System.Diagnostics;

namespace Upshift.Core.Detection;

public static class FileVersions
{
    /// <summary>Version like "310.2.1" (trailing ".0" dropped), or null if the file has none.</summary>
    public static string? Read(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0 && info.FilePrivatePart == 0)
                return string.IsNullOrWhiteSpace(info.ProductVersion) ? null : info.ProductVersion.Trim();

            var v = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
            return info.FilePrivatePart != 0 ? $"{v}.{info.FilePrivatePart}" : v;
        }
        catch
        {
            return null;
        }
    }

    public static FileVersionInfo? TryGetInfo(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path); }
        catch { return null; }
    }
}
