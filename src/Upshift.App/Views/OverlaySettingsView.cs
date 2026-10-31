using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Upshift.App.Services;
using Upshift.Core.Measure;

namespace Upshift.App.Views;

/// <summary>
/// Settings > Measure performance > the overlay: on or off, Full or Minimal, corner, panel opacity, and "Show on overlay"
/// with a live preview drawn by the same renderer as the overlay itself (sample figures, Ready / Recording / Result).
/// Every choice is saved at once and an overlay on screen follows it within a quarter of a second.
/// </summary>
public sealed class OverlaySettingsView : UserControl
{
    private readonly Image _preview = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private OverlayPhase _previewPhase = OverlayPhase.Recording;
    private bool _loading = true;

    public OverlaySettingsView()
    {
        var settings = AppServices.Settings.Current;
        var items = OverlayItems.Normalize(settings.OverlayItems);

        var show = new ToggleSwitch { Header = "Show overlay while measuring", IsOn = settings.MeasureOverlay };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(show, "Show overlay while measuring");
        show.Toggled += (_, _) => Save(s => s.MeasureOverlay = show.IsOn);

        var style = new RadioButtons { Header = "Style", MaxColumns = 2 };
        style.Items.Add(new RadioButton { Content = "Full", Tag = "Full" });
        style.Items.Add(new RadioButton { Content = "Minimal (one line)", Tag = "Minimal" });
        style.SelectedIndex = string.Equals(settings.OverlayStyle, "Minimal", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(style, "Overlay style");
        style.SelectionChanged += (_, _) => Save(s => s.OverlayStyle = style.SelectedIndex == 1 ? "Minimal" : "Full");

        var corner = new ComboBox { Header = "Corner", MinWidth = 150 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(corner, "Overlay corner");
        foreach (var (value, label) in new[] { ("TopLeft", "Top left"), ("TopRight", "Top right"), ("BottomLeft", "Bottom left"), ("BottomRight", "Bottom right") })
            corner.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        var currentCorner = MeasureOverlay.ParseCorner(settings.OverlayCorner).ToString();
        corner.SelectedItem = corner.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == currentCorner);
        corner.SelectionChanged += (_, _) =>
        {
            if (corner.SelectedItem is ComboBoxItem { Tag: string tag }) Save(s => s.OverlayCorner = tag);
        };

        var opacity = new Slider { Header = $"Panel opacity: {Math.Clamp(settings.OverlayOpacity, 30, 100)}%", Minimum = 30, Maximum = 100, StepFrequency = 5, Value = Math.Clamp(settings.OverlayOpacity, 30, 100), Width = 220 };
        opacity.ThumbToolTipValueConverter = null;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(opacity, "Overlay panel opacity");
        opacity.ValueChanged += (_, _) =>
        {
            opacity.Header = $"Panel opacity: {(int)Math.Round(opacity.Value)}%";
            Save(s => s.OverlayOpacity = (int)Math.Round(opacity.Value));
        };

        var look = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children = { style, corner, opacity } };

        // "Show on overlay": the defaults first, then the optional ones.
        var boxes = new StackPanel { Spacing = 0 };
        boxes.Children.Add(new TextBlock { Text = "Show on overlay", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var (key, label) in OverlayItems.All)
        {
            var box = new CheckBox { Content = label, IsChecked = items.Contains(key), MinHeight = 28, Padding = new Thickness(6, 4, 0, 0) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Show on overlay: " + label);
            box.Checked += (_, _) => SetItem(key, true);
            box.Unchecked += (_, _) => SetItem(key, false);
            boxes.Children.Add(box);
        }
        boxes.Children.Add(new TextBlock
        {
            Text = "Figures come from PresentMon and Windows' own counters. Anything a game or PC doesn't report shows —.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, Margin = new Thickness(0, 6, 0, 0),
            Foreground = (Brush)Application.Current.Resources["SubtleTextBrush"]
        });

        // The preview: the overlay over a game-like backdrop, so the panel's see-through shows.
        var phase = new RadioButtons { MaxColumns = 3 };
        foreach (var p in new[] { "Ready", "Recording", "Result" }) phase.Items.Add(new RadioButton { Content = p });
        phase.SelectedIndex = 1;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(phase, "Preview state");
        phase.SelectionChanged += (_, _) => { _previewPhase = (OverlayPhase)Math.Max(0, phase.SelectedIndex); Redraw(); };
        var backdrop = new Border
        {
            Width = OverlayRenderer.FullWidth + 60, MinHeight = 220, Padding = new Thickness(20, 16, 20, 16), CornerRadius = new CornerRadius(8), Child = _preview,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0x5B, 0x7F, 0x9E), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0xC9, 0x8B, 0x55), Offset = 0.55 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0x2E, 0x3B, 0x2A), Offset = 1 }
                }
            }
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(backdrop, "Overlay preview");
        var previewColumn = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Preview", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, phase, backdrop } };

        var grid = new Grid { ColumnSpacing = 32 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(boxes);
        Grid.SetColumn(previewColumn, 1);
        grid.Children.Add(previewColumn);

        Content = new StackPanel { Spacing = 12, Margin = new Thickness(0, 8, 0, 0), Children = { show, look, grid } };
        Loaded += (_, _) => { _loading = false; Redraw(); };
    }

    private void SetItem(string key, bool on)
    {
        Save(s =>
        {
            var list = OverlayItems.Normalize(s.OverlayItems);
            if (on && !list.Contains(key)) list.Add(key);
            if (!on) list.Remove(key);
            s.OverlayItems = OverlayItems.Normalize(list);   // back in the list's order
        });
    }

    private void Save(Action<Core.Services.AppSettings> change)
    {
        if (_loading) return;
        var settings = AppServices.Settings.Current;
        change(settings);
        AppServices.Settings.Save(settings);
        Redraw();
    }

    /// <summary>The preview with sample figures, at this screen's scale.</summary>
    private void Redraw()
    {
        if (XamlRoot is null) return;
        var settings = AppServices.Settings.Current;
        var scale = XamlRoot.RasterizationScale;
        var frame = OverlayItems.Build(_previewPhase, OverlayItems.Normalize(settings.OverlayItems), Measurement.HotkeyName, 60,
            Sample(), Sample() with { AverageFps = 141, LowFps = 97, Low01Fps = 74, MaxFps = 158 }, new LiveLoad(96, 31, 7_150_000_000), 23,
            new FrameStats(8460, 60, 141, 97, 0, true, 158), "Limited by your graphics card", "Try a lower upscaler quality or lower graphics settings.");
        var look = new OverlayRenderer.Look(string.Equals(settings.OverlayStyle, "Minimal", StringComparison.OrdinalIgnoreCase), settings.OverlayOpacity);
        var (pixels, w, h) = OverlayRenderer.Render(frame, look, scale);
        var bitmap = new WriteableBitmap(w, h);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        _preview.Source = bitmap;
        _preview.Width = w / scale;
        _preview.Height = h / scale;
    }

    private static LiveSnapshot Sample()
    {
        // About 144 fps with a stutter, for the frame-time line.
        var graph = Enumerable.Range(0, 700).Select(i => i == 420 ? 31.0 : i % 97 == 0 ? 11.5 : 6.6 + Math.Sin(i / 9.0) * 0.5).ToArray();
        return new LiveSnapshot(8460, 144, 97, 6.9, false)
        {
            Graph = graph, AverageFps = 141, Low01Fps = 74, MaxFps = 158, LatencyMs = null, GpuBusyMs = 6.4, GpuWaitMs = 0.3
        };
    }
}
