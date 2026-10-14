using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Upshift.App.Services;
using Upshift.App.ViewModels;
using Upshift.Core.Install;
using Upshift.Core.Measure;

namespace Upshift.App.Views;

/// <summary>
/// "Measure performance" in a game's panel: closed by default (remembered), with the last measurement's date and average
/// in its header. Inside: Measure this game (and what to do next), the two latest runs side by side with the
/// difference, up to 10 older runs, and "What to try".
/// </summary>
public sealed class MeasureView : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(GameCardViewModel), typeof(MeasureView), new PropertyMetadata(null, (d, _) => ((MeasureView)d).Build()));

    public GameCardViewModel? Card
    {
        get => (GameCardViewModel?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    /// <summary>Set by the page: applies settings (the frame cap advice).</summary>
    public LibraryViewModel? Library { get; set; }

    /// <summary>Set by the page: shows a dialog (one at a time).</summary>
    public Func<ContentDialog, Task<ContentDialogResult>>? ShowDialog { get; set; }

    private const string SectionKey = "game.measure";

    public MeasureView()
    {
        Loaded += (_, _) => Measurement.Changed += Build;
        Unloaded += (_, _) => Measurement.Changed -= Build;
    }

    private void Build()
    {
        if (Card is not { } card)
        {
            Content = null;
            return;
        }
        var data = AppServices.Measurements.Get(card.Info.Id);
        var runs = data.Runs;

        // ---- header: "Last: 4 Oct 2026, 21:30 · 87 fps average" ----
        var header = new StackPanel { Spacing = 2, Padding = new Thickness(0, 8, 0, 8) };
        header.Children.Add(new TextBlock { Text = "Measure performance", FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            FontSize = 12, Foreground = Res("SubtleTextBrush"),
            Text = runs.FirstOrDefault() is { } last
                ? $"Last: {last.Utc.ToLocalTime():d MMM yyyy, HH:mm} · {Math.Round(last.AverageFps)} fps average"
                : "Not measured yet"
        });

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Subtle("Records the game's frame rate for as long as you choose, with PresentMon, Intel's free frame-timing tool " +
                                 "(downloaded from its GitHub page the first time). Nothing measured leaves this PC."));
        body.Children.Add(Controls(card));
        if (runs.Count > 0 && VerdictBlock(runs[0]) is { } verdict) body.Children.Add(verdict);
        if (runs.Count > 0) body.Children.Add(Comparison(card, runs));
        if (runs.Count > 2) body.Children.Add(Older(card, runs.Skip(2).ToList()));
        if (runs.Count > 0) body.Children.Add(WhatToTry(card, runs[0]));
        body.Children.Add(Subtle("Results depend on the scene you played. Treat them as a starting point."));
        var export = new Button { Content = "Export all results…" };
        ToolTipService.SetToolTip(export, "Every game's results on this PC as a CSV file, to combine with other PCs' results");
        export.Click += async (_, _) => await ExportAllAsync();
        body.Children.Add(export);

        // The Performance tab's main card: always open.
        var card2 = new StackPanel { Spacing = 8 };
        header.Padding = new Thickness(0);
        ((TextBlock)header.Children[0]).FontSize = 15;
        card2.Children.Add(header);
        card2.Children.Add(body);
        var border = new Border
        {
            Padding = new Thickness(16, 14, 16, 16), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Background = Res("CardSurfaceBrush"), BorderBrush = Res("HairlineBrush"), Child = card2
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(border, "Measure performance");
        Content = border;
    }

    // ---------------- Sharing a result ----------------

    /// <summary>The PC, for shared results (no user name, PC name or folder path).</summary>
    private static Core.Services.PcInfo Pc()
    {
        var gpu = AppServices.Gpu;
        // NVIDIA's own driver number (617.42) rather than Windows' (32.0.16.1742).
        var driver = gpu?.Vendor == Core.Models.GpuVendor.Nvidia ? Core.Install.Dlss5.NvidiaDriver(gpu.DriverVersion) ?? gpu.DriverVersion : gpu?.DriverVersion;
        return Core.Services.PcInfo.Read(gpu?.Name, driver);
    }

    /// <summary>
    /// "Note and share" for one run: a preview of the result as it's shared, the note box (resolution and in-game
    /// settings, which Upshift can't read), "Copy as text" and "Save as image".
    /// </summary>
    private Microsoft.UI.Xaml.Controls.Primitives.ButtonBase ShareButton(GameCardViewModel card, MeasurementRun run, bool compact)
    {
        Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button = compact
            ? new Button { Content = "Note and share", Padding = new Thickness(10, 3, 10, 4) }
            : new HyperlinkButton { Content = "Note and share", Padding = new Thickness(0), FontSize = 12 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Note and share the run from {run.Utc.ToLocalTime():d MMM yyyy HH:mm}");
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        button.Click += (_, _) =>
        {
            flyout.Content = SharePanel(card, run, flyout);
            flyout.ShowAt(button);
        };
        flyout.Closed += (_, _) => Build();   // shows the saved note
        return button;
    }

    private FrameworkElement SharePanel(GameCardViewModel card, MeasurementRun run, Flyout flyout)
    {
        var pc = Pc();
        var panel = new StackPanel { Spacing = 10, Width = 420 };
        var preview = new Border { Child = ResultCard(card.Name, run, pc) };
        var note = new TextBox
        {
            Header = "Your note: resolution and in-game settings", Text = run.Note ?? "", PlaceholderText = "e.g. 2560×1440, High preset, ray tracing off",
            AcceptsReturn = false, MaxLength = 200
        };
        void SaveNote()
        {
            if ((run.Note ?? "") == note.Text.Trim()) return;
            run.Note = string.IsNullOrWhiteSpace(note.Text) ? null : note.Text.Trim();
            AppServices.Measurements.SetNote(card.Info.Id, run.Id, run.Note);
            preview.Child = ResultCard(card.Name, run, pc);
        }
        note.LostFocus += (_, _) => SaveNote();
        note.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { SaveNote(); e.Handled = true; } };

        var status = Subtle("");
        var copy = new Button { Content = "Copy as text" };
        copy.Click += (_, _) =>
        {
            SaveNote();
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(ResultSharing.Text(card.Name, run, pc));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            status.Text = "Copied.";
        };
        var image = new Button { Content = "Save as image…" };
        image.Click += async (_, _) =>
        {
            SaveNote();
            var result = await SaveImageAsync(preview.Child, card.Name, run);
            status.Text = result ?? "";
            // The flyout has usually closed by now (the save dialog takes the focus), so the outcome gets its own message.
            if (result is not null && ShowDialog is not null && !flyout.IsOpen)
                await ShowDialog(new ContentDialog
                {
                    Title = "Save as image", Content = new TextBlock { Text = result, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK", XamlRoot = XamlRoot
                });
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(copy);
        buttons.Children.Add(image);
        panel.Children.Add(preview);
        panel.Children.Add(note);
        panel.Children.Add(buttons);
        panel.Children.Add(status);
        panel.Children.Add(Subtle("Shared results never include your Windows user name, the PC's name or any folder path."));
        return panel;
    }

    /// <summary>The result as a picture-ready card: the same lines as "Copy as text".</summary>
    private static FrameworkElement ResultCard(string gameName, MeasurementRun run, Core.Services.PcInfo pc)
    {
        var lines = ResultSharing.Lines(gameName, run, pc);
        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        foreach (var (label, value) in lines.Skip(1))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, FontSize = 12, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x9A, 0xA4, 0xAE)) };
            var strong = label is "Average" or "Low (1%)" or "Max (best second)" or "Limited by";
            var v = new TextBlock { Text = value, FontSize = strong ? 14 : 12, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Foreground = White };
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
            row++;
        }
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = gameName, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = White });
        stack.Children.Add(grid);
        stack.Children.Add(new TextBlock { Text = "Measured with Upshift", FontSize = 11, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x6F, 0xC9, 0xAE)) });
        return new Border
        {
            Padding = new Thickness(18, 16, 18, 14), CornerRadius = new CornerRadius(10), Child = stack,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x16, 0x1B, 0x22))
        };
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush White => new(Microsoft.UI.Colors.White);

    /// <summary>
    /// Renders the result card to a PNG first (while it's on screen: the save dialog closes the flyout), then asks where
    /// to save it. Returns a status line, or null when cancelled.
    /// </summary>
    private static async Task<string?> SaveImageAsync(UIElement card, string gameName, MeasurementRun run)
    {
        byte[] pixels;
        int width, height;
        try
        {
            var target = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await target.RenderAsync(card, (int)(card.ActualSize.X * 2), (int)(card.ActualSize.Y * 2));
            pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(await target.GetPixelsAsync());
            (width, height) = (target.PixelWidth, target.PixelHeight);
            if (width == 0 || height == 0) return "The image couldn't be made: the result card wasn't on screen.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return $"The image couldn't be made: {ex.Message}"; }

        Windows.Storage.StorageFile? file = null;
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary };
            picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
            picker.SuggestedFileName = SafeName($"{gameName} {run.Utc.ToLocalTime():yyyy-MM-dd HHmm}");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow));
            file = await picker.PickSaveFileAsync();
            if (file is null) return null;
            using (var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
            {
                stream.Size = 0;
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    (uint)width, (uint)height, 192, 192, pixels);
                await encoder.FlushAsync();
            }
            return $"Saved as {file.Name}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The save dialog makes an empty file; don't leave it behind.
            try { if (file is not null && new FileInfo(file.Path).Length == 0) File.Delete(file.Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return $"The image wasn't saved: {ex.Message}";
        }
    }

    private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));

    /// <summary>"Export all results": every game's runs on this PC as one CSV file.</summary>
    private async Task ExportAllAsync()
    {
        var names = AppServices.Library.Current.ToDictionary(g => g.Id, g => g.Name);
        var rows = AppServices.Measurements.All()
            .SelectMany(g => g.Runs.Select(r => (names.TryGetValue(g.GameId, out var n) ? n : "Unknown game", r)))
            .OrderBy(r => r.Item1).ThenByDescending(r => r.r.Utc)
            .ToList();
        string message;
        if (rows.Count == 0) message = "There are no results to export yet.";
        else
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
                picker.FileTypeChoices.Add("CSV file", new List<string> { ".csv" });
                picker.SuggestedFileName = $"Upshift results {DateTime.Now:yyyy-MM-dd}";
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow));
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;
                await File.WriteAllTextAsync(file.Path, ResultSharing.Csv(rows, Pc()), new System.Text.UTF8Encoding(true));
                message = $"{rows.Count} result{(rows.Count == 1 ? "" : "s")} exported to {file.Name}.";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { message = $"The results weren't exported: {ex.Message}"; }
        }
        if (ShowDialog is not null)
            await ShowDialog(new ContentDialog
            {
                Title = "Export all results", Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK", XamlRoot = XamlRoot
            });
    }

    // ---------------- Measure this game ----------------

    private FrameworkElement Controls(GameCardViewModel card)
    {
        var panel = new StackPanel { Spacing = 8 };
        var thisGame = Measurement.GameId == card.Info.Id;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        if (thisGame && Measurement.State != MeasureState.Idle)
        {
            buttons.Children.Add(new ProgressRing { IsActive = true, Width = 18, Height = 18 });
            // Say what's happening next to the spinner, not just the spinner.
            var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            switch (Measurement.State)
            {
                case MeasureState.Preparing:
                    status.Text = "Getting ready…";
                    break;
                case MeasureState.Armed when Measurement.CountdownUntilUtc is not null:
                    status.Text = Countdown();
                    var count = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    count.Tick += (_, _) => status.Text = Countdown();
                    status.Loaded += (_, _) => count.Start();
                    status.Unloaded += (_, _) => count.Stop();
                    break;
                case MeasureState.Armed:
                    status.Text = $"Waiting for you to press {Measurement.HotkeyName} in the game";
                    break;
                case MeasureState.Measuring:
                    status.Text = Elapsed();
                    // Counts up every second while the panel is shown.
                    var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    tick.Tick += (_, _) => status.Text = Elapsed();
                    status.Loaded += (_, _) => tick.Start();
                    status.Unloaded += (_, _) => tick.Stop();
                    break;
            }
            buttons.Children.Add(status);
            if (Measurement.State == MeasureState.Armed && Measurement.CountdownUntilUtc is null)
            {
                // For games that keep keys to themselves (the hotkey never reaches Upshift).
                var later = new Button { Content = $"Start in {Measurement.CountdownSeconds} seconds" };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(later, $"Start in {Measurement.CountdownSeconds} seconds");
                ToolTipService.SetToolTip(later, $"Starts measuring by itself after {Measurement.CountdownSeconds} seconds, without a key. Switch to the game meanwhile.");
                later.Click += async (_, _) => await Measurement.StartInAsync();
                buttons.Children.Add(later);
            }
            if (Measurement.State == MeasureState.Measuring)
            {
                var stop = new Button { Content = "Stop and keep result" };
                stop.Click += (_, _) => Measurement.StopEarly();
                buttons.Children.Add(stop);
            }
            var cancel = new Button { Content = "Cancel" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cancel, "Cancel measuring");
            cancel.Click += (_, _) => Measurement.Cancel();
            buttons.Children.Add(cancel);
        }
        else
        {
            var measure = new Button { Content = "Measure this game", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            measure.Click += async (_, _) => await MeasureAsync(card);
            buttons.Children.Add(measure);
            buttons.Children.Add(LengthBox());
            if (Measurement.State != MeasureState.Idle && Measurement.GameId is not null)
                panel.Children.Add(Subtle("A measurement is armed for another game; this replaces it."));
        }
        panel.Children.Add(buttons);

        if (Measurement.MessageGameId == card.Info.Id && Measurement.Message.Length > 0)
            panel.Children.Add(new InfoBar
            {
                IsOpen = true, IsClosable = false, Message = Measurement.Message,
                Severity = Measurement.MessageIsProblem ? InfoBarSeverity.Warning
                    : Measurement.State == MeasureState.Idle ? InfoBarSeverity.Success : InfoBarSeverity.Informational
            });
        else if (Measurement.State == MeasureState.Idle || Measurement.GameId != card.Info.Id)
            panel.Children.Add(Subtle($"Press Measure this game, then start the game, get to a normal gameplay area and press {Measurement.HotkeyName} " +
                                      "(change the key in Settings). A beep marks the start and two beeps the end."));
        return panel;
    }

    /// <summary>"Measuring starts in 7 s: switch to the game".</summary>
    private static string Countdown()
    {
        var left = Measurement.CountdownUntilUtc is { } until ? Math.Max(0, (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds)) : 0;
        return $"Measuring starts in {left} s: switch to the game";
    }

    /// <summary>"Measuring… 0:23 of 1:00 (F10 stops early)".</summary>
    private static string Elapsed()
    {
        var since = Measurement.MeasuringSinceUtc;
        var elapsed = since is { } s ? DateTime.UtcNow - s : TimeSpan.Zero;
        var total = TimeSpan.FromSeconds(Measurement.CurrentSeconds);
        if (elapsed > total) elapsed = total;
        string F(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        return $"Measuring… {F(elapsed)} of {F(total)} ({Measurement.HotkeyName} stops early)";
    }

    /// <summary>The length box: whole seconds, 10 to 1800, remembered for next time (empty or invalid goes back to the last value).</summary>
    private static FrameworkElement LengthBox()
    {
        var box = new NumberBox
        {
            Value = Measurement.ChosenSeconds, Minimum = MeasureThresholds.MinSeconds, Maximum = MeasureThresholds.MaxSeconds,
            SmallChange = 10, LargeChange = 60, Width = 110, AcceptsExpression = false,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten, VerticalAlignment = VerticalAlignment.Center
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Measuring length in seconds");
        ToolTipService.SetToolTip(box, $"How long to measure, in seconds ({MeasureThresholds.MinSeconds} to {MeasureThresholds.MaxSeconds}).");
        box.ValueChanged += (_, e) =>
        {
            var seconds = double.IsNaN(e.NewValue) ? Measurement.ChosenSeconds : MeasureThresholds.ClampSeconds(e.NewValue);
            if (box.Value != seconds) box.Value = seconds;   // whole seconds, within range
            var settings = AppServices.Settings.Current;
            if (settings.MeasureSeconds == seconds) return;
            settings.MeasureSeconds = seconds;
            try { AppServices.Settings.Save(settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = "seconds", VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private async Task MeasureAsync(GameCardViewModel card)
    {
        if (Measurement.NeedsPermissionPrompt && ShowDialog is not null)
        {
            var answer = await ShowDialog(new ContentDialog
            {
                Title = "Measure performance",
                Content = new TextBlock { Text = Measurement.PermissionExplanation, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Continue", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            });
            if (answer != ContentDialogResult.Primary) return;
        }
        await Measurement.ArmAsync(card.Info, () => Snapshot(card));
    }

    /// <summary>The settings the game has right now, as the measurement records them.</summary>
    internal static MeasuredSettings Snapshot(GameCardViewModel card)
    {
        var dir = card.Info.TargetDir;
        var manifest = dir is null ? null : OptiScalerInstaller.ReadManifest(dir);
        var iniPath = dir is null ? null : Path.Combine(dir, "OptiScaler.ini");
        if (manifest is not { Removed: false } || iniPath is null || !File.Exists(iniPath))
            return new MeasuredSettings
            {
                Upscaler = "The game's own", FrameGeneration = "The game's own", FrameCap = "No cap", Fsr4 = "Off",
                OptiScaler = "Not installed by Upshift"
            };
        var ini = IniFile.Load(iniPath);
        var catalog = AppServices.Catalog;
        var fsr4 = OptiScalerOptions.Fsr4InUse(dir!, manifest, ini, catalog.Fsr4.CommunityInt8FileName);
        // HelixSR is listed when the game uses it, so a run with it is recorded as "DLSS 3.7 via HelixSR (experimental)".
        var helixDir = HelixSr.InUse(ini, AppServices.DataDir) ? HelixSr.FilesDir(AppServices.DataDir) : null;
        var upscalers = OptiScalerOptions.Upscalers(AppServices.Gpu, card.Info.Api, catalog.Fsr4, fsr4.Source != Fsr4Source.Off, helixDir);
        var upscalerId = OptiScalerOptionsView.CurrentUpscaler(ini, upscalers);
        var frameGen = OptiScalerOptions.CurrentFrameGen(ini);
        var cap = OptiScalerOptions.CurrentFrameCap(ini);
        return new MeasuredSettings
        {
            Upscaler = upscalers.FirstOrDefault(u => u.Id == upscalerId)?.Label ?? "Auto",
            FrameGeneration = frameGen?.Label ?? "Off",
            OptiScalerFrameGenOn = frameGen is not null && frameGen.Id is not ("off" or "native"),
            FrameCap = cap is { } c ? $"{Math.Round(c)} fps" : "No cap",
            Fsr4 = OptiScalerOptions.Fsr4Label(fsr4.Source, fsr4.Version)?.Replace("FSR 4: ", "") ?? "Off",
            OptiScaler = $"{GameUpdates.ComponentFor(manifest.ComponentId).Name} {manifest.Version}"
        };
    }

    // ---------------- Results ----------------

    /// <summary>The latest run next to the one before it, with the difference.</summary>
    private FrameworkElement Comparison(GameCardViewModel card, List<MeasurementRun> runs)
    {
        var latest = runs[0];
        var previous = runs.Count > 1 ? runs[1] : null;
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Latest, previous and difference share the width equally, so nothing is cut off in a narrow panel.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (previous is not null)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var row = 0;
        void Add(string label, string a, string? b, string? diff = null, bool strong = false)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            void Cell(string text, int column, bool subtle = false)
            {
                var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
                if (strong && column > 0) block.FontWeight = FontWeights.SemiBold;
                if (subtle) block.Foreground = Res("SubtleTextBrush");
                Grid.SetRow(block, row);
                Grid.SetColumn(block, column);
                grid.Children.Add(block);
            }
            Cell(label, 0, subtle: true);
            Cell(a, 1);
            if (previous is not null)
            {
                Cell(b ?? "", 2);
                Cell(diff ?? "", 3);
            }
            row++;
        }
        string When(MeasurementRun r) => r.Utc.ToLocalTime().ToString(r.Utc.ToLocalTime().Year == DateTime.Now.Year ? "d MMM, HH:mm" : "d MMM yyyy");
        string Diff(double now, double before)
        {
            var d = Math.Round(now) - Math.Round(before);
            var pct = before > 0 ? Math.Round((now - before) / before * 100) : 0;
            return d == 0 ? "same" : $"{(d > 0 ? "+" : "")}{d} ({(pct > 0 ? "+" : "")}{pct}%)";
        }
        string Fg(MeasurementRun r) => r.FrameGenOn ? "On" : "Off";

        Add("", previous is null ? "Latest" : "Latest", previous is null ? null : "Previous", previous is null ? null : "Difference");
        Add("Measured", When(latest), previous is null ? null : When(previous));
        Add("Average", $"{Math.Round(latest.AverageFps)} fps", previous is null ? null : $"{Math.Round(previous.AverageFps)} fps",
            previous is null ? null : Diff(latest.AverageFps, previous.AverageFps), strong: true);
        Add("Low points (1%)", $"{Math.Round(latest.LowFps)} fps", previous is null ? null : $"{Math.Round(previous.LowFps)} fps",
            previous is null ? null : Diff(latest.LowFps, previous.LowFps), strong: true);
        Add("Max (best second)", Fps(latest.MaxFps), previous is null ? null : Fps(previous.MaxFps),
            previous is null ? null : latest.MaxFps is { } lm && previous.MaxFps is { } pm ? Diff(lm, pm) : "–", strong: true);
        Add("Length", RunLength(latest), previous is null ? null : RunLength(previous));
        Add("Your note", latest.Note ?? "–", previous is null ? null : previous.Note ?? "–");
        Add("Limited by", VerdictText(latest), previous is null ? null : VerdictText(previous), strong: true);
        Add("GPU busy / wait", BusyWait(latest.GpuBusyMs, latest.GpuWaitMs), previous is null ? null : BusyWait(previous.GpuBusyMs, previous.GpuWaitMs));
        Add("CPU busy / wait", BusyWait(latest.CpuBusyMs, latest.CpuWaitMs), previous is null ? null : BusyWait(previous.CpuBusyMs, previous.CpuWaitMs));
        Add("GPU usage", Percent(latest.GpuPercent), previous is null ? null : Percent(previous.GpuPercent));
        Add("CPU usage", CpuText(latest), previous is null ? null : CpuText(previous));
        Add("Video memory", Vram(latest), previous is null ? null : Vram(previous));
        Add("Frame generation", Fg(latest), previous is null ? null : Fg(previous));
        Add("Upscaler", latest.Settings.Upscaler, previous?.Settings.Upscaler);
        Add("Frame gen. mode", latest.Settings.FrameGeneration, previous?.Settings.FrameGeneration);
        Add("Frame cap", latest.Settings.FrameCap, previous?.Settings.FrameCap);
        Add("FSR 4", latest.Settings.Fsr4, previous?.Settings.Fsr4);
        Add("OptiScaler", latest.Settings.OptiScaler.Replace("OptiScaler ", ""), previous?.Settings.OptiScaler.Replace("OptiScaler ", ""));

        // Delete links under each run.
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var (run, column) in new[] { (latest, 1), (previous, 2) })
        {
            if (run is null) continue;
            var delete = new HyperlinkButton { Content = "Delete", Padding = new Thickness(0), FontSize = 12 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, $"Delete the run from {run.Utc.ToLocalTime():d MMM yyyy HH:mm}");
            delete.Click += (_, _) => Delete(card, run);
            var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            links.Children.Add(ShareButton(card, run, compact: false));
            links.Children.Add(delete);
            Grid.SetRow(links, row);
            Grid.SetColumn(links, column);
            grid.Children.Add(links);
        }

        var panel = new StackPanel { Spacing = 8 };
        if (latest.FrameGenOn)
            panel.Children.Add(Subtle("The latest run had frame generation on: its frame rate includes generated frames, which look smoother but don't make the game respond faster."));
        if (previous is not null && latest.FrameGenOn != previous.FrameGenOn)
            panel.Children.Add(new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = "Not a like-for-like comparison",
                Message = "Only one of these runs had frame generation on. Its frame rate counts generated frames, so it isn't directly comparable with the other."
            });
        panel.Children.Insert(0, new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 12), Background = Res("CardSurfaceBrush"),
            BorderBrush = Res("HairlineBrush"), BorderThickness = new Thickness(1), Child = grid
        });
        return panel;
    }

    /// <summary>Up to 10 older runs, each with Delete.</summary>
    private FrameworkElement Older(GameCardViewModel card, List<MeasurementRun> older)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = "Older runs", FontWeight = FontWeights.SemiBold, FontSize = 13 });
        foreach (var run in older)
        {
            var line = new Grid { ColumnSpacing = 12 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new TextBlock
            {
                FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Text = $"{run.Utc.ToLocalTime():d MMM yyyy, HH:mm} · {Math.Round(run.AverageFps)} fps average · low points {Math.Round(run.LowFps)} fps" +
                       $" · max {Fps(run.MaxFps)} · {RunLength(run)} · {VerdictText(run)}" +
                       $" · GPU {Percent(run.GpuPercent)}, CPU {CpuText(run)}" +
                       (run.FrameGenOn ? " · frame generation on" : "") + $" · {run.Settings.Upscaler}" + (run.Note is { Length: > 0 } n ? $" · {n}" : "")
            });
            var delete = new Button { Content = "Delete", Padding = new Thickness(10, 3, 10, 4) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, $"Delete the run from {run.Utc.ToLocalTime():d MMM yyyy HH:mm}");
            delete.Click += (_, _) => Delete(card, run);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(ShareButton(card, run, compact: true));
            actions.Children.Add(delete);
            Grid.SetColumn(actions, 1);
            line.Children.Add(actions);
            panel.Children.Add(line);
        }
        return panel;
    }

    /// <summary>"Your graphics card", "Balanced (less certain)", or a dash when there was nothing to judge by.</summary>
    private static string VerdictText(MeasurementRun run) => run.Bottleneck is { } b
        ? b.Title.Replace("Limited by your ", "Your ") + (b.LessCertain ? " (less certain)" : "")
        : "–";

    /// <summary>"14.2 / 0.6 ms per frame"; a dash for any part PresentMon didn't record.</summary>
    private static string BusyWait(double? busy, double? wait) =>
        busy is null && wait is null ? "–" : $"{Ms(busy)} / {Ms(wait)} ms per frame";

    private static string Ms(double? ms) => ms is { } m ? m.ToString(m < 10 ? "0.0" : "0", System.Globalization.CultureInfo.CurrentCulture) : "–";

    private static string Percent(double? p) => p is { } v ? $"{Math.Round(v)}%" : "–";

    /// <summary>"34% (busiest core 88%)".</summary>
    private static string CpuText(MeasurementRun run) =>
        run.CpuPercent is null && run.BusiestCorePercent is null ? "–"
            : $"{Percent(run.CpuPercent)} (busiest core {Percent(run.BusiestCorePercent)})";

    /// <summary>"9.8 GB peak of 12 GB".</summary>
    private static string Vram(MeasurementRun run)
    {
        static string Gb(long bytes) => $"{bytes / 1024.0 / 1024 / 1024:0.0} GB";
        return run.PeakVramBytes is null && run.TotalVramBytes is null ? "–"
            : $"{(run.PeakVramBytes is { } p ? Gb(p) : "–")} peak of {(run.TotalVramBytes is { } t ? Gb(t) : "–")}";
    }

    /// <summary>The latest run's verdict, its advice and how sure it is.</summary>
    private static FrameworkElement? VerdictBlock(MeasurementRun latest)
    {
        if (latest.Bottleneck is not { } b) return null;
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = b.Title + (b.LessCertain ? " (less certain)" : ""), FontWeight = FontWeights.SemiBold, FontSize = 14 });
        panel.Children.Add(new TextBlock { Text = b.Advice, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        if (b.Caution is { } caution) panel.Children.Add(Subtle(caution));
        return panel;
    }

    /// <summary>"143 fps", or a dash for runs saved before Max existed.</summary>
    private static string Fps(double? fps) => fps is { } f ? $"{Math.Round(f)} fps" : "–";

    /// <summary>The run's real length: "58 seconds", "45 seconds (stopped early)".</summary>
    private static string RunLength(MeasurementRun run) =>
        Measurement.Length(run.Seconds) + (run.StoppedEarly ? " (stopped early)" : "");

    private void Delete(GameCardViewModel card, MeasurementRun run)
    {
        AppServices.Measurements.Delete(card.Info.Id, run.Id);
        Build();
    }

    // ---------------- What to try ----------------

    private FrameworkElement WhatToTry(GameCardViewModel card, MeasurementRun latest)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "What to try", FontWeight = FontWeights.SemiBold, FontSize = 13 });
        var refresh = latest.RefreshHz ?? Helpers.Display.Pick(Helpers.Display.All(), AppServices.Settings.Current.FrameCapDisplay)?.RefreshHz;
        double? currentCap = null;
        if (card.Info.TargetDir is { } dir && File.Exists(Path.Combine(dir, "OptiScaler.ini")))
            currentCap = OptiScalerOptions.CurrentFrameCap(IniFile.Load(Path.Combine(dir, "OptiScaler.ini")));
        var advice = MeasureAdvice.For(latest, refresh, currentCap);
        if (advice.Count == 0)
            panel.Children.Add(Subtle("Nothing stands out: the game runs steadily at a good frame rate."));

        foreach (var item in advice)
        {
            var line = new Grid { ColumnSpacing = 12 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new FontIcon { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            var text = new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            Grid.SetColumn(text, 1);
            line.Children.Add(text);
            // Apply only where Upshift can make the change itself: the frame cap, in an OptiScaler it installed.
            if (item.Cap is { } cap && card.IsInstalledByUs && AppServices.PretendGpu is null && Library is not null)
            {
                var apply = new Button { Content = $"Apply {cap} fps cap", VerticalAlignment = VerticalAlignment.Top };
                apply.Click += async (_, _) => await ApplyCapAsync(card, cap);
                Grid.SetColumn(apply, 2);
                line.Children.Add(apply);
            }
            else if (item.Cap is not null && !card.IsInstalledByUs)
                text.Text += " (Install OptiScaler with Upshift to set a frame cap.)";
            panel.Children.Add(line);
        }
        return panel;
    }

    /// <summary>Fills in the frame cap (saved to OptiScaler.ini) and notes it in the game's change history.</summary>
    private async Task ApplyCapAsync(GameCardViewModel card, int cap)
    {
        var note = $"Frame cap {cap} fps applied from Measure performance";
        AppServices.Measurements.RecordApplied(card.Info.Id, note);
        var result = await Library!.ConfigureOptiScalerAsync(card, OptiScalerOptions.FrameCapSettings(cap));
        if (result is not { Success: true })
        {
            AppServices.Measurements.ForgetApplied(card.Info.Id, note);
            if (ShowDialog is not null && result is not null)
                await ShowDialog(new ContentDialog
                {
                    Title = "The frame cap wasn't set", Content = new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "OK", XamlRoot = XamlRoot
                });
        }
        Build();
    }

    private static TextBlock Subtle(string text) => new()
    {
        Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Res("SubtleTextBrush")
    };

    private static Microsoft.UI.Xaml.Media.Brush Res(string key) => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];
}
