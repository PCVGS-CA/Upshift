using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Upshift.Core.Models;

namespace Upshift.App.Helpers;

public static class Ui
{
    /// <summary>A DLSS DLL's version with its marketing name, "310.9.1 (DLSS 4.5)"; other files' versions as they are.</summary>
    public static string VersionLabel(string version, string fileName) =>
        Path.GetFileName(fileName).StartsWith("nvngx_dlss", StringComparison.OrdinalIgnoreCase)
            ? Core.Catalog.DlssNames.Label(AppServices.Catalog.DlssVersionNames, version, fileName)
            : version;

    /// <summary>A DLSS version with its marketing name (for nvngx_dlss.dll unless another DLSS file is named).</summary>
    public static string DlssLabel(string version, string fileName = "nvngx_dlss.dll") =>
        Core.Catalog.DlssNames.Label(AppServices.Catalog.DlssVersionNames, version, fileName);

    public static SolidColorBrush Brush(string hex)
    {
        hex = hex.TrimStart('#');
        var r = byte.Parse(hex[..2], NumberStyles.HexNumber);
        var g = byte.Parse(hex[2..4], NumberStyles.HexNumber);
        var b = byte.Parse(hex[4..6], NumberStyles.HexNumber);
        return new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
    }

    /// <summary>Company colours for chips: NVIDIA green, AMD red, Intel blue.</summary>
    public static (string Background, string Foreground) VendorColors(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => ("#E6F2D8", "#2B560B"),
        GpuVendor.Amd => ("#FBE7E7", "#7C1F1F"),
        GpuVendor.Intel => ("#E3EEFA", "#0C447C"),
        _ => ("#ECEEF1", "#454850")
    };

    public static (string Background, string Foreground) FamilyColors(UpscalerFamily family) => family switch
    {
        UpscalerFamily.Dlss => VendorColors(GpuVendor.Nvidia),
        UpscalerFamily.Fsr => VendorColors(GpuVendor.Amd),
        UpscalerFamily.Xess => VendorColors(GpuVendor.Intel),
        _ => VendorColors(GpuVendor.Unknown)
    };

    public static string FamilyLabel(UpscalerFamily family) => family switch
    {
        UpscalerFamily.Dlss => "DLSS",
        UpscalerFamily.Fsr => "FSR",
        UpscalerFamily.Xess => "XeSS",
        _ => "Streamline"
    };
}
