using System.Runtime.InteropServices;
using Upshift.Core.Measure;

namespace Upshift.App.Services;

/// <summary>
/// The Measure overlay: Upshift's own window in a corner of the game's screen (see <see cref="OverlayRenderer"/> for the
/// look). It's always on top, see-through to clicks and never takes the focus. Nothing is injected into the game: the
/// figures come from Measure's PresentMon capture and Windows' counters, so it works in every game, except over exclusive
/// fullscreen. The window lives on its own thread with its own message loop; every call here is safe from any thread.
/// </summary>
public sealed class MeasureOverlay : IDisposable
{
    public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

    private const int WmUpdate = 0x0400 + 0x51, WmQuitLoop = 0x0400 + 0x52, WmNcHitTest = 0x0084, WmDestroy = 0x0002;
    private const int HtTransparent = -1;
    private const uint WsPopup = 0x80000000;
    private const uint WsExLayered = 0x00080000, WsExTransparent = 0x00000020, WsExTopmost = 0x00000008, WsExToolWindow = 0x00000080, WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010, SwpNoSize = 0x0001, SwpNoMove = 0x0002;
    private const int SwShowNoActivate = 4, SwHide = 0;
    private const int Margin = 16;
    private static readonly IntPtr HwndTopmost = new(-1);

    private static readonly WndProc Proc = WindowProc;   // kept alive while Windows calls it
    private static readonly Dictionary<IntPtr, MeasureOverlay> Windows = new();
    private static ushort _class;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly object _sync = new();
    private IntPtr _hwnd;
    private IntPtr _gameWindow;

    // What to draw (set under _sync).
    private OverlayFrame? _frame;
    private OverlayRenderer.Look _look;
    private Corner _corner;
    private bool _visible = true;
    private double _fade = 1;

    private MeasureOverlay(IntPtr gameWindow, Corner corner, OverlayRenderer.Look look)
    {
        _gameWindow = gameWindow;
        _corner = corner;
        _look = look;
        _thread = new Thread(Run) { IsBackground = true, Name = "Measure overlay" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Makes the overlay for the screen <paramref name="gameWindow"/> is on (the main screen when it's zero); it shows with the first <see cref="Update"/>. Null when the window couldn't be made.</summary>
    public static MeasureOverlay? Show(IntPtr gameWindow, Corner corner, OverlayRenderer.Look look)
    {
        var overlay = new MeasureOverlay(gameWindow, corner, look);
        if (overlay._hwnd != IntPtr.Zero) return overlay;
        overlay.Dispose();
        return null;
    }

    public static Corner ParseCorner(string? text) => Enum.TryParse<Corner>(text, true, out var c) ? c : Corner.TopRight;

    /// <summary>New figures (at most four times a second from Measure).</summary>
    public void Update(OverlayFrame frame)
    {
        lock (_sync) _frame = frame;
        Post();
    }

    /// <summary>Settings changed while it shows (style, opacity, corner).</summary>
    public void Restyle(OverlayRenderer.Look look, Corner corner)
    {
        lock (_sync) (_look, _corner) = (look, corner);
        Post();
    }

    /// <summary>The result's fade-out: 1 is fully shown, 0 gone.</summary>
    public void Fade(double opacity)
    {
        lock (_sync) _fade = Math.Clamp(opacity, 0, 1);
        Post();
    }

    /// <summary>The game's window once it has one (a game can start without a window): the overlay moves to its screen.</summary>
    public void Follow(IntPtr gameWindow)
    {
        if (gameWindow != IntPtr.Zero) lock (_sync) _gameWindow = gameWindow;
    }

    /// <summary>Shift + the measuring key: hidden and shown again; measuring goes on either way.</summary>
    public bool Visible
    {
        get { lock (_sync) return _visible; }
        set { lock (_sync) _visible = value; Post(); }
    }

    private void Post()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WmUpdate, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        try
        {
            var module = GetModuleHandle(null);
            lock (Windows)
            {
                if (_class == 0)
                {
                    var wc = new WNDCLASSEX
                    {
                        cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                        hInstance = module, lpszClassName = "UpshiftMeasureOverlay", hCursor = IntPtr.Zero
                    };
                    _class = RegisterClassEx(ref wc);
                }
            }
            _hwnd = CreateWindowEx(WsExLayered | WsExTransparent | WsExTopmost | WsExToolWindow | WsExNoActivate,
                "UpshiftMeasureOverlay", "Upshift measuring", WsPopup, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return;
            lock (Windows) Windows[_hwnd] = this;
        }
        finally { _ready.Set(); }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            // The message's own window: Dispose has already cleared _hwnd by the time this arrives.
            if (msg.message == WmQuitLoop) { DestroyWindow(msg.hwnd); continue; }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    /// <summary>Draws the latest frame into the window, sized for the game's screen and placed in the chosen corner, on top.</summary>
    private void Draw(IntPtr hwnd)
    {
        OverlayFrame? frame;
        OverlayRenderer.Look look;
        Corner corner;
        bool visible;
        double fade;
        IntPtr game;
        lock (_sync) (frame, look, corner, visible, fade, game) = (_frame, _look, _corner, _visible, _fade, _gameWindow);
        if (frame is null || !visible || fade <= 0)
        {
            ShowWindow(hwnd, SwHide);
            return;
        }

        var monitor = MonitorFromWindow(game, 2 /* nearest */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) info.rcMonitor = new RECT { Right = 1920, Bottom = 1080 };
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
        var (pixels, w, h) = OverlayRenderer.Render(frame, look, scale);
        var m = (int)(Margin * scale);
        var r = info.rcMonitor;
        var position = new POINT
        {
            X = corner is Corner.TopLeft or Corner.BottomLeft ? r.Left + m : r.Right - w - m,
            Y = corner is Corner.TopLeft or Corner.TopRight ? r.Top + m : r.Bottom - h - m
        };

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(memory, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(memory, bitmap);
        try
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var size = new SIZE { cx = w, cy = h };
            var source = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = (byte)Math.Round(255 * fade), AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(hwnd, screen, ref position, ref size, memory, ref source, 0, ref blend, 2 /* ULW_ALPHA */);
            // Kept above the game (a game going fullscreen can push it down).
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
            ShowWindow(hwnd, SwShowNoActivate);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        MeasureOverlay? overlay;
        lock (Windows) Windows.TryGetValue(hwnd, out overlay);
        switch (message)
        {
            case WmNcHitTest:
                return new IntPtr(HtTransparent);
            case WmUpdate when overlay is not null:
                try { overlay.Draw(hwnd); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { CrashLog.Write(ex, "MeasureOverlay"); }
                return IntPtr.Zero;
            case WmDestroy:
                lock (Windows) Windows.Remove(hwnd);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WmQuitLoop, IntPtr.Zero, IntPtr.Zero);
        _hwnd = IntPtr.Zero;
    }

    // ---------------- Win32 ----------------

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public int message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screen, ref POINT position, ref SIZE size, IntPtr source, ref POINT sourcePoint, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
