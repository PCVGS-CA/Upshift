using System.Runtime.InteropServices;

namespace Upshift.App.Helpers;

/// <summary>One monitor attached to the desktop, with the refresh rate currently set for it in Windows.</summary>
/// <param name="DeviceName">Windows' name for it, e.g. \\.\DISPLAY1 (stable while the setup doesn't change).</param>
/// <param name="Name">The monitor's own name ("LG ULTRAGEAR"), or "Display 1" when Windows doesn't give one.</param>
/// <param name="RefreshHz">The rate set in Windows (Settings, Display, Advanced display), not the monitor's maximum.</param>
public sealed record DisplayInfo(string DeviceName, string Name, bool IsPrimary, int RefreshHz, int Width, int Height)
{
    /// <summary>"LG ULTRAGEAR (main) · 2560 × 1440 · 144 Hz".</summary>
    public string Label => $"{Name}{(IsPrimary ? " (main)" : "")} · {Width} × {Height} · {RefreshHz} Hz";
}

/// <summary>The monitors and their refresh rates, as currently set in Windows.</summary>
public static class Display
{
    private const int EnumCurrentSettings = -1;
    private const int AttachedToDesktop = 0x1, PrimaryDevice = 0x4;

    /// <summary>Every monitor attached to the desktop, the main one first.</summary>
    public static List<DisplayInfo> All()
    {
        var names = FriendlyNames();
        var list = new List<DisplayInfo>();
        try
        {
            for (var i = 0; ; i++)
            {
                var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevicesW(null, i, ref device, 0)) break;
                if ((device.StateFlags & AttachedToDesktop) == 0) continue;
                var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettingsW(device.DeviceName, EnumCurrentSettings, ref mode) || mode.dmDisplayFrequency <= 1) continue;
                var name = names.TryGetValue(device.DeviceName, out var n) && n.Length > 0 ? n : $"Display {list.Count + 1}";
                list.Add(new DisplayInfo(device.DeviceName, name, (device.StateFlags & PrimaryDevice) != 0,
                    mode.dmDisplayFrequency, mode.dmPelsWidth, mode.dmPelsHeight));
            }
        }
        catch (Exception) { /* no list: the caller says the rate is unknown */ }
        return list.OrderByDescending(d => d.IsPrimary).ToList();
    }

    /// <summary>The chosen monitor when it's still attached, otherwise the main one.</summary>
    public static DisplayInfo? Pick(IReadOnlyList<DisplayInfo> all, string? deviceName) =>
        all.FirstOrDefault(d => d.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
        ?? all.FirstOrDefault(d => d.IsPrimary) ?? all.FirstOrDefault();

    // ---------------- Monitor names (Windows' display configuration) ----------------

    /// <summary>\\.\DISPLAY1 → "LG ULTRAGEAR", from the active display paths. Empty when Windows won't say.</summary>
    private static Dictionary<string, string> FriendlyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0) return result;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return result;
            for (var i = 0; i < pathCount; i++)
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = 1, size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = paths[i].sourceAdapterId, id = paths[i].sourceId
                    }
                };
                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = 2, size = Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = paths[i].targetAdapterId, id = paths[i].targetId
                    }
                };
                if (DisplayConfigGetDeviceInfo(ref source) == 0 && DisplayConfigGetDeviceInfo(ref target) == 0)
                    result[source.viewGdiDeviceName] = target.monitorFriendlyDeviceName ?? "";
            }
        }
        catch (Exception) { }
        return result;
    }

    private const uint QdcOnlyActivePaths = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public LUID sourceAdapterId;
        public uint sourceId, sourceModeInfoIdx, sourceStatusFlags;
        public LUID targetAdapterId;
        public uint targetId, targetModeInfoIdx;
        public int outputTechnology, rotation, scaling;
        public uint refreshNumerator, refreshDenominator;
        public int scanLineOrdering, targetAvailable;
        public uint targetStatusFlags, flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO { public int infoType; public uint id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public int type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public int outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);

    // ---------------- Modes ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, int devNum, ref DISPLAY_DEVICE displayDevice, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}
