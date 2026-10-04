using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Upshift.App.Helpers;

/// <summary>
/// Middle-click auto-scroll, like in a web browser: pressing the mouse wheel shows a marker where it was pressed, and
/// moving the mouse above or below it scrolls up or down, faster the further away it is. Any mouse button or Esc stops
/// it. While it runs, a see-through layer over the window takes the clicks (so the stopping click doesn't also press
/// whatever is under it) and passes wheel turns on, so normal wheel scrolling keeps working.
/// </summary>
public sealed class AutoScroll
{
    private const double DeadZone = 12;   // px around the marker where nothing moves
    private readonly ScrollViewer _scroller;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Popup? _overlay;
    private Point _origin;
    private double _pointerY;
    private UIElement? _keyRoot;

    private AutoScroll(ScrollViewer scroller)
    {
        _scroller = scroller;
        _timer.Tick += (_, _) => Step();
        scroller.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), handledEventsToo: true);
        scroller.Unloaded += (_, _) => Stop();
    }

    /// <summary>Turns on middle-click auto-scroll for a ScrollViewer.</summary>
    public static void Attach(ScrollViewer scroller) => _ = new AutoScroll(scroller);

    /// <summary>For a list or grid: its own ScrollViewer, once it's loaded.</summary>
    public static void Attach(ListViewBase list)
    {
        void Hook()
        {
            if (FindScrollViewer(list) is { } sv) Attach(sv);
        }
        if (list.IsLoaded) Hook();
        else list.Loaded += (_, _) => Hook();
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || _overlay is not null) return;
        var point = e.GetCurrentPoint(null);
        if (!point.Properties.IsMiddleButtonPressed) return;
        if (_scroller.ScrollableHeight <= 0) return;
        e.Handled = true;
        Start(point.Position);
    }

    private void Start(Point origin)
    {
        var root = _scroller.XamlRoot;
        if (root is null) return;
        _origin = origin;
        _pointerY = origin.Y;

        // The marker: a small round badge with up and down arrows, where the wheel was pressed.
        var marker = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
            Background = (Brush)Application.Current.Resources["CardSurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["SubtleTextBrush"], BorderThickness = new Thickness(1.5),
            Child = new FontIcon { Glyph = "", FontSize = 14 }   // "Sort" (up and down arrows)
        };
        Canvas.SetLeft(marker, origin.X - 15);
        Canvas.SetTop(marker, origin.Y - 15);
        var layer = new Canvas
        {
            Width = root.Size.Width, Height = root.Size.Height,
            Background = new SolidColorBrush(Colors.Transparent),   // transparent, but still takes the pointer
            Children = { marker }
        };
        layer.PointerMoved += (_, e) => _pointerY = e.GetCurrentPoint(null).Position.Y;
        layer.PointerPressed += (_, e) => { e.Handled = true; Stop(); };
        layer.PointerWheelChanged += (_, e) =>
        {
            // Normal wheel scrolling keeps working while auto-scroll runs.
            var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
            _scroller.ChangeView(null, _scroller.VerticalOffset - delta, null, false);
            e.Handled = true;
        };

        _overlay = new Popup { XamlRoot = root, Child = layer, IsLightDismissEnabled = false };
        _overlay.IsOpen = true;

        _keyRoot = root.Content;
        _keyRoot?.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKey), handledEventsToo: true);
        _timer.Start();
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        Stop();
    }

    /// <summary>Scrolls by an amount that grows with the distance from the marker (none inside the dead zone).</summary>
    private void Step()
    {
        // The cursor is read directly each tick, so it keeps working when the mouse leaves the window.
        if (CursorY() is { } y) _pointerY = y;
        var dy = _pointerY - _origin.Y;
        var distance = Math.Abs(dy) - DeadZone;
        if (distance <= 0) return;
        var speed = Math.Pow(distance / 8, 1.35);   // px per tick: ~4 at 30 px away, ~25 at 100, ~70 at 200
        _scroller.ChangeView(null, Math.Clamp(_scroller.VerticalOffset + Math.Sign(dy) * speed, 0, _scroller.ScrollableHeight), null, true);
    }

    private void Stop()
    {
        _timer.Stop();
        if (_overlay is not null)
        {
            _overlay.IsOpen = false;
            _overlay = null;
        }
        _keyRoot?.RemoveHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKey));
        _keyRoot = null;
    }

    /// <summary>The cursor's height in the window's own units (as XAML positions are), or null if it can't be read.</summary>
    private double? CursorY()
    {
        if (App.MainAppWindow is not { } window || _scroller.XamlRoot is not { } root) return null;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (!GetCursorPos(out var p) || !ScreenToClient(hwnd, ref p)) return null;
        return p.Y / root.RasterizationScale;
    }

    private struct NativePoint { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }
}
