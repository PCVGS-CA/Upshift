using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Upshift.Core.Services;

/// <summary>
/// The PC, for shared results: processor, graphics card, memory and Windows version. Never the Windows user name, the
/// PC name or any folder path. Each part is null when Windows doesn't say.
/// </summary>
public sealed record PcInfo(string? Processor, string? GraphicsCard, string? DriverVersion, double? MemoryGb, string? Windows)
{
    /// <summary>Reads this PC. <paramref name="graphicsCard"/> and <paramref name="driver"/> come from Upshift's own detection.</summary>
    public static PcInfo Read(string? graphicsCard, string? driver) =>
        new(ProcessorName(), graphicsCard, driver, MemoryGigabytes(), WindowsVersion());

    public string ProcessorText => Processor ?? "Unknown processor";
    public string GraphicsText => (GraphicsCard ?? "Unknown graphics card") + (DriverVersion is { Length: > 0 } d ? $" (driver {d})" : "");
    public string MemoryText => MemoryGb is { } gb ? $"{Math.Round(gb)} GB" : "Unknown";
    public string WindowsText => Windows ?? "Windows";

    /// <summary>The driver's date, for the "This PC" panel.</summary>
    public DateTime? DriverDate { get; init; }

    /// <summary>Monitors as "LG ULTRAGEAR (main) · 2560 × 1440 · 165 Hz" (the monitor's own name, never a path).</summary>
    public IReadOnlyList<string> Displays { get; init; } = Array.Empty<string>();

    /// <summary>The "This PC" panel's rows.</summary>
    public List<(string Label, string Value)> Lines()
    {
        var lines = new List<(string, string)>
        {
            ("Processor", ProcessorText),
            ("Graphics card", GraphicsCard ?? "Unknown graphics card"),
            ("Driver", (DriverVersion ?? "Unknown") + (DriverDate is { } d ? $", {d.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}" : "")),
            ("Memory", MemoryText),
            ("Windows", WindowsText)
        };
        if (Displays.Count == 0) lines.Add(("Display", "Unknown"));
        for (var i = 0; i < Displays.Count; i++) lines.Add((Displays.Count == 1 ? "Display" : $"Display {i + 1}", Displays[i]));
        return lines;
    }

    /// <summary>"Copy as text": one "Label: value" line each.</summary>
    public string Text() => string.Join(Environment.NewLine, Lines().Select(l => $"{l.Label}: {l.Value}"));

    private static string? ProcessorName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() is { Length: > 0 } name
                ? System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ") : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static double? MemoryGigabytes()
    {
        try
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            return GlobalMemoryStatusEx(ref status) ? status.TotalPhys / 1024.0 / 1024 / 1024 : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>"Windows 11 Pro 25H2 (build 26200)". The product name says "Windows 10" on Windows 11, so the build decides.</summary>
    private static string? WindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null) return null;
            var product = key.GetValue("ProductName") as string ?? "Windows";
            var display = key.GetValue("DisplayVersion") as string;
            var build = key.GetValue("CurrentBuildNumber") as string;
            if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            return $"{product}{(display is { Length: > 0 } ? " " + display : "")}{(build is { Length: > 0 } ? $" (build {build})" : "")}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
