using Upshift.Core.Models;

namespace Upshift.Core.Hardware;

/// <summary>
/// One graphics card. Generation strings must match the "generations" lists in catalog.json
/// exactly (for example "RDNA 4", "Blackwell"), because that is how backends are matched to cards.
/// </summary>
public sealed record GpuInfo(
    string Name,
    GpuVendor Vendor,
    int VendorId,
    int DeviceId,
    string? DriverVersion,
    string Generation,
    bool IsIntegrated);
