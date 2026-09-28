namespace Upshift.Core.Models;

/// <summary>Where a game was found. Order here is also merge priority (earlier wins).</summary>
public enum GameSourceKind
{
    Steam,
    Epic,
    Gog,
    Xbox,
    EA,
    Ubisoft,
    BattleNet,
    Heroic,
    Itch,
    Manual,
    InstalledProgram,
    DriveScan
}

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel
}

public enum UpscalerFamily
{
    Dlss,
    Fsr,
    Xess,
    Streamline
}

[Flags]
public enum GraphicsApi
{
    None = 0,
    D3D9 = 1,
    D3D10 = 2,
    D3D11 = 4,
    D3D12 = 8,
    Vulkan = 16,
    OpenGL = 32
}

public enum ModKind
{
    OptiScaler,
    ReShade,
    SpecialK
}
