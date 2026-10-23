using System.Runtime.InteropServices;

namespace Upshift.App.Services;

/// <summary>
/// The "Show overlay while measuring" box: Upshift's own small window in a corner of the game's screen with the live
/// frame rate, the 1% low so far, the frame time and a recording dot with the time left. It's always on top,
/// see-through to clicks, never takes the focus and is semi-transparent. Nothing is injected into the game: the
/// figures come from the PresentMon capture Measure already runs. It can't show over a game in exclusive fullscreen.
/// The window lives on its own thread with its own message loop; every call here is safe from any thread.
/// </summary>
public sealed class MeasureOverlay : IDisposable
{
    public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

    private const int WmUpdate = 0x0400 + 0x51, WmQuitLoop = 0x0400 + 0x52, WmPaint = 0x000F, WmNcHitTest = 0x0084, WmDestroy = 0x0002;
    private const int HtTransparent = -1;
    private const uint WsPopup = 0x80000000;
    private const uint WsExLayered = 0x00080000, WsExTransparent = 0x00000020, WsExTopmost = 0x00000008, WsExToolWindow = 0x00000080, WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010, SwpShowWindow = 0x0040, SwpHideWindow = 0x0080;
    private const uint LwaAlpha = 0x2;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const int Width = 230, Height = 76, Margin = 16;

    private static readonly WndProc Proc = WindowProc;   // kept alive while Windows calls it
    private static readonly Dictionary<IntPtr, MeasureOverlay> Windows = new();
    private static ushort _class;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly object _sync = new();
    private IntPtr _hwnd;
    private readonly IntPtr _gameWindow;
    private Corner _corner;

    // What to draw (set under _sync).
    private string _status = "REC";
    private string _line1 = "FPS –";
    private string _line2 = "";
    private bool _visible = true;

    private MeasureOverlay(IntPtr gameWindow, Corner corner)
    {
        _gameWindow = gameWindow;
        _corner = corner;
        _thread = new Thread(Run) { IsBackground = true, Name = "Measure overlay" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Shows the overlay on the screen <paramref name="gameWindow"/> is on (the main screen when it's zero). Null when the window couldn't be made.</summary>
    public static MeasureOverlay? Show(IntPtr gameWindow, Corner corner)
    {
        var overlay = new MeasureOverlay(gameWindow, corner);
        if (overlay._hwnd != IntPtr.Zero) return overlay;
        overlay.Dispose();
        return null;
    }

    public static Corner ParseCorner(string? text) => Enum.TryParse<Corner>(text, true, out var c) ? c : Corner.TopRight;

    /// <summary>"0:42 left", "FPS 143 · 1% low 98", "Frame time 7.0 ms".</summary>
    public void Update(string status, string line1, string line2)
    {
        lock (_sync) { (_status, _line1, _line2) = (status, line1, line2); }
        Post();
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
                "UpshiftMeasureOverlay", "Upshift measuring", WsPopup, 0, 0, Width, Height, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return;
            lock (Windows) Windows[_hwnd] = this;
            SetLayeredWindowAttributes(_hwnd, 0, 220, LwaAlpha);
            Place(show: true);
        }
        finally { _ready.Set(); }

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WmQuitLoop) { DestroyWindow(_hwnd); continue; }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    /// <summary>The game's screen, the chosen corner, scaled for that screen; kept on top of the game.</summary>
    private void Place(bool show)
    {
        var monitor = MonitorFromWindow(_gameWindow, 2 /* nearest */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) info.rcMonitor = new RECT { Right = 1920, Bottom = 1080 };
        var scale = Scale(monitor);
        int w = (int)(Width * scale), h = (int)(Height * scale), m = (int)(Margin * scale);
        var r = info.rcMonitor;
        var x = _corner is Corner.TopLeft or Corner.BottomLeft ? r.Left + m : r.Right - w - m;
        var y = _corner is Corner.TopLeft or Corner.TopRight ? r.Top + m : r.Bottom - h - m;
        SetWindowPos(_hwnd, HwndTopmost, x, y, w, h, SwpNoActivate | (show ? SwpShowWindow : SwpHideWindow));
    }

    private static double Scale(IntPtr monitor) =>
        GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        MeasureOverlay? overlay;
        lock (Windows) Windows.TryGetValue(hwnd, out overlay);
        switch (message)
        {
            case WmNcHitTest:
                return new IntPtr(HtTransparent);
            case WmUpdate when overlay is not null:
                overlay.Place(overlay.Visible);
                InvalidateRect(hwnd, IntPtr.Zero, true);
                return IntPtr.Zero;
            case WmPaint when overlay is not null:
                overlay.Paint(hwnd);
                return IntPtr.Zero;
            case WmDestroy:
                lock (Windows) Windows.Remove(hwnd);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void Paint(IntPtr hwnd)
    {
        string status, line1, line2;
        lock (_sync) (status, line1, line2) = (_status, _line1, _line2);
        var dc = BeginPaint(hwnd, out var ps);
        try
        {
            GetClientRect(hwnd, out var rect);
            var scale = rect.Right / (double)Width;
            int S(double v) => (int)Math.Round(v * scale);
            var back = CreateSolidBrush(0x00201C1B);   // #1B1C20 (BGR)
            FillRect(dc, ref rect, back);
            DeleteObject(back);

            // The recording dot.
            var red = CreateSolidBrush(0x003C3CE8);    // #E83C3C
            var oldBrush = SelectObject(dc, red);
            var nullPen = GetStockObject(8 /* NULL_PEN */);
            var oldPen = SelectObject(dc, nullPen);
            Ellipse(dc, S(12), S(10), S(23), S(21));
            SelectObject(dc, oldPen);
            SelectObject(dc, oldBrush);
            DeleteObject(red);

            SetBkMode(dc, 1 /* TRANSPARENT */);
            var bold = CreateFont(-S(15), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE */, 0, "Segoe UI");
            var normal = CreateFont(-S(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            var old = SelectObject(dc, bold);
            SetTextColor(dc, 0x00FFFFFF);
            TextOut(dc, S(30), S(7), status, status.Length);
            TextOut(dc, S(12), S(29), line1, line1.Length);
            SelectObject(dc, normal);
            SetTextColor(dc, 0x00D8D4D0);
            TextOut(dc, S(12), S(51), line2, line2.Length);
            SelectObject(dc, old);
            DeleteObject(bold);
            DeleteObject(normal);
        }
        finally { EndPaint(hwnd, ref ps); }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WmQuitLoop, IntPtr.Zero, IntPtr.Zero);
        _hwnd = IntPtr.Zero;
    }

    /// <summary>
    /// True while a program runs Direct3D in exclusive fullscreen (Windows' "QUNS_RUNNING_D3D_FULL_SCREEN"), where no
    /// other window can show on top.
    /// </summary>
    public static bool ExclusiveFullscreenNow() => SHQueryUserNotificationState(out var state) == 0 && state == 3;

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
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public int message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
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
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll")] private static extern bool Ellipse(IntPtr dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOut(IntPtr dc, int x, int y, string text, int length);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}
