using System.Text.RegularExpressions;
using Upshift.Core.Models;

namespace Upshift.Core.Hardware;

public enum OfficialFsr4
{
    /// <summary>Not an AMD card AMD's official FSR 4.1 covers (RX 5000, Radeon 680M/780M, Ryzen Z1, other vendors).</summary>
    NotCovered,
    /// <summary>A covered card with Adrenalin 26.10.1 or newer: OptiScaler's own FSR 4 is the recommendation.</summary>
    Ready,
    /// <summary>A covered card with an older driver.</summary>
    DriverTooOld,
    /// <summary>A covered card whose Adrenalin version Windows doesn't say.</summary>
    DriverUnknown
}

/// <summary>
/// AMD's Adrenalin 26.10.1 adds official FSR 4.1 for Radeon RX 6000 (RDNA 2) and Ryzen AI / Ryzen Z2 graphics. On those
/// cards, with that driver, OptiScaler's own FSR 4 replaces the modified 4.1.1b file and HelixSR as the suggestion.
/// Not yet tested on a real RX 6000 with the new driver.
/// </summary>
public static class AmdOfficialFsr4
{
    public const string MinAdrenalin = "26.10.1";

    public static bool Covered(GpuInfo? gpu, string? processor)
    {
        if (gpu is not { Vendor: GpuVendor.Amd }) return false;
        if (gpu.Generation is "RDNA 2" or "RDNA 3.5 integrated") return true;     // RX 6000; Ryzen AI's Radeon 8x0M / 80x0S
        // Handhelds' graphics are named "AMD Radeon(TM) Graphics", so the processor decides: Ryzen Z2 and Z2 Extreme are
        // covered; the Z2 Go (Radeon 680M-class) and the Z1 family are not.
        if (gpu.IsIntegrated && processor is not null && Regex.IsMatch(processor, @"Ryzen\s+(AI\s+)?Z2\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(processor, @"Z2\s+Go", RegexOptions.IgnoreCase))
            return true;
        if (gpu.IsIntegrated && processor is not null && Regex.IsMatch(processor, @"Ryzen\s+AI\b", RegexOptions.IgnoreCase)
            && gpu.Generation == "Radeon integrated")
            return true;
        return false;
    }

    public static OfficialFsr4 For(GpuInfo? gpu, string? processor, string? adrenalinVersion)
    {
        if (!Covered(gpu, processor)) return OfficialFsr4.NotCovered;
        return DriverDetails.CompareVersions(adrenalinVersion, MinAdrenalin) switch
        {
            null => OfficialFsr4.DriverUnknown,
            < 0 => OfficialFsr4.DriverTooOld,
            _ => OfficialFsr4.Ready
        };
    }

    public const string UpdateDriverLine = "Update your AMD driver to 26.10.1 for official FSR 4.1";
    public const string ReadyLine = "Your AMD driver runs FSR 4.1 officially. Use OptiScaler's own FSR 4.";
}
