using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Upshift.Core.Services;

/// <summary>
/// The shortcuts and "Installed apps" entry of an installed Upshift: a Start menu and desktop "Upshift" shortcut that
/// point at the install's current\Upshift.exe, with its icon (index 0) and Velopack's app ID, and an entry icon that
/// exists. Used by Upshift.App's Services.Shortcuts, which knows whether this is an installed copy.
/// </summary>
public static class Shortcuts
{
    public const string ShortcutName = "Upshift.lnk";

    /// <summary>The app ID Velopack gives an install: "velopack." plus the package id (the install folder's name).</summary>
    public static string AppId(string installRoot) => "velopack." + Path.GetFileName(installRoot.TrimEnd(Path.DirectorySeparatorChar));

    /// <summary>
    /// Checks the "Upshift" shortcut in each folder and rewrites it when it doesn't point at this install's
    /// current\Upshift.exe with that exe's icon at index 0 and the install's app ID. Missing shortcuts are created only
    /// with <paramref name="createMissing"/>. With an uninstall key name, its DisplayIcon is fixed too when the file it
    /// names doesn't exist. Returns what was changed.
    /// </summary>
    public static List<string> Ensure(string installRoot, IEnumerable<string> shortcutFolders, bool createMissing, string? uninstallKeyName)
    {
        var changed = new List<string>();
        var exe = Path.Combine(installRoot, "current", "Upshift.exe");
        if (!File.Exists(exe)) return changed;
        var workDir = Path.GetDirectoryName(exe)!;
        var appId = AppId(installRoot);

        foreach (var folder in shortcutFolders.Where(f => !string.IsNullOrEmpty(f)))
        {
            var path = Path.Combine(folder, ShortcutName);
            if (!File.Exists(path) && !createMissing) continue;
            if (File.Exists(path) && ShellLink.Read(path) is { } current
                && SamePath(current.Target, exe) && SamePath(current.Icon, exe) && current.IconIndex == 0 && current.AppId == appId)
                continue;
            Directory.CreateDirectory(folder);
            ShellLink.Write(path, exe, workDir, exe, appId);
            changed.Add(path);
        }

        if (uninstallKeyName is not null)
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{uninstallKeyName}", writable: true);
            if (key is not null && (key.GetValue("DisplayIcon") is not string icon || !File.Exists(icon.Split(',')[0].Trim('"'))))
            {
                key.SetValue("DisplayIcon", exe);
                changed.Add("Installed apps entry icon");
            }
        }

        if (changed.Count > 0) ShellLink.RefreshIcons();
        return changed;
    }

    /// <summary>Target, icon, icon index and app ID of a shortcut, or null when it can't be read.</summary>
    public static (string Target, string Icon, int IconIndex, string? AppId)? Describe(string path) =>
        ShellLink.Read(path) is { } i ? (i.Target, i.Icon, i.IconIndex, i.AppId) : null;

    private static bool SamePath(string? a, string b) =>
        a is not null && string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(a)), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads and writes .lnk files (target, working folder, icon and the AppUserModelID property).</summary>
    private static class ShellLink
    {
        public sealed record Info(string Target, string Icon, int IconIndex, string? AppId);

        private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
        private static readonly Guid AppUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private const uint AppUserModelIdProperty = 5;
        private const ushort VtLpwstr = 31;

        // PROPERTYKEY (16-byte GUID + 4-byte id) and PROPVARIANT (24 bytes on x64) are passed as raw buffers: struct
        // marshalling of these returned garbage under .NET 8, though the same declarations work on .NET Framework.
        private const int PropertyKeySize = 20, PropVariantSize = 24;

        private static IntPtr AllocKey()
        {
            var key = Marshal.AllocCoTaskMem(PropertyKeySize);
            Marshal.Copy(AppUserModelFormat.ToByteArray(), 0, key, 16);
            Marshal.WriteInt32(key, 16, (int)AppUserModelIdProperty);
            return key;
        }

        private static IntPtr AllocVariant()
        {
            var value = Marshal.AllocCoTaskMem(PropVariantSize);
            for (var i = 0; i < PropVariantSize; i++) Marshal.WriteByte(value, i, 0);
            return value;
        }

        private static IShellLinkW Create() => (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClsid)!)!;

        public static Info? Read(string path)
        {
            try
            {
                var link = Create();
                ((IPersistFile)link).Load(path, 0);
                var target = new StringBuilder(1024);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                var icon = new StringBuilder(1024);
                link.GetIconLocation(icon, icon.Capacity, out var index);
                string? appId = null;
                var key = AllocKey();
                var value = AllocVariant();
                try
                {
                    ((IPropertyStore)link).GetValue(key, value);
                    if ((ushort)Marshal.ReadInt16(value) == VtLpwstr) appId = Marshal.PtrToStringUni(Marshal.ReadIntPtr(value, 8));
                }
                finally
                {
                    PropVariantClear(value);
                    Marshal.FreeCoTaskMem(value);
                    Marshal.FreeCoTaskMem(key);
                }
                return new Info(target.ToString(), icon.ToString(), index, appId);
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { return null; }
        }

        public static void Write(string path, string target, string workDir, string icon, string appId)
        {
            var link = Create();
            link.SetPath(target);
            link.SetWorkingDirectory(workDir);
            link.SetIconLocation(icon, 0);
            link.SetDescription("Upshift");
            var store = (IPropertyStore)link;
            var key = AllocKey();
            var value = AllocVariant();
            var text = Marshal.StringToCoTaskMemUni(appId);
            try
            {
                Marshal.WriteInt16(value, (short)VtLpwstr);
                Marshal.WriteIntPtr(value, 8, text);
                store.SetValue(key, value);
                store.Commit();
            }
            finally
            {
                Marshal.FreeCoTaskMem(text);
                Marshal.FreeCoTaskMem(value);
                Marshal.FreeCoTaskMem(key);
            }
            ((IPersistFile)link).Save(path, true);
        }

        /// <summary>Tells Explorer icons changed, so the Start menu and taskbar redraw them.</summary>
        public static void RefreshIcons() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(IntPtr value);

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid classId);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string file, bool remember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
            void GetCurFile(out IntPtr file);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, IntPtr key);
            void GetValue(IntPtr key, IntPtr value);
            void SetValue(IntPtr key, IntPtr value);
            void Commit();
        }
    }
}
