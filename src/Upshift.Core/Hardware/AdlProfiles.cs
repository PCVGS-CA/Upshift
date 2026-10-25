using System.Runtime.InteropServices;

namespace Upshift.Core.Hardware;

/// <summary>
/// AMD's Display Library (ADL) application profiles, from atiadlxx.dll, which AMD's driver installs. Signatures are from
/// AMD's ADL SDK documentation (GPUOpen display-library, group__APPPROFILESAPI): ADL2_Main_Control_Create/Destroy,
/// ADL2_ApplicationProfiles_ProfileApplicationX2_Assign, _RemoveApplication, _ProfileOfAnApplicationX2_Search,
/// _Profile_Exist and _System_Reload. Nothing is bundled; on a PC without AMD's driver, Open says so. Never throws.
/// </summary>
public sealed class AdlProfiles : IDisposable
{
    private const int AdlOk = 0, AdlOkWarning = 1;
    private readonly IntPtr _library;
    private IntPtr _context;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr MallocCallback(int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ControlCreate(MallocCallback callback, int enumConnectedAdapters, out IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ControlDestroy(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int AssignX2(IntPtr context, string fileName, string path, string version, string title, string driverArea, string profileName);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int RemoveApplication(IntPtr context, string fileName, string path, string version, string driverArea);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int SearchX2(IntPtr context, string fileName, string path, string version, string area, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int ProfileExist(IntPtr context, string driverArea, string profileName, out int exists);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SystemReload(IntPtr context);

    // ADL keeps the callback for the context's life: it's static so it's never collected.
    private static readonly MallocCallback Malloc = size => Marshal.AllocHGlobal(size);

    private AdlProfiles(IntPtr library) => _library = library;

    /// <summary>ADL with a context of its own, or null and why (no AMD driver, a driver without application profiles…).</summary>
    public static AdlProfiles? Open(out string? problem)
    {
        problem = null;
        try
        {
            if (!NativeLibrary.TryLoad("atiadlxx.dll", out var library))
            {
                problem = "AMD's driver library (atiadlxx.dll) isn't on this PC.";
                return null;
            }
            var adl = new AdlProfiles(library);
            if (adl.Get<ControlCreate>("ADL2_Main_Control_Create") is not { } create || create(Malloc, 1, out adl._context) != AdlOk || adl._context == IntPtr.Zero)
            {
                adl.Dispose();
                problem = "AMD's driver library couldn't be started.";
                return null;
            }
            return adl;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problem = $"AMD's driver library couldn't be used: {ex.Message}";
            return null;
        }
    }

    /// <summary>True when the driver area has this profile (read only).</summary>
    public bool? ProfileExists(string area, string profile) =>
        Get<ProfileExist>("ADL2_ApplicationProfiles_Profile_Exist") is { } f && f(_context, area, profile, out var exists) is AdlOk or AdlOkWarning
            ? exists != 0 : null;

    /// <summary>True when the exe already has a use record in this driver area (read only; null when ADL can't say).</summary>
    public bool? HasRecord(string fileName, string path, string area)
    {
        if (Get<SearchX2>("ADL2_ApplicationProfiles_ProfileOfAnApplicationX2_Search") is not { } f) return null;
        var result = f(_context, fileName, path, "", area, out var profile);
        if (profile != IntPtr.Zero) Marshal.FreeHGlobal(profile);
        return result is AdlOk or AdlOkWarning ? profile != IntPtr.Zero : result == -1 /* ADL_ERR: none found */ ? false : null;
    }

    /// <summary>Assigns the profile to the exe in that area (a new use record, or the area's existing one). Reason or null.</summary>
    public string? Assign(string fileName, string path, string title, string area, string profile)
    {
        if (Get<AssignX2>("ADL2_ApplicationProfiles_ProfileApplicationX2_Assign") is not { } f) return "This AMD driver doesn't offer application profiles.";
        var result = f(_context, fileName, path, "", title, area, profile);
        if (result is not (AdlOk or AdlOkWarning)) return $"AMD's driver refused it (ADL error {result}).";
        Reload();
        return null;
    }

    /// <summary>Removes the exe's use record in that area. Reason or null.</summary>
    public string? Remove(string fileName, string path, string area)
    {
        if (Get<RemoveApplication>("ADL2_ApplicationProfiles_RemoveApplication") is not { } f) return "This AMD driver doesn't offer application profiles.";
        var result = f(_context, fileName, path, "", area);
        if (result is not (AdlOk or AdlOkWarning)) return $"AMD's driver refused it (ADL error {result}).";
        Reload();
        return null;
    }

    /// <summary>The driver reads its profiles again, so the change applies without a restart.</summary>
    private void Reload()
    {
        if (Get<SystemReload>("ADL2_ApplicationProfiles_System_Reload") is { } f) f(_context);
    }

    private T? Get<T>(string name) where T : Delegate =>
        NativeLibrary.TryGetExport(_library, name, out var address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;

    public void Dispose()
    {
        try
        {
            if (_context != IntPtr.Zero && Get<ControlDestroy>("ADL2_Main_Control_Destroy") is { } destroy) destroy(_context);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _context = IntPtr.Zero;
        if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
    }
}
