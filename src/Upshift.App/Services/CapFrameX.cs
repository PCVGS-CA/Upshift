using System.Diagnostics;
using Microsoft.Win32;

namespace Upshift.App.Services;

/// <summary>
/// CapFrameX (github.com/CXWorld/CapFrameX), a free, open-source frame-time capture and analysis tool, as a companion
/// tool. Upshift never bundles, downloads or changes it: it only looks for its installed program and starts it.
/// </summary>
public static class CapFrameX
{
    public const string ReleasesUrl = "https://github.com/CXWorld/CapFrameX/releases";
    public const string ExeName = "CapFrameX.exe";

    /// <summary>The installed CapFrameX.exe (its uninstall entry, else the usual Program Files folder), or null.</summary>
    public static string? Find()
    {
        try
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                {
                    using var root = hive.OpenSubKey(path);
                    if (root is null) continue;
                    foreach (var name in root.GetSubKeyNames())
                    {
                        using var key = root.OpenSubKey(name);
                        if (key?.GetValue("DisplayName") is not string display || !display.StartsWith("CapFrameX", StringComparison.OrdinalIgnoreCase)) continue;
                        if (key.GetValue("InstallLocation") is string folder && File.Exists(Path.Combine(folder, ExeName))) return Path.Combine(folder, ExeName);
                        if ((key.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"') is { } icon
                            && icon.EndsWith(ExeName, StringComparison.OrdinalIgnoreCase) && File.Exists(icon)) return icon;
                    }
                }
            foreach (var programs in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var exe = Path.Combine(Environment.GetFolderPath(programs), "CapFrameX", ExeName);
                if (File.Exists(exe)) return exe;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return null;
    }

    /// <summary>Starts CapFrameX the way Explorer would (Windows asks for permission itself if CapFrameX needs it). Returns why it failed, or null.</summary>
    public static string? Open()
    {
        if (Find() is not { } exe) return "CapFrameX wasn't found.";
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return null; }   // the user said no to Windows' prompt
        catch (Exception ex) when (ex is not OutOfMemoryException) { return $"CapFrameX couldn't be started: {ex.Message}"; }
    }
}
