namespace Upshift.App.Services;

/// <summary>
/// Keeps an installed Upshift's shortcuts and "Installed apps" entry pointing at this install, with Upshift's icon
/// (the work is in Core.Services.Shortcuts).
/// <para>
/// Why: a shortcut named "Upshift" that points at a missing exe or icon (or carries a bad icon index) makes Windows
/// draw a blank page in the Start menu, and on the taskbar, which takes a running app's icon from the shortcut with the
/// same app ID. On 2026-10-03 a test install from Claude's sandbox replaced the real shortcut with one pointing at
/// files that only existed in the sandbox.
/// </para>
/// Runs after the installer installs or updates (also over an older copy), creating the Start menu and desktop
/// shortcuts if missing, and on every start of an installed copy, where it only repairs: a shortcut the user deleted
/// stays deleted. Only the real package (Upshift.App) does this; test packages, portable and development copies never
/// touch the "Upshift" shortcuts.
/// </summary>
public static class Shortcuts
{
    /// <summary>The install root (%LocalAppData%\Upshift.App) when this is the installed copy running from current\; otherwise null.</summary>
    public static string? InstallRoot()
    {
        var exeDir = Core.Services.AppLocations.ExeDir.TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.GetFileName(exeDir).Equals("current", StringComparison.OrdinalIgnoreCase)) return null;
        var root = Path.GetDirectoryName(exeDir);
        if (root is null || !File.Exists(Path.Combine(root, "Update.exe"))) return null;
        if (File.Exists(Path.Combine(root, ".portable"))) return null; // the portable zip makes no shortcuts
        if (!Path.GetFileName(root).Equals(Core.Services.AppLocations.PackageId, StringComparison.OrdinalIgnoreCase)) return null;
        return root;
    }

    /// <summary>Checks (and with <paramref name="createMissing"/>, creates) the shortcuts. Never throws.</summary>
    public static List<string> Ensure(bool createMissing)
    {
        try
        {
            if (InstallRoot() is not { } root) return new();
            return Core.Services.Shortcuts.Ensure(root, new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            }, createMissing, Core.Services.AppLocations.PackageId);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Couldn't check Upshift's shortcuts (Upshift kept running)");
            return new();
        }
    }
}
