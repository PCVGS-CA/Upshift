using Upshift.Core.Util;

namespace Upshift.Core.Services;

/// <summary>
/// Where Upshift's own program files are, and the rule that they never share a folder with its data.
/// The installer (Velopack) puts the app in %LocalAppData%\Upshift.App and its uninstaller deletes that whole folder,
/// while the data lives in %LocalAppData%\Upshift. Neither may ever be inside the other.
/// </summary>
public static class AppLocations
{
    /// <summary>
    /// The Velopack package id. It decides the install folder (%LocalAppData%\&lt;id&gt;), so it must never be "Upshift".
    /// The release workflow (.github/workflows/release.yml) passes the same id to vpk.
    /// </summary>
    public const string PackageId = "Upshift.App";

    /// <summary>
    /// The folder Upshift.exe runs from. Uses the process path rather than AppContext.BaseDirectory, which points at
    /// a temporary extraction folder when the app is published as a single file.
    /// </summary>
    public static string ExeDir { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>The folder the installer uses: %LocalAppData%\Upshift.App.</summary>
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), PackageId);

    /// <summary>True when the two folders are the same or one is inside the other.</summary>
    public static bool Overlap(string a, string b)
    {
        var x = PathUtil.Normalize(a);
        var y = PathUtil.Normalize(b);
        return PathUtil.IsSameOrInside(x, y) || PathUtil.IsSameOrInside(y, x);
    }

    /// <summary>
    /// Null when the data folder is safely apart from the program folders; otherwise why it isn't. A data folder
    /// inside the program folder would be deleted by the uninstaller, and one containing it would be deleted by
    /// "delete my data" together with the running app.
    /// </summary>
    public static string? DataFolderProblem(string dataDir, string? appRootDir)
    {
        foreach (var dir in new[] { ExeDir, InstallDir, appRootDir })
            if (dir is not null && Overlap(dataDir, dir))
                return $"Upshift's data folder ({dataDir}) and its program folder ({dir}) overlap.";
        return null;
    }
}
