using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Upshift.App.Services;
using Upshift.App.ViewModels;
using Upshift.Core.ShaderPrebuild;

namespace Upshift.App.Views;

/// <summary>
/// "Pre-build shaders" (Experimental) on a game's Performance tab, shown only while its switch in Settings is on, and
/// never for games with anti-cheat. Learning on and off, how many shaders are learned, "Pre-build now" with progress,
/// what it doesn't do, and the test tools (clear and restore the driver's shader cache, the before-and-after report).
/// </summary>
public sealed class ShaderPrebuildView : CardPart
{
    /// <summary>Set by the page: shows a dialog (one at a time).</summary>
    public Func<ContentDialog, Task<ContentDialogResult>>? ShowDialog { get; set; }

    private bool _busy;
    private string? _note;

    /// <summary>Whether the game was running when the card was last drawn (null before that).</summary>
    private bool? _drawnRunning;
    private readonly DispatcherTimer _runningWatch = new() { Interval = TimeSpan.FromSeconds(3) };

    public ShaderPrebuildView()
    {
        // Shown again (back from Settings, say): the switch or a pre-build may have changed meanwhile.
        Loaded += (_, _) => { PrebuildSession.Changed += Rebuild; ShaderPrebuildGames.EnabledChanged += Rebuild; Rebuild(); _runningWatch.Start(); };
        Unloaded += (_, _) => { PrebuildSession.Changed -= Rebuild; ShaderPrebuildGames.EnabledChanged -= Rebuild; _runningWatch.Stop(); };
        // The game started or closed since the card was drawn: draw it again, so its buttons and text match.
        _runningWatch.Tick += (_, _) =>
        {
            if (Card?.Info is not { } game || _drawnRunning is null) return;
            if ((ShaderPrebuildGames.RunningByName(game) is not null) != _drawnRunning) Rebuild();
        };
    }

    protected override void Build()
    {
        var card = Card;
        // Off in Settings, or a game with anti-cheat: nothing of the feature shows.
        if (card is null || !ShaderPrebuildGames.Enabled || card.Info.HasAntiCheat)
        {
            Content = null;
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        var game = card.Info;
        var panel = new StackPanel { Spacing = 10 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(Text("Pre-build shaders", 15, bold: true));
        title.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1), BorderBrush = Res("UpshiftAccentBrush"),
            Child = new TextBlock { Text = "Experimental", FontSize = 11, Foreground = Res("UpshiftAccentBrush") }
        });
        panel.Children.Add(title);
        panel.Children.Add(Text("Learns which shaders this game builds while you play. When the graphics driver's shader cache is wiped (a driver update, " +
                                "a cache reset), Pre-build now builds them all again before you play, so the game doesn't stutter while it rebuilds them.", 13));

        if (ShaderPrebuildGames.WhyNot(game, card.IsInstalledByUs) is { } why)
        {
            panel.Children.Add(Text(why, 13, subtle: true));
            Content = Wrap(panel);
            return;
        }

        var gameDir = ShaderPrebuildGames.GameDir(game);
        var program = ShaderLibrary.ProgramOf(game)!;
        var running = ShaderPrebuildGames.RunningByName(game) is not null;
        _drawnRunning = running;
        var learning = ShaderPrebuildGames.IsLearning(game);

        // ---- Learn ----
        var toggle = new ToggleSwitch { Header = "Learn this game's shaders while I play", IsOn = learning, IsEnabled = !_busy && !running };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, "Learn this game's shaders while I play");
        toggle.Toggled += async (_, _) => await SetLearningAsync(game, toggle.IsOn);
        panel.Children.Add(toggle);
        panel.Children.Add(Text(learning
            ? "On: Upshift's recorder loads with OptiScaler when the game starts. Turning it off puts the game's OptiScaler.ini back as it was."
            : "Off: nothing is recorded. Turning it on sets two values in the game's OptiScaler.ini; Upshift records them, and turning it off or uninstalling OptiScaler puts them back.",
            12, subtle: true));
        if (running) panel.Children.Add(Text("The game is running: learning can be changed, and shaders pre-built, once it's closed.", 12, subtle: true));

        // ---- What's learned ----
        var status = Text("Counting what's learned…", 13);
        panel.Children.Add(status);
        _ = CountAsync(gameDir, running, learning, status);

        // ---- Pre-build now ----
        var isThisGame = PrebuildSession.GameId == game.Id;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var prebuild = new Button
        {
            Content = "Pre-build now", Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            IsEnabled = !running && !_busy && !PrebuildSession.Running
        };
        prebuild.Click += (_, _) => PrebuildSession.Start(game);   // it checks again that the game isn't running
        row.Children.Add(prebuild);
        if (isThisGame)
        {
            var cancel = new Button { Content = "Stop" };
            cancel.Click += (_, _) => PrebuildSession.Cancel();
            row.Children.Add(cancel);
        }
        panel.Children.Add(row);
        if (isThisGame)
        {
            var p = PrebuildSession.Progress;
            panel.Children.Add(new ProgressBar { Maximum = Math.Max(1, p?.Total ?? 1), Value = p?.Done ?? 0, IsIndeterminate = p is null });
            panel.Children.Add(Text(p is null ? "Starting…" : $"{p.Done:N0} of {p.Total:N0} built{(p.Failed > 0 ? $", {p.Failed:N0} failed" : "")}", 12, subtle: true));
        }
        else if (PrebuildSession.Running)
            panel.Children.Add(Text("Another game's shaders are being pre-built; one at a time.", 12, subtle: true));
        else if (PrebuildSession.LastGameId == game.Id && PrebuildSession.LastResult is { } r)
            panel.Children.Add(new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = r.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                Message = r.Success
                    ? $"{r.Built:N0} built, {r.Failed:N0} failed, in {(r.Seconds < 10 ? r.Seconds.ToString("0.0") : r.Seconds.ToString("0"))} seconds{(r.Card is { } c ? $" on {c}" : "")}. Start the game when you're ready." +
                      (r.Different > 0 ? $" ({r.Different:N0} didn't match their recording exactly: please report this.)" : "")
                    : r.Problem ?? "The pre-build didn't finish."
            });
        if (_note is { } note) panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = note });

        // ---- Did it help? ----
        var help = new StackPanel { Spacing = 8 };
        help.Children.Add(Text("Did it help?", 14, bold: true));
        var helpBody = new StackPanel { Spacing = 8 };
        helpBody.Children.Add(Text("Comparing sessions…", 12, subtle: true));
        help.Children.Add(helpBody);
        panel.Children.Add(help);
        _ = FillHelpAsync(card, helpBody);

        // ---- What it doesn't do ----
        panel.Children.Add(Text("What it doesn't do: it only restores shaders already learned. New areas still build their shaders once (and are " +
                                "learned then). Ray tracing shaders aren't covered. What's learned belongs to this graphics card model; a different " +
                                "card needs its own learning. It never reads shaders from the game's files, and nothing it learns leaves this PC.", 12, subtle: true));

        panel.Children.Add(TestTools(card, gameDir, program, running));
        Content = Wrap(panel);
    }

    private async Task CountAsync(string gameDir, bool running, bool learning, TextBlock status)
    {
        var gpu = AppServices.Gpu;
        var (libraries, waiting, bytes) = await Task.Run(() =>
        {
            var merge = running ? null : ShaderLibrary.Merge(gameDir);
            var unmerged = ShaderLibrary.Sessions(gameDir).Count(s => !s.Merged && File.Exists(Path.Combine(s.Folder, "pipelines.bin")));
            return (ShaderLibrary.Libraries(gameDir), unmerged, ShaderLibrary.Bytes(gameDir));
        });
        var mine = libraries.Where(l => gpu is not null && l.Model.Vendor == gpu.VendorId && l.Model.Device == gpu.DeviceId).ToList();
        var count = mine.Sum(l => l.Pipelines);
        var text = count > 0
            ? $"{count:N0} shaders learned for {gpu?.Name ?? "this graphics card"} · {Size(bytes)}"
            : libraries.Count > 0
                ? $"Nothing learned for {gpu?.Name ?? "this graphics card"} yet; learned on {string.Join(", ", libraries.Select(l => l.Model.Name).Distinct())}."
                : learning ? "Nothing learned yet: play the game normally, and what it builds is learned." : "Nothing learned yet. Turn learning on, then play the game normally.";
        if (waiting > 0) text += $" {waiting} recording{(waiting == 1 ? "" : "s")} will be added once the game is closed.";
        status.Text = text;
    }

    // ---------------- Did it help? ----------------

    /// <summary>
    /// The latest Cold start and Pre-built sessions side by side (in a card that's also the picture "Save as image" makes),
    /// the verdict, what's missing to make the comparison, and Copy as text / Save as image.
    /// </summary>
    private async Task FillHelpAsync(GameCardViewModel card, StackPanel body)
    {
        StutterComparison comparison;
        try { comparison = await Task.Run(() => ShaderPrebuildGames.Comparison(card.Info)); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            body.Children.Clear();
            body.Children.Add(Text($"The sessions couldn't be compared: {ex.Message}", 12, subtle: true));
            return;
        }
        body.Children.Clear();
        body.Children.Add(Text("Each play session is labelled by what came just before it: Cold start (the first after Clear driver shader cache), " +
                               "Pre-built (the first after Pre-build now) or Normal. The latest Cold start and Pre-built sessions are compared.", 12, subtle: true));
        if (!comparison.HasAny)
        {
            foreach (var line in comparison.Missing) body.Children.Add(Text(line, 13));
            foreach (var line in comparison.AllNotes)
                body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = line });
            return;
        }
        var pc = Pc();
        var result = ResultCard(card.Name, comparison, pc);
        body.Children.Add(result);
        foreach (var line in comparison.Missing) body.Children.Add(Text(line, 13));
        foreach (var line in comparison.AllNotes)
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = line });

        var status = Text("", 12, subtle: true);
        var copy = new Button { Content = "Copy as text" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, "Copy the shader comparison as text");
        copy.Click += (_, _) =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(StutterReport.Text(card.Name, comparison, pc));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            status.Text = "Copied.";
        };
        var image = new Button { Content = "Save as image…" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, "Save the shader comparison as an image");
        image.Click += async (_, _) => status.Text = await Helpers.ImageExport.SavePngAsync(result, $"{card.Name} shader pre-build {DateTime.Now:yyyy-MM-dd}") ?? "";
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copy, image } });
        body.Children.Add(status);
        body.Children.Add(Text("Shared results never include your Windows user name, the PC's name or any folder path. " +
                               "Export all results (under Measure performance) includes this comparison.", 12, subtle: true));
    }

    /// <summary>The PC, for shared results (no user name, PC name or folder path); NVIDIA's own driver number.</summary>
    private static Core.Services.PcInfo Pc()
    {
        var gpu = AppServices.Gpu;
        var driver = gpu?.Vendor == Core.Models.GpuVendor.Nvidia ? Core.Install.Dlss5.NvidiaDriver(gpu.DriverVersion) ?? gpu.DriverVersion : gpu?.DriverVersion;
        return Core.Services.PcInfo.Read(gpu?.Name, driver);
    }

    /// <summary>The comparison as a dark result card, like Measure's: game, PC, the two columns, the verdict.</summary>
    private static FrameworkElement ResultCard(string gameName, StutterComparison c, Core.Services.PcInfo pc)
    {
        var dim = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x9A, 0xA4, 0xAE));
        var white = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        var accent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x6F, 0xC9, 0xAE));
        var grid = new Grid { ColumnSpacing = 18, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Cell(int row, int col, string text, double size, bool strong, Microsoft.UI.Xaml.Media.Brush brush)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, Foreground = brush };
            Grid.SetRow(t, row);
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(0, 1, "Cold start", 13, true, white);
        Cell(0, 2, "Pre-built", 13, true, white);
        var row = 1;
        foreach (var (label, cold, pre) in StutterReport.Rows(c))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var strong = label is "Builds over 10 ms" or "Time spent building";
            Cell(row, 0, label, 12, false, dim);
            Cell(row, 1, cold, strong ? 14 : 12, strong, white);
            Cell(row, 2, pre, strong ? 14 : 12, strong, white);
            row++;
        }
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = gameName, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = white });
        stack.Children.Add(new TextBlock { Text = $"{pc.GraphicsText} · {pc.ProcessorText} · {pc.WindowsText}", FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = dim });
        stack.Children.Add(grid);
        if (c.Verdict is { } verdict)
            stack.Children.Add(new TextBlock { Text = verdict, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = accent });
        stack.Children.Add(new TextBlock { Text = "Pre-build shaders (Experimental) · Upshift", FontSize = 11, Foreground = accent });
        var border = new Border
        {
            Padding = new Thickness(18, 16, 18, 14), CornerRadius = new CornerRadius(10), Child = stack, HorizontalAlignment = HorizontalAlignment.Left,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x16, 0x1B, 0x22))
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(border, "Did it help? comparison");
        return border;
    }

    private async Task SetLearningAsync(Core.Models.GameInfo game, bool on)
    {
        if (_busy) return;
        _busy = true;
        _note = null;
        Rebuild();
        try { _note = await ShaderPrebuildGames.SetLearningAsync(game, on); }
        finally { _busy = false; }
        // The page swaps in a refreshed card after a settings change; this rebuild covers the case where it doesn't.
        Rebuild();
    }

    // ---------------- Test tools ----------------

    private FrameworkElement TestTools(GameCardViewModel card, string gameDir, string program, bool running)
    {
        // Intel: Upshift doesn't know where Intel's driver keeps its shader cache, so it can't clear or restore it fairly.
        if (AppServices.EffectiveGpu?.Vendor == Core.Models.GpuVendor.Intel)
            return Text("Test tools aren't available on Intel graphics: Upshift doesn't know where Intel's driver keeps its shader cache, so it can't " +
                        "clear it or put it back, and a before-and-after comparison wouldn't be fair.", 12, subtle: true);
        var caches = new ShaderCaches(AppServices.DataDir);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Text("For comparing a cold start with a pre-built one, on this PC or several. Windows and the graphics driver keep one shader cache " +
                               "for all games, so clearing it makes every game build its shaders once again. Nothing is deleted: it's moved aside, " +
                               "and Restore puts it back.", 12, subtle: true));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var clear = new Button { Content = "Clear driver shader cache (with backup)", IsEnabled = !running };
        clear.Click += async (_, _) => await ClearAsync(caches, card.Info);
        var restore = new Button { Content = caches.BackupTime is { } t ? $"Restore backup ({t:d MMM HH:mm})" : "Restore backup", IsEnabled = caches.HasBackup && !running };
        restore.Click += async (_, _) => await ShowLinesAsync("Restore the shader cache backup", caches.Restore());
        var report = new Button { Content = "Report" };
        report.Click += async (_, _) => await ReportAsync(card, gameDir, program, caches);
        buttons.Children.Add(clear);
        buttons.Children.Add(restore);
        buttons.Children.Add(report);
        body.Children.Add(buttons);
        var expander = new Expander
        {
            Header = "Test tools", Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expander, "Test tools");
        Helpers.Sections.Bind(expander, "game.shaderTestTools");
        return expander;
    }

    /// <summary>
    /// Clear, after showing exactly what moves and where its backup goes. Refused while the game runs (checked again just
    /// before clearing, in case it was started while the question was open).
    /// </summary>
    private async Task ClearAsync(ShaderCaches caches, Core.Models.GameInfo game)
    {
        var gameName = game.Name;
        if (await Task.Run(() => ShaderPrebuildGames.RunningProgram(game)) is { } running)
        {
            await ShowLinesAsync("Clear driver shader cache", new() { $"Not cleared: {running} is running. Close {gameName} first: a cache cleared while the game runs isn't a cold start." });
            return;
        }
        if (ShowDialog is null) return;
        var preview = await Task.Run(caches.Preview);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Text("These caches are shared by every game on this PC: each game builds its shaders once again the next time it starts. " +
                                "Nothing is deleted. Each cache's files move to a backup folder beside it:", 13));
        if (preview.Count == 0) panel.Children.Add(Text("No shader caches were found on this PC, so there's nothing to clear.", 13));
        foreach (var item in preview)
        {
            var block = new StackPanel { Spacing = 1 };
            block.Children.Add(Text($"{item.Name}: {item.Files:N0} files, {Size(item.Bytes)}", 13, bold: true));
            block.Children.Add(Text($"From  {item.Path}", 12, subtle: true));
            block.Children.Add(Text($"To    {item.BackupPath}", 12, subtle: true));
            panel.Children.Add(block);
        }
        panel.Children.Add(Text(caches.HasBackup
            ? "A backup from an earlier clear is already waiting: Restore puts that one back, and this clear's folders stay beside it (listed after the restore, to delete by hand)."
            : "Restore backup puts these files back. Files a running program has open stay where they are.", 12, subtle: true));
        var answer = await ShowDialog(new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Clear the driver shader cache?", Content = new ScrollViewer { Content = panel, MaxHeight = 440 },
            PrimaryButtonText = "Clear", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = preview.Count > 0
        });
        if (answer != ContentDialogResult.Primary) return;
        if (await Task.Run(() => ShaderPrebuildGames.RunningProgram(game)) is { } started)
        {
            await ShowLinesAsync("Clear driver shader cache", new() { $"Not cleared: {started} was started meanwhile. Close {gameName}, then try again." });
            return;
        }
        await ShowLinesAsync("Driver shader cache cleared", caches.Clear(gameName));
    }

    private async Task ReportAsync(GameCardViewModel card, string gameDir, string program, ShaderCaches caches)
    {
        var gpu = AppServices.Gpu;
        var driver = gpu?.Vendor == Core.Models.GpuVendor.Nvidia ? Core.Install.Dlss5.NvidiaDriver(gpu.DriverVersion) ?? gpu.DriverVersion : gpu?.DriverVersion;
        var text = await Task.Run(() =>
        {
            var events = caches.Events().Concat(PrebuildReport.PrebuildEvents(gameDir)).OrderBy(e => e.When).ToList();
            var sessions = ShaderLibrary.Sessions(gameDir).Select(s => PrebuildReport.Stats(s, events)).ToList();
            return PrebuildReport.Text(card.Name, program, gpu?.Name ?? "unknown graphics card", driver, sessions);
        });
        if (ShowDialog is null) return;
        var box = new ScrollViewer
        {
            Content = new TextBlock { Text = text, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12, IsTextSelectionEnabled = true },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto, MaxHeight = 440, Width = 860
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Pre-build shaders: before and after", Content = box,
            PrimaryButtonText = "Copy as text", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close
        };
        dialog.Resources["ContentDialogMaxWidth"] = 940.0;
        if (await ShowDialog(dialog) == ContentDialogResult.Primary)
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
    }

    private async Task ShowLinesAsync(string title, List<string> lines)
    {
        if (ShowDialog is not null)
            await ShowDialog(new ContentDialog
            {
                XamlRoot = XamlRoot, Title = title, CloseButtonText = "OK",
                Content = new TextBlock { Text = string.Join("\n", lines), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
            });
        Rebuild();
    }

    private static string Size(long bytes) => bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : $"{bytes / 1024.0 / 1024:0} MB";

    private static FrameworkElement Wrap(UIElement panel) => new Border
    {
        Padding = new Thickness(16, 14, 16, 16), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Background = Res("CardSurfaceBrush"), BorderBrush = Res("HairlineBrush"), Child = panel
    };

    private static Microsoft.UI.Xaml.Media.Brush Res(string key) => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];
}
