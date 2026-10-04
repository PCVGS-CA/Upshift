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
        body.Children.Add(Subtle($"Records the game's frame rate for {MeasureThresholds.MeasureSeconds} seconds with PresentMon, Intel's free frame-timing tool " +
                                 "(downloaded from its GitHub page the first time). Nothing measured leaves this PC."));
        body.Children.Add(Controls(card));
        if (runs.Count > 0) body.Children.Add(Comparison(card, runs));
        if (runs.Count > 2) body.Children.Add(Older(card, runs.Skip(2).ToList()));
        if (runs.Count > 0) body.Children.Add(WhatToTry(card, runs[0]));
        body.Children.Add(Subtle("Results depend on the scene you played. Treat them as a starting point."));

        var expander = new Expander
        {
            Header = header, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expander, "Measure performance");
        Helpers.Sections.Bind(expander, SectionKey);
        Content = expander;
    }

    // ---------------- Measure this game ----------------

    private FrameworkElement Controls(GameCardViewModel card)
    {
        var panel = new StackPanel { Spacing = 8 };
        var thisGame = Measurement.GameId == card.Info.Id;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        if (thisGame && Measurement.State != MeasureState.Idle)
        {
            if (Measurement.State is MeasureState.Preparing or MeasureState.Armed or MeasureState.Measuring)
                buttons.Children.Add(new ProgressRing { IsActive = true, Width = 18, Height = 18 });
            var cancel = new Button { Content = Measurement.State == MeasureState.Measuring ? "Stop measuring" : "Cancel" };
            cancel.Click += (_, _) => Measurement.Cancel();
            buttons.Children.Add(cancel);
        }
        else
        {
            var measure = new Button { Content = "Measure this game", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            measure.Click += async (_, _) => await MeasureAsync(card);
            buttons.Children.Add(measure);
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
    private static MeasuredSettings Snapshot(GameCardViewModel card)
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
        var upscalers = OptiScalerOptions.Upscalers(AppServices.Gpu, card.Info.Api, catalog.Fsr4, fsr4.Source != Fsr4Source.Off);
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
            Grid.SetRow(delete, row);
            Grid.SetColumn(delete, column);
            grid.Children.Add(delete);
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
                       (run.FrameGenOn ? " · frame generation on" : "") + $" · {run.Settings.Upscaler}"
            });
            var delete = new Button { Content = "Delete", Padding = new Thickness(10, 3, 10, 4) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, $"Delete the run from {run.Utc.ToLocalTime():d MMM yyyy HH:mm}");
            delete.Click += (_, _) => Delete(card, run);
            Grid.SetColumn(delete, 1);
            line.Children.Add(delete);
            panel.Children.Add(line);
        }
        return panel;
    }

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
