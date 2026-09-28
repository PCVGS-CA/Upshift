using System.Text.RegularExpressions;
using Upshift.Core.Models;

namespace Upshift.Core.Hardware;

/// <summary>Works out a card's generation from its name, e.g. "AMD Radeon RX 9070 XT" is RDNA 4.</summary>
public static class GpuClassifier
{
    public static (string Generation, bool IsIntegrated) Classify(GpuVendor vendor, string name) => vendor switch
    {
        GpuVendor.Nvidia => ClassifyNvidia(name),
        GpuVendor.Amd => ClassifyAmd(name),
        GpuVendor.Intel => ClassifyIntel(name),
        _ => ("Unknown", false)
    };

    private static (string, bool) ClassifyNvidia(string n)
    {
        if (Has(n, @"RTX\s*50\d{2}") || Has(n, @"Blackwell")) return ("Blackwell", false);
        if (Has(n, @"RTX\s*40\d{2}") || Has(n, @"\bAda\b")) return ("Ada Lovelace", false);
        if (Has(n, @"RTX\s*30\d{2}") || Has(n, @"RTX\s*A\d{4}")) return ("Ampere", false);
        if (Has(n, @"RTX\s*20\d{2}") || Has(n, @"Quadro\s*RTX") || Has(n, @"TITAN\s*RTX")) return ("Turing", false);
        if (Has(n, @"GTX\s*16\d{2}")) return ("Turing GTX", false);
        return ("Older NVIDIA", false);
    }

    private static (string, bool) ClassifyAmd(string n)
    {
        if (Has(n, @"RX\s*9\d{3}")) return ("RDNA 4", false);
        if (Has(n, @"RX\s*7\d{3}")) return ("RDNA 3", false);
        if (Has(n, @"RX\s*6\d{3}")) return ("RDNA 2", false);
        if (Has(n, @"RX\s*5\d{3}")) return ("RDNA 1", false);

        // Integrated graphics get their own labels so desktop-card runtimes never match them by accident.
        if (Has(n, @"Radeon\s*(8[4-9]0M|80[4-6]0S)")) return ("RDNA 3.5 integrated", true);
        if (Has(n, @"Radeon\s*7[0-9]0M")) return ("RDNA 3 integrated", true);
        if (Has(n, @"Radeon\s*6[0-9]0M")) return ("RDNA 2 integrated", true);
        if (Has(n, @"Radeon(\(TM\))?\s*Graphics") || Has(n, @"Vega")) return ("Radeon integrated", true);
        return ("Older AMD", false);
    }

    private static (string, bool) ClassifyIntel(string n)
    {
        if (Has(n, @"Arc(\(TM\))?\s*(Pro\s*)?B\d{2,3}")) return ("Battlemage", false);
        if (Has(n, @"Arc(\(TM\))?\s*(Pro\s*)?A\d{2,3}")) return ("Alchemist", false);
        if (Has(n, @"Arc(\(TM\))?\s*1[34]0V")) return ("Xe2 integrated", true);
        if (Has(n, @"Arc")) return ("Xe-LPG integrated", true);
        return ("Intel integrated", true);
    }

    private static bool Has(string input, string pattern) =>
        Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
