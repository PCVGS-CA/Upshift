using System.Globalization;
using Microsoft.Win32;
using Upshift.Core.Models;

namespace Upshift.Core.Hardware;

/// <summary>
/// A card's driver as its registry entry describes it (no administrator rights): Windows' driver version, the driver's
/// date and, for AMD, the Adrenalin version ("26.10.1"). Each part is null when Windows doesn't say.
/// </summary>
public sealed record DriverDetails(string? WindowsVersion, DateTime? Date, string? AdrenalinVersion)
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static readonly DriverDetails None = new(null, null, null);

    /// <summary>Never throws.</summary>
    public static DriverDetails Read(GpuInfo? gpu)
    {
        if (gpu is null) return None;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (cls is null) return None with { WindowsVersion = gpu.DriverVersion };
            var want = $"ven_{gpu.VendorId:x4}&dev_{gpu.DeviceId:x4}";
            foreach (var name in cls.GetSubKeyNames())
            {
                if (!name.All(char.IsDigit)) continue;
                using var key = cls.OpenSubKey(name);
                if (key?.GetValue("MatchingDeviceId") is not string match || !match.Contains(want, StringComparison.OrdinalIgnoreCase)) continue;
                return new DriverDetails(
                    key.GetValue("DriverVersion") as string ?? gpu.DriverVersion,
                    ParseDate(key.GetValue("DriverDate") as string),
                    gpu.Vendor == GpuVendor.Amd ? (key.GetValue("RadeonSoftwareVersion") as string)?.Trim() : null);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return None with { WindowsVersion = gpu.DriverVersion };
    }

    /// <summary>The registry's "10-6-2026" (month-day-year).</summary>
    public static DateTime? ParseDate(string? text) =>
        DateTime.TryParseExact(text?.Trim(), new[] { "M-d-yyyy", "MM-dd-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>The version vendors publish: NVIDIA's "617.42", AMD's Adrenalin "26.10.1", otherwise Windows' version.</summary>
    public static string? PublishedVersion(GpuInfo? gpu, DriverDetails details) => gpu?.Vendor switch
    {
        GpuVendor.Nvidia => Install.Dlss5.NvidiaDriver(details.WindowsVersion ?? gpu.DriverVersion) ?? details.WindowsVersion,
        GpuVendor.Amd => details.AdrenalinVersion ?? details.WindowsVersion,
        _ => details.WindowsVersion ?? gpu?.DriverVersion
    };

    /// <summary>Compares dotted numbers part by part ("26.9.1" &lt; "26.10.1"). Null when either isn't one.</summary>
    public static int? CompareVersions(string? a, string? b)
    {
        static int[]? Parts(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var parts = s.Trim().Split('.');
            var result = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out result[i])) return null;
            return result;
        }
        if (Parts(a) is not { } x || Parts(b) is not { } y) return null;
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var p = i < x.Length ? x[i] : 0;
            var q = i < y.Length ? y[i] : 0;
            if (p != q) return p.CompareTo(q);
        }
        return 0;
    }
}
