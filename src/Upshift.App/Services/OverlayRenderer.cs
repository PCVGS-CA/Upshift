using System.Globalization;
using System.Runtime.InteropServices;
using Upshift.Core.Measure;

namespace Upshift.App.Services;

/// <summary>
/// Draws the Measure overlay into a premultiplied BGRA image: a dark see-through panel (the chosen opacity) with rounded
/// corners and a thin light border, solid text and lines on top, Upshift's teal accent. The same image goes to the overlay
/// window (UpdateLayeredWindow) and to the live preview in Settings. GDI only; numbers use Segoe UI, whose digits all have
/// the same width, and sit in fixed boxes, so they don't jitter.
/// </summary>
public static class OverlayRenderer
{
    /// <summary>Full or Minimal, and the panel's opacity (30 to 100 percent).</summary>
    public sealed record Look(bool Minimal, int OpacityPercent);

    public const int FullWidth = 324, MinimalWidth = 228;
    private const int Pad = 14, Gap = 6, Radius = 10;

    // Colours (GDI COLORREF is 0x00BBGGRR). The background fills are see-through; everything else is solid.
    private const uint Panel = 0x001C1816, TileBack = 0x002C2723, GraphBack = 0x00262220, Track = 0x003A3633;
    private const uint White = 0x00F7F4F2, Grey = 0x00ABA19A, Teal = 0x00A3B32B, Red = 0x004D48E5, Yellow = 0x004CC9F2, GridLine = 0x00504A46;

    public static (byte[] Pixels, int Width, int Height) Render(OverlayFrame frame, Look look, double scale)
    {
        int S(double v) => (int)Math.Round(v * scale);
        var width = S(look.Minimal ? MinimalWidth : FullWidth);
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var fonts = new Fonts(S);
        try
        {
            SetBkMode(dc, 1 /* TRANSPARENT */);
            var height = look.Minimal ? S(34) : Layout(dc, frame, fonts, S, width, draw: false);
            var info = new BITMAPINFO { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
            var old = SelectObject(dc, bitmap);
            try
            {
                Fill(dc, 0, 0, width, height, Panel);
                if (look.Minimal) Minimal(dc, frame, fonts, S, width);
                else Layout(dc, frame, fonts, S, width, draw: true);
                GdiFlush();
                var pixels = new byte[width * height * 4];
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                Finish(pixels, width, height, S(Radius), Math.Max(1, S(1)), look.OpacityPercent);
                return (pixels, width, height);
            }
            finally
            {
                SelectObject(dc, old);
                DeleteObject(bitmap);
            }
        }
        finally
        {
            fonts.Dispose();
            DeleteDC(dc);
        }
    }

    private sealed class Fonts : IDisposable
    {
        public readonly IntPtr Big, Title, Value, Label, Small;
        public Fonts(Func<double, int> S)
        {
            // ANTIALIASED_QUALITY (4): grey smoothing, which survives the see-through panel (ClearType would fringe).
            Big = CreateFont(-S(40), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
            Title = CreateFont(-S(13), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
            Value = CreateFont(-S(16), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
            Label = CreateFont(-S(11), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
            Small = CreateFont(-S(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
        }
        public void Dispose() { foreach (var f in new[] { Big, Title, Value, Label, Small }) DeleteObject(f); }
    }

    private static uint DotColour(OverlayPhase phase) => phase switch { OverlayPhase.Ready => Yellow, OverlayPhase.Recording => Red, _ => Teal };

    /// <summary>The full panel, top to bottom. Measures only (returns the height) when <paramref name="draw"/> is false.</summary>
    private static int Layout(IntPtr dc, OverlayFrame f, Fonts fonts, Func<double, int> S, int width, bool draw)
    {
        int left = S(Pad), right = width - S(Pad), y = S(12);

        // Header: the state's dot and name, "Upshift" on the right.
        if (draw)
        {
            Dot(dc, left, y + S(4), S(10), DotColour(f.Phase));
            Text(dc, fonts.Title, White, left + S(16), y, f.Phase switch { OverlayPhase.Ready => "Ready", OverlayPhase.Recording => "Recording", _ => "Result" });
            TextRight(dc, fonts.Label, Grey, right, y + S(2), "Upshift");
        }
        y += S(24);

        // The big live FPS.
        if (f.Phase != OverlayPhase.Result && f.ShowFps)
        {
            if (draw)
            {
                var number = f.Fps is { } fps ? Math.Round(fps).ToString("0", CultureInfo.InvariantCulture) : OverlayItems.Dash;
                var w = Text(dc, fonts.Big, White, left - S(2), y - S(4), number);
                Text(dc, fonts.Small, Grey, left + w + S(4), y + S(22), "FPS");
            }
            y += S(48);
        }

        // The frame-time line, last ~5 seconds.
        if (f.ShowGraph)
        {
            if (draw) Graph(dc, f.Graph, left, y, right, y + S(44), S);
            y += S(44) + S(Gap + 2);
        }

        // Tiles, three per row.
        if (f.Tiles.Count > 0)
        {
            var tileW = (right - left - 2 * S(Gap)) / 3;
            var tileH = S(42);
            for (var i = 0; i < f.Tiles.Count; i++)
            {
                var col = i % 3;
                var row = i / 3;
                var x = left + col * (tileW + S(Gap));
                var ty = y + row * (tileH + S(Gap));
                if (!draw) continue;
                RoundFill(dc, x, ty, x + tileW, ty + tileH, S(6), TileBack);
                Clipped(dc, fonts.Label, Grey, x + S(7), ty + S(4), x + tileW - S(2), f.Tiles[i].Label);
                Clipped(dc, fonts.Value, White, x + S(7), ty + S(18), x + tileW - S(2), f.Tiles[i].Value);
            }
            var rows = (f.Tiles.Count + 2) / 3;
            y += rows * tileH + (rows - 1) * S(Gap) + S(Gap + 2);
        }

        // The result's verdict, wrapped.
        if (f.Verdict is { Length: > 0 } verdict)
        {
            var h = Wrapped(dc, fonts.Small, White, left, y, right, verdict, draw);
            y += h + S(4);
        }

        // Progress bar with elapsed / total.
        if (f.Progress is { } progress)
        {
            if (draw)
            {
                RoundFill(dc, left, y + S(4), right, y + S(8), S(2), Track);
                var done = left + (int)Math.Round((right - left) * progress);
                if (done > left + S(2)) RoundFill(dc, left, y + S(4), done, y + S(8), S(2), Teal);
                if (f.Timer is { } timer) TextRight(dc, fonts.Label, Grey, right, y + S(12), timer);
            }
            y += S(14);
        }

        // Footer: what the key does now, or how long it measured.
        if (draw) Text(dc, fonts.Label, Grey, left, y + (f.Progress is null ? 0 : S(-2)), f.Footer);
        y += S(18);
        return y + S(10);
    }

    /// <summary>Minimal: one line with the dot, the first ticked value and the timer.</summary>
    private static void Minimal(IntPtr dc, OverlayFrame f, Fonts fonts, Func<double, int> S, int width)
    {
        Dot(dc, S(12), S(12), S(10), DotColour(f.Phase));
        Text(dc, fonts.Value, White, S(28), S(6), f.MinimalValue);
        TextRight(dc, fonts.Small, Grey, width - S(12), S(9), f.MinimalTimer);
    }

    /// <summary>
    /// The frame-time line: time runs left to right over the last ~5 seconds, frame time upwards, so a stutter is a
    /// spike. The top is at least 33 ms and at least twice the median frame time; a faint line marks the median.
    /// </summary>
    private static void Graph(IntPtr dc, IReadOnlyList<double> graph, int left, int top, int right, int bottom, Func<double, int> S)
    {
        RoundFill(dc, left, top, right, bottom, S(6), GraphBack);
        if (graph.Count < 2) return;
        var sorted = graph.OrderBy(v => v).ToArray();
        var median = sorted[sorted.Length / 2];
        var max = Math.Max(Math.Max(33.4, median * 2), sorted[^1] * 1.1);
        var total = graph.Sum();
        int inset = S(4), x0 = left + inset, x1 = right - inset, y0 = top + inset, y1 = bottom - inset;
        int Y(double ms) => y1 - (int)Math.Round(Math.Min(ms, max) / max * (y1 - y0));

        var grid = CreatePen(0, 1, GridLine);
        var oldPen = SelectObject(dc, grid);
        MoveToEx(dc, x0, Y(median), IntPtr.Zero);
        LineTo(dc, x1, Y(median));
        SelectObject(dc, oldPen);
        DeleteObject(grid);

        var points = new POINT[graph.Count];
        double clock = 0;
        for (var i = 0; i < graph.Count; i++)
        {
            clock += graph[i];
            points[i] = new POINT { X = x0 + (int)Math.Round(clock / total * (x1 - x0)), Y = Y(graph[i]) };
        }
        var line = CreatePen(0, Math.Max(1, S(1.5)), Teal);
        oldPen = SelectObject(dc, line);
        Polyline(dc, points, points.Length);
        SelectObject(dc, oldPen);
        DeleteObject(line);
    }

    /// <summary>
    /// Alpha: the background fills take the chosen opacity, everything drawn on them is solid; outside the rounded corners is
    /// clear, and a thin light border runs along the edge. Then premultiplied, as UpdateLayeredWindow and XAML want.
    /// </summary>
    private static void Finish(byte[] px, int w, int h, int radius, int border, int opacityPercent)
    {
        var panelAlpha = Math.Clamp(opacityPercent, 30, 100) * 255 / 100;
        var see = new HashSet<uint> { Panel, TileBack, GraphBack, Track };
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var colour = (uint)(px[i + 2] | px[i + 1] << 8 | px[i] << 16);   // BGRA bytes → 0x00BBGGRR
                double a = see.Contains(colour) ? panelAlpha : 255;
                var cover = Coverage(x + 0.5, y + 0.5, w, h, radius);
                var edge = cover - Coverage(x + 0.5, y + 0.5, w, h, radius, border);
                if (edge > 0)
                {
                    // The border: a light line over whatever is there.
                    const double borderAlpha = 0.32;
                    var mix = edge * borderAlpha;
                    for (var c = 0; c < 3; c++) px[i + c] = (byte)(px[i + c] * (1 - mix) + 235 * mix);
                    a = Math.Max(a, 255 * borderAlpha + a * (1 - borderAlpha));
                }
                a *= cover;
                px[i + 3] = (byte)Math.Round(a);
                for (var c = 0; c < 3; c++) px[i + c] = (byte)Math.Round(px[i + c] * a / 255);
            }
    }

    /// <summary>How much of the pixel at (px, py) lies inside the rounded rectangle, inset by <paramref name="inset"/> (anti-aliased by distance).</summary>
    private static double Coverage(double px, double py, int w, int h, int radius, int inset = 0)
    {
        double left = inset, top = inset, right = w - inset, bottom = h - inset, r = Math.Max(0, radius - inset);
        if (px < left || px > right || py < top || py > bottom) return 0;
        double cx = Math.Clamp(px, left + r, right - r), cy = Math.Clamp(py, top + r, bottom - r);
        var d = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        var inside = r - d;
        var edgeDistance = Math.Min(Math.Min(px - left, right - px), Math.Min(py - top, bottom - py));
        return Math.Clamp(Math.Min(inside + 0.5, edgeDistance + 0.5), 0, 1);
    }

    // ---------------- drawing helpers ----------------

    private static void Fill(IntPtr dc, int left, int top, int right, int bottom, uint colour)
    {
        var brush = CreateSolidBrush(colour);
        var rect = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        FillRect(dc, ref rect, brush);
        DeleteObject(brush);
    }

    private static void RoundFill(IntPtr dc, int left, int top, int right, int bottom, int radius, uint colour)
    {
        var brush = CreateSolidBrush(colour);
        var pen = CreatePen(0, 1, colour);
        var oldBrush = SelectObject(dc, brush);
        var oldPen = SelectObject(dc, pen);
        RoundRect(dc, left, top, right, bottom, radius * 2, radius * 2);
        SelectObject(dc, oldPen);
        SelectObject(dc, oldBrush);
        DeleteObject(pen);
        DeleteObject(brush);
    }

    private static void Dot(IntPtr dc, int left, int top, int size, uint colour)
    {
        var brush = CreateSolidBrush(colour);
        var oldBrush = SelectObject(dc, brush);
        var oldPen = SelectObject(dc, GetStockObject(8 /* NULL_PEN */));
        Ellipse(dc, left, top, left + size + 1, top + size + 1);
        SelectObject(dc, oldPen);
        SelectObject(dc, oldBrush);
        DeleteObject(brush);
    }

    /// <summary>Draws one line of text; returns its width.</summary>
    private static int Text(IntPtr dc, IntPtr font, uint colour, int x, int y, string text)
    {
        var old = SelectObject(dc, font);
        SetTextColor(dc, colour);
        GetTextExtentPoint32(dc, text, text.Length, out var size);
        TextOut(dc, x, y, text, text.Length);
        SelectObject(dc, old);
        return size.cx;
    }

    /// <summary>One line that ends in "…" rather than running past <paramref name="right"/>.</summary>
    private static void Clipped(IntPtr dc, IntPtr font, uint colour, int left, int top, int right, string text)
    {
        var old = SelectObject(dc, font);
        SetTextColor(dc, colour);
        var rect = new RECT { Left = left, Top = top, Right = right, Bottom = top + 1000 };
        DrawText(dc, text, text.Length, ref rect, 0x0020 /* SINGLELINE */ | 0x8000 /* END_ELLIPSIS */ | 0x0800 /* NOPREFIX */);
        SelectObject(dc, old);
    }

    private static void TextRight(IntPtr dc, IntPtr font, uint colour, int right, int y, string text)
    {
        var old = SelectObject(dc, font);
        GetTextExtentPoint32(dc, text, text.Length, out var size);
        SelectObject(dc, old);
        Text(dc, font, colour, right - size.cx, y, text);
    }

    /// <summary>Word-wrapped text between <paramref name="left"/> and <paramref name="right"/>; returns its height.</summary>
    private static int Wrapped(IntPtr dc, IntPtr font, uint colour, int left, int top, int right, string text, bool draw)
    {
        var old = SelectObject(dc, font);
        SetTextColor(dc, colour);
        var rect = new RECT { Left = left, Top = top, Right = right, Bottom = top + 1000 };
        DrawText(dc, text, text.Length, ref rect, 0x0010 /* WORDBREAK */ | 0x0400 /* CALCRECT */);
        if (draw) DrawText(dc, text, text.Length, ref rect, 0x0010);
        SelectObject(dc, old);
        return rect.Bottom - rect.Top;
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint colour);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, uint colour);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("gdi32.dll")] private static extern bool RoundRect(IntPtr dc, int left, int top, int right, int bottom, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool Ellipse(IntPtr dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool MoveToEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll")] private static extern bool LineTo(IntPtr dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool Polyline(IntPtr dc, POINT[] points, int count);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint colour);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOut(IntPtr dc, int x, int y, string text, int length);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32(IntPtr dc, string text, int length, out SIZE size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(IntPtr dc, string text, int length, ref RECT rect, uint format);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
}
