using System.Runtime.InteropServices;

namespace Upshift.App.Services;

/// <summary>
/// One system-wide key (no modifiers) that works while a game has the focus, registered with Windows only while a
/// measurement is armed. Runs its own small message loop on a background thread; Pressed is raised there.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312, WmQuit = 0x0012, HotkeyId = 0x5550;
    private const uint ModNoRepeat = 0x4000;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private bool _registered;

    public event Action? Pressed;

    private GlobalHotkey(int virtualKey)
    {
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _registered = RegisterHotKey(IntPtr.Zero, HotkeyId, ModNoRepeat, (uint)virtualKey);
            _ready.Set();
            if (!_registered) return;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                if (msg.message == WmHotkey && msg.wParam == (IntPtr)HotkeyId) Pressed?.Invoke();
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
        }) { IsBackground = true, Name = "Measure hotkey" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Registers the key; null when another program already uses it.</summary>
    public static GlobalHotkey? TryRegister(int virtualKey)
    {
        var hotkey = new GlobalHotkey(virtualKey);
        return hotkey._registered ? hotkey : null;
    }

    public void Dispose()
    {
        if (_registered && _threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _registered = false;
    }

    /// <summary>"F10", "Scroll Lock"…</summary>
    public static string KeyName(int virtualKey) => virtualKey switch
    {
        >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",
        0x91 => "Scroll Lock",
        0x13 => "Pause",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "Page Up",
        0x22 => "Page Down",
        _ => $"key 0x{virtualKey:X2}"
    };

    /// <summary>The keys offered in Settings: F1–F12, Scroll Lock and Pause (OptiScaler's keys aren't among them).</summary>
    public static IReadOnlyList<int> OfferedKeys { get; } = Enumerable.Range(0x70, 12).Append(0x91).Append(0x13).ToList();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public int message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
