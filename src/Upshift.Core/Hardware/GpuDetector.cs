using System.Management;
using System.Runtime.InteropServices;
using Upshift.Core.Models;

namespace Upshift.Core.Hardware;

/// <summary>
/// Lists the graphics cards Windows currently has, and picks the one games will use.
/// The rest of the app only needs a GpuInfo, so another detection method can replace this one.
/// </summary>
public static class GpuDetector
{
    public static IReadOnlyList<GpuInfo> DetectAll()
    {
        var list = new List<GpuInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, DriverVersion FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                using var mo = (ManagementObject)obj;
                var name = (mo["Name"] as string)?.Trim() ?? "";
                var pnp = mo["PNPDeviceID"] as string ?? "";
                var driver = mo["DriverVersion"] as string;

                var vendorId = ParseHexAfter(pnp, "VEN_");
                var deviceId = ParseHexAfter(pnp, "DEV_");
                var vendor = vendorId switch
                {
                    0x10DE => GpuVendor.Nvidia,
                    0x1002 or 0x1022 => GpuVendor.Amd,
                    0x8086 => GpuVendor.Intel,
                    _ => GpuVendor.Unknown
                };

                // Skips "Microsoft Basic Display Adapter", remote-desktop and virtual adapters.
                if (vendor == GpuVendor.Unknown) continue;

                var (generation, integrated) = GpuClassifier.Classify(vendor, name);
                list.Add(new GpuInfo(name, vendor, vendorId, deviceId, driver, generation, integrated));
            }
        }
        catch (ManagementException) { }
        catch (COMException) { }
        catch (UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>A dedicated card wins over integrated graphics (laptops usually have both).</summary>
    public static GpuInfo? PickPrimary(IEnumerable<GpuInfo> gpus) =>
        gpus.OrderBy(g => g.IsIntegrated)
            .ThenBy(g => g.Vendor switch { GpuVendor.Nvidia => 0, GpuVendor.Amd => 1, GpuVendor.Intel => 2, _ => 3 })
            .FirstOrDefault();

    private static int ParseHexAfter(string text, string key)
    {
        var i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0 || i + key.Length + 4 > text.Length) return 0;
        try { return Convert.ToInt32(text.Substring(i + key.Length, 4), 16); }
        catch (FormatException) { return 0; }
    }
}
