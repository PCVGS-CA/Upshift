using System.Diagnostics;

namespace Upshift.App.Services;

/// <summary>
/// SCSKiller (github.com/BlueHeisenberg/SCSKiller), a free, open-source tool for shader stutter, as a companion tool.
/// Upshift never reads, changes, bundles or downloads any of its files: it only checks where it is and starts it.
/// </summary>
public static class ScsKiller
{
    public const string ReleasesUrl = "https://github.com/BlueHeisenberg/SCSKiller/releases";
    public const string ExeName = "SCSKiller.exe";

    /// <summary>%LOCALAPPDATA%\SCSKiller, where its installer puts it.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller");

    /// <summary>The program to start: the one picked in Settings, otherwise the default folder's, or null when neither exists.</summary>
    public static string? Find()
    {
        try
        {
            if (AppServices.Settings.Current.ScsKillerPath is { Length: > 0 } picked && File.Exists(picked)) return picked;
            return Directory.Exists(DefaultFolder) ? Path.Combine(DefaultFolder, ExeName) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>Starts SCSKiller the way Explorer would (never with administrator rights from Upshift). Returns why it failed, or null.</summary>
    public static string? Open()
    {
        if (Find() is not { } exe) return "SCSKiller wasn't found.";
        try
        {
            if (!File.Exists(exe)) return $"{ExeName} isn't in {DefaultFolder}. Point Upshift at it in Settings.";
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return $"SCSKiller couldn't be started: {ex.Message}"; }
    }
}
