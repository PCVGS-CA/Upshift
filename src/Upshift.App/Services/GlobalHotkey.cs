using System.Runtime.InteropServices;

namespace Upshift.App.Services;

/// <summary>
/// One system-wide key (no modifiers) that works while a game has the focus, watched only while a measurement is
/// armed. Two ways, because games differ: a hotkey registered with Windows, and a low-level keyboard hook that sees
/// the key even when the game turns hotkeys off (raw input with RIDEV_NOHOTKEYS) or reads the keyboard itself. The
/// hook only watches; every key still goes to the game. Both run on a background thread with its own message loop,
/// and a press seen by both is raised once. Pressed is raised on that thread.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312, WmQuit = 0x0012, WmHookPressed = 0x8000 + 0x5551, HotkeyId = 0x5550;
    private const int WhKeyboardLl = 13, WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const uint ModNoRepeat = 0x4000;
    /// <summary>A press seen by both ways arrives twice within a few milliseconds; presses closer than this count once.</summary>
    private const long SamePressMs = 400;
    private readonly int _virtualKey;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private bool _registered;
    private IntPtr _hook;
    private HookProc? _hookProc;   // kept so the delegate isn't collected while Windows calls it
    private bool _down;
    private long _lastPressed = long.MinValue / 2;
    private volatile bool _running;

    public event Action? Pressed;

    private GlobalHotkey(int virtualKey)
    {
        _virtualKey = virtualKey;
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _registered = RegisterHotKey(IntPtr.Zero, HotkeyId, ModNoRepeat, (uint)virtualKey);
            _hookProc = OnKeyboard;
            _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
            _running = _registered || _hook != IntPtr.Zero;
            _ready.Set();
            if (!_running) return;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                if ((msg.message == WmHotkey && msg.wParam == (IntPtr)HotkeyId) || msg.message == WmHookPressed) Raise();
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            if (_registered) UnregisterHotKey(IntPtr.Zero, HotkeyId);
        }) { IsBackground = true, Name = "Measure hotkey" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Starts watching the key; null when neither way could be set up.</summary>
    public static GlobalHotkey? TryRegister(int virtualKey)
    {
        var hotkey = new GlobalHotkey(virtualKey);
        return hotkey._running ? hotkey : null;
    }

    private void Raise()
    {
        var now = Environment.TickCount64;
        if (now - _lastPressed < SamePressMs) return;
        _lastPressed = now;
        Pressed?.Invoke();
    }

    /// <summary>
    /// Windows calls this for every key press on the PC while the hook is set, so it only notes the first press of the
    /// watched key (not auto-repeat) and posts it to the loop; the key itself is passed on unchanged.
    /// </summary>
    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var vk = Marshal.ReadInt32(data);
            if (vk == _virtualKey)
            {
                var kind = (int)message;
                if (kind is WmKeyDown or WmSysKeyDown)
                {
                    if (!_down) PostThreadMessage(_threadId, WmHookPressed, IntPtr.Zero, IntPtr.Zero);
                    _down = true;
                }
                else if (kind is WmKeyUp or WmSysKeyUp) _down = false;
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    public void Dispose()
    {
        if (_running && _threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _running = false;
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

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public int message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
