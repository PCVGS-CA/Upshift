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

    public const int FullWidth = 300, MinimalHeight = 30;
    private const int Pad = 14, Gap = 6, Radius = 10;

    // Colours (GDI COLORREF is 0x00BBGGRR). The background fills are see-through; everything else is solid.
    private const uint Panel = 0x001C1816, TileBack = 0x002C2723, GraphBack = 0x00262220, Track = 0x003A3633, VerdictBack = 0x002E3412, KeyBack = 0x00332E2B;
    private const uint White = 0x00F7F4F2, Grey = 0x00ABA19A, Dim = 0x00857D77, Teal = 0x00A3B32B, TealLight = 0x00C4D35F, Blue = 0x00FF8D4C,
        Red = 0x004D48E5, Yellow = 0x004CC9F2, GridLine = 0x00504A46, KeyEdge = 0x00665F5A;

    public static (byte[] Pixels, int Width, int Height) Render(OverlayFrame frame, Look look, double scale)
    {
        int S(double v) => (int)Math.Round(v * scale);
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var fonts = new Fonts(S);
        try
        {
            SetBkMode(dc, 1 /* TRANSPARENT */);
            int width, height;
            if (look.Minimal) (width, height) = (Minimal(dc, frame, fonts, S, draw: false), S(MinimalHeight));
            else (width, height) = (S(FullWidth), Layout(dc, frame, fonts, S, S(FullWidth), draw: false));
            var info = new BITMAPINFO { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
            var old = SelectObject(dc, bitmap);
            try
            {
                Fill(dc, 0, 0, width, height, Panel);
                if (look.Minimal) Minimal(dc, frame, fonts, S, draw: true);
                else Layout(dc, frame, fonts, S, width, draw: true);
                GdiFlush();
                var pixels = new byte[width * height * 4];
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                // The minimal pill is fully rounded; the panel has the usual corners.
                Finish(pixels, width, height, look.Minimal ? height / 2 : S(Radius), Math.Max(1, S(1)), look.OpacityPercent);
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
        public readonly IntPtr Big, Unit, Caps, CapsBold, Value, Small, SmallBold, Key;
        public Fonts(Func<double, int> S)
        {
            // ANTIALIASED_QUALITY (4): grey smoothing, which survives the see-through panel (ClearType would fringe).
            IntPtr Font(double size, int weight) => CreateFont(-S(size), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
            Big = Font(42, 700);
            Unit = Font(13, 400);
            Caps = Font(10.5, 400);
            CapsBold = Font(10.5, 700);
            Value = Font(17, 700);
            Small = Font(12, 400);
            SmallBold = Font(12, 700);
            Key = Font(10.5, 700);
        }
        public void Dispose() { foreach (var f in new[] { Big, Unit, Caps, CapsBold, Value, Small, SmallBold, Key }) DeleteObject(f); }
    }

    private static uint DotColour(OverlayPhase phase) => phase switch { OverlayPhase.Ready => Yellow, OverlayPhase.Recording => Red, _ => Teal };

    /// <summary>
    /// The full panel, top to bottom, as in the mockup. Header: "UPSHIFT · MEASURE" (or "· RESULT") and the state with its
    /// dot. Ready: big live FPS, "Press [F10] to start · 60 s", the GPU and CPU bars. Recording: big live FPS, the
    /// frame-time line, the ticked tiles, the progress bar with "0:37 / 1:00" and "[F10] stop & keep". Result: Average,
    /// 1% low and Max, the verdict box, "1:00 measured" and "Hides in 8 s". Measures only (returns the height) when
    /// <paramref name="draw"/> is false.
    /// </summary>
    private static int Layout(IntPtr dc, OverlayFrame f, Fonts fonts, Func<double, int> S, int width, bool draw)
    {
        int left = S(Pad), right = width - S(Pad), y = S(12);
        var result = f.Phase == OverlayPhase.Result;

        // Header.
        if (draw)
        {
            Spaced(dc, fonts.Caps, Grey, left, y, result ? "UPSHIFT · RESULT" : "UPSHIFT · MEASURE", S(1));
            var state = f.Phase switch { OverlayPhase.Ready => "READY", OverlayPhase.Recording => "RECORDING", _ => "SAVED" };
            var w = SpacedWidth(dc, fonts.CapsBold, state, S(1));
            Spaced(dc, fonts.CapsBold, White, right - w, y, state, S(1));
            Dot(dc, right - w - S(12), y + S(3), S(7), DotColour(f.Phase));
        }
        y += S(20);

        // The big live FPS.
        if (!result && f.ShowFps)
        {
            if (draw)
            {
                var number = f.Fps is { } fps ? Math.Round(fps).ToString("0", CultureInfo.InvariantCulture) : OverlayItems.Dash;
                var w = Text(dc, fonts.Big, White, left - S(2), y - S(6), number);
                Text(dc, fonts.Unit, Grey, left + w + S(4), y + S(26), f.Phase == OverlayPhase.Ready ? "FPS live" : "FPS");
            }
            y += S(48);
        }

        if (f.Phase == OverlayPhase.Ready)
        {
            // "Press [F10] to start · 60 s", then the card's and the processor's use.
            if (draw)
            {
                var x = left + Text(dc, fonts.Small, White, left, y, "Press ") ;
                x += KeyCap(dc, fonts.Key, x, y, f.KeyName, S);
                Text(dc, fonts.Small, White, x + S(4), y, $"to start · {f.LengthText}");
            }
            y += S(24);
            var half = (right - left - S(12)) / 2;
            if (draw)
            {
                Bar(dc, fonts.Caps, left, y, left + half, "GPU", f.GpuPercent, Teal, S);
                Bar(dc, fonts.Caps, left + half + S(12), y, right, "CPU", f.CpuPercent, Blue, S);
            }
            y += S(24);
        }

        // The frame-time line, last ~5 seconds.
        if (f.ShowGraph)
        {
            if (draw) Graph(dc, f.Graph, left, y, right, y + S(34), S);
            y += S(34) + S(Gap + 4);
        }

        // Tiles, three per row.
        if (f.Phase != OverlayPhase.Ready && f.Tiles.Count > 0)
        {
            var tileW = (right - left - 2 * S(Gap)) / 3;
            var tileH = S(40);
            for (var i = 0; i < f.Tiles.Count; i++)
            {
                var x = left + i % 3 * (tileW + S(Gap));
                var ty = y + i / 3 * (tileH + S(Gap));
                if (!draw) continue;
                RoundFill(dc, x, ty, x + tileW, ty + tileH, S(5), TileBack);
                Clipped(dc, fonts.Caps, Grey, x + S(7), ty + S(4), x + tileW - S(2), f.Tiles[i].Label.ToUpperInvariant(), S(1));
                Clipped(dc, fonts.Value, White, x + S(7), ty + S(16), x + tileW - S(2), f.Tiles[i].Value);
            }
            var rows = (f.Tiles.Count + 2) / 3;
            y += rows * tileH + (rows - 1) * S(Gap) + S(Gap + 2);
        }

        // The result's verdict box: the title in the accent, the first advice sentence after it.
        if (f.Verdict is { Length: > 0 } verdict)
        {
            var inner = S(9);
            var titleH = Wrapped(dc, fonts.SmallBold, TealLight, left + inner, y + inner, right - inner, verdict + ".", false);
            var adviceH = f.VerdictAdvice is { Length: > 0 } a ? Wrapped(dc, fonts.Small, White, left + inner, y + inner + titleH, right - inner, a, false) : 0;
            var boxH = inner * 2 + titleH + adviceH;
            if (draw)
            {
                RoundFill(dc, left, y, right, y + boxH, S(6), VerdictBack, Teal);
                Wrapped(dc, fonts.SmallBold, TealLight, left + inner, y + inner, right - inner, verdict + ".", true);
                if (f.VerdictAdvice is { Length: > 0 } advice) Wrapped(dc, fonts.Small, White, left + inner, y + inner + titleH, right - inner, advice, true);
            }
            y += boxH + S(8);
        }

        // Progress bar.
        if (f.Progress is { } progress)
        {
            if (draw)
            {
                RoundFill(dc, left, y, right, y + S(4), S(2), Track);
                var done = left + (int)Math.Round((right - left) * progress);
                if (done > left + S(2)) RoundFill(dc, left, y, done, y + S(4), S(2), Teal);
            }
            y += S(10);
        }

        // Footer: the timer (or "1:00 measured") on the left; "[F10] stop & keep" or "Hides in 8 s" on the right.
        if (f.Phase != OverlayPhase.Ready)
        {
            if (draw)
            {
                var leftText = result ? f.Footer : f.Timer;
                if (leftText is not null) Text(dc, fonts.Caps, Grey, left, y + S(2), leftText);
                if (result) TextRight(dc, fonts.Caps, Grey, right, y + S(2), $"Hides in {f.HidesIn} s");
                else
                {
                    var tail = Width(dc, fonts.Caps, "stop & keep");
                    Text(dc, fonts.Caps, Grey, right - tail, y + S(2), "stop & keep");
                    var capW = KeyCapWidth(dc, fonts.Key, f.KeyName, S);
                    KeyCap(dc, fonts.Key, right - tail - S(4) - capW, y, f.KeyName, S);
                }
            }
            y += S(18);
        }
        return y + S(8);
    }

    /// <summary>Minimal: a pill with the dot, the first ticked value and the timer. Returns its width.</summary>
    private static int Minimal(IntPtr dc, OverlayFrame f, Fonts fonts, Func<double, int> S, bool draw)
    {
        var valueW = Width(dc, fonts.SmallBold, f.MinimalValue);
        var timerW = Width(dc, fonts.Small, f.MinimalTimer);
        var width = S(12) + S(8) + S(8) + valueW + S(8) + timerW + S(12);
        if (draw)
        {
            Dot(dc, S(12), S(MinimalHeight) / 2 - S(4), S(8), DotColour(f.Phase));
            Text(dc, fonts.SmallBold, White, S(28), S(MinimalHeight) / 2 - S(8), f.MinimalValue);
            Text(dc, fonts.Small, Grey, S(28) + valueW + S(8), S(MinimalHeight) / 2 - S(8), f.MinimalTimer);
        }
        return width;
    }

    /// <summary>"GPU 97%" over a thin bar filled to that share (an empty bar and "—" when Windows gives no figure).</summary>
    private static void Bar(IntPtr dc, IntPtr font, int left, int top, int right, string label, double? percent, uint colour, Func<double, int> S)
    {
        Spaced(dc, font, colour, left, top, $"{label} {(percent is { } p ? Math.Round(p).ToString("0", CultureInfo.InvariantCulture) + "%" : OverlayItems.Dash)}", S(1));
        RoundFill(dc, left, top + S(15), right, top + S(18), S(1), Track);
        if (percent is { } value && value > 0)
            RoundFill(dc, left, top + S(15), left + Math.Max(S(2), (int)Math.Round((right - left) * Math.Clamp(value, 0, 100) / 100)), top + S(18), S(1), colour);
    }

    private static int KeyCapWidth(IntPtr dc, IntPtr font, string key, Func<double, int> S) => Width(dc, font, key) + S(10);

    /// <summary>A small key cap ("F10"); returns its width.</summary>
    private static int KeyCap(IntPtr dc, IntPtr font, int left, int top, string key, Func<double, int> S)
    {
        var w = KeyCapWidth(dc, font, key, S);
        RoundFill(dc, left, top, left + w, top + S(16), S(3), KeyBack, KeyEdge);
        Text(dc, font, White, left + S(5), top + S(1), key);
        return w;
    }

    /// <summary>
    /// The frame-time line: time runs left to right over the last ~5 seconds, frame time upwards, so a stutter is a
    /// spike. The top is at least 33 ms and at least twice the median frame time; a faint line marks the median.
    /// </summary>
    private static void Graph(IntPtr dc, IReadOnlyList<double> graph, int left, int top, int right, int bottom, Func<double, int> S)
    {
        if (graph.Count < 2) return;
        var sorted = graph.OrderBy(v => v).ToArray();
        var median = sorted[sorted.Length / 2];
        var max = Math.Max(Math.Max(33.4, median * 2), sorted[^1] * 1.1);
        var total = graph.Sum();
        int x0 = left, x1 = right, y0 = top + S(2), y1 = bottom - S(2);
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
        var see = new HashSet<uint> { Panel, TileBack, GraphBack, Track, VerdictBack, KeyBack };
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

    private static void RoundFill(IntPtr dc, int left, int top, int right, int bottom, int radius, uint colour, uint? edge = null)
    {
        var brush = CreateSolidBrush(colour);
        var pen = CreatePen(0, 1, edge ?? colour);
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
    private static void Clipped(IntPtr dc, IntPtr font, uint colour, int left, int top, int right, string text, int spacing = 0)
    {
        var old = SelectObject(dc, font);
        SetTextColor(dc, colour);
        SetTextCharacterExtra(dc, spacing);
        var rect = new RECT { Left = left, Top = top, Right = right, Bottom = top + 1000 };
        DrawText(dc, text, text.Length, ref rect, 0x0020 /* SINGLELINE */ | 0x8000 /* END_ELLIPSIS */ | 0x0800 /* NOPREFIX */);
        SetTextCharacterExtra(dc, 0);
        SelectObject(dc, old);
    }

    /// <summary>Small capitals with a little letter spacing ("UPSHIFT · MEASURE").</summary>
    private static void Spaced(IntPtr dc, IntPtr font, uint colour, int x, int y, string text, int spacing)
    {
        SetTextCharacterExtra(dc, spacing);
        Text(dc, font, colour, x, y, text);
        SetTextCharacterExtra(dc, 0);
    }

    private static int SpacedWidth(IntPtr dc, IntPtr font, string text, int spacing)
    {
        SetTextCharacterExtra(dc, spacing);
        var w = Width(dc, font, text);
        SetTextCharacterExtra(dc, 0);
        return w;
    }

    private static int Width(IntPtr dc, IntPtr font, string text)
    {
        var old = SelectObject(dc, font);
        GetTextExtentPoint32(dc, text, text.Length, out var size);
        SelectObject(dc, old);
        return size.cx;
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
    [DllImport("gdi32.dll")] private static extern int SetTextCharacterExtra(IntPtr dc, int extra);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint colour);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOut(IntPtr dc, int x, int y, string text, int length);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32(IntPtr dc, string text, int length, out SIZE size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(IntPtr dc, string text, int length, ref RECT rect, uint format);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
}
