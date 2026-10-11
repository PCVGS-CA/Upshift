using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Upshift.App.ViewModels;
using Upshift.Core.Detection;
using Upshift.Core.Install;
using Upshift.Core.Models;

namespace Upshift.App.Views;

/// <summary>A code-built part of the game page that rebuilds whenever the page shows another game.</summary>
public abstract class CardPart : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(GameCardViewModel), typeof(CardPart), new PropertyMetadata(null, (d, _) => ((CardPart)d).SafeBuild()));

    public GameCardViewModel? Card
    {
        get => (GameCardViewModel?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    public void Rebuild() => SafeBuild();

    private void SafeBuild()
    {
        try { Build(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Services.CrashLog.Write(ex, GetType().Name);
            Content = null;
            Visibility = Visibility.Collapsed;
        }
    }

    protected abstract void Build();

    protected static TextBlock Text(string text, double size = 14, bool bold = false, bool subtle = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        if (subtle) block.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleTextBrush"];
        return block;
    }
}

/// <summary>
/// The Overview's game-specific notices: Capcom RE Engine games without REFramework (or "REFramework found"), and
/// Battle.net games. Upshift never downloads or installs REFramework; it only says where to get it and checks again.
/// </summary>
public sealed class GameNoticesView : CardPart
{
    /// <summary>"Remove OptiScaler" in the REFramework notice: the page's own uninstall, which explains itself.</summary>
    public Func<Task>? RemoveOptiScaler { get; set; }

    protected override void Build()
    {
        var card = Card;
        var panel = new StackPanel { Spacing = 8 };
        if (card is not null)
        {
            if (ReEngineGames.Notice(card.Info, AppServices.Catalog.ReEngine) is { } re)
                panel.Children.Add(re.Found ? Found() : ReFrameworkNotice(card, re.Warning));
            if (card.Info.Source == GameSourceKind.BattleNet && !card.HasAntiCheat)
                panel.Children.Add(new InfoBar
                {
                    IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = "Battle.net game",
                    Message = "Most Battle.net games are played online. Online games can ban players for changed game files, so check that this game " +
                              "allows mods before installing OptiScaler. Upshift's anti-cheat check can't see every online game's protection."
                });
        }
        Content = panel;
        Visibility = panel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static FrameworkElement Found()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["UpshiftAccentBrush"] });
        row.Children.Add(Text("REFramework found in the game folder.", 13));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, "REFramework found");
        return row;
    }

    private FrameworkElement ReFrameworkNotice(GameCardViewModel card, ReFrameworkWarning warning)
    {
        var body = new StackPanel { Spacing = 6 };
        var installed = card.IsInstalledByUs;
        body.Children.Add(Text(ReEngineGames.Text(warning) ?? ReEngineGames.LikelyText, 14));
        if (installed)
            body.Children.Add(Text("OptiScaler is already installed here without REFramework. If the game doesn't start, install REFramework, or remove OptiScaler to get it running again.", 13));
        var steps = new RichTextBlock { FontSize = 13 };
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = "1. Press Get REFramework and download REFramework.zip from the newest release (the file named for this game, when there is one).\n" });
        p.Inlines.Add(new Run { Text = "2. Open the zip and copy only dinput8.dll next to the game's exe:\n" });
        p.Inlines.Add(new Run { Text = "     " + (card.Info.TargetDir ?? ""), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
        p.Inlines.Add(new Run { Text = "\n3. Press Check again." });
        steps.Blocks.Add(p);
        body.Children.Add(steps);
        body.Children.Add(Text("After a game update the game may crash at start until REFramework is updated too. Removing OptiScaler gets it running again. " +
                               "Upshift never downloads or installs REFramework.", 12, subtle: true));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var get = new Button { Content = "Get REFramework" };
        get.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri(ReEngineGames.DownloadPage));
        var again = new Button { Content = "Check again" };
        again.Click += (_, _) => Rebuild();
        buttons.Children.Add(get);
        buttons.Children.Add(again);
        if (installed && RemoveOptiScaler is { } remove)
        {
            var removeButton = new Button { Content = "Remove OptiScaler" };
            removeButton.Click += async (_, _) => { await remove(); Rebuild(); };
            buttons.Children.Add(removeButton);
        }
        body.Children.Add(buttons);

        var bar = new InfoBar
        {
            IsOpen = true, IsClosable = false, Severity = warning == ReFrameworkWarning.Confirmed ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
            Title = "REFramework isn't installed", Content = body
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bar, "REFramework notice");
        return bar;
    }
}

/// <summary>The Overview's "In use" lines: OptiScaler's version, upscaler, frame generation, frame cap and FSR 4, and which upscaler to pick in the game.</summary>
public sealed class InUseView : CardPart
{
    protected override void Build()
    {
        var card = Card;
        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var panel = new StackPanel { Spacing = 10 };
        if (card is null) { Content = null; return; }

        if (!card.IsInstalledByUs)
        {
            panel.Children.Add(Text(card.HasAntiCheat ? "Nothing is installed: this game has anti-cheat." : "OptiScaler isn't installed by Upshift in this game.", 14));
            if (!card.HasAntiCheat)
                panel.Children.Add(Text("Install it with Install OptiScaler at the top. Its options then appear here and under Upscaling.", 13, subtle: true));
            Content = panel;
            return;
        }

        var s = MeasureView.Snapshot(card);
        var row = 0;
        void Add(string label, string value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Text(label, 13, subtle: true);
            var v = Text(value, 14, bold: true);
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
            row++;
        }
        Add("Upscaler", s.Upscaler);
        Add("Frame generation", s.FrameGeneration);
        Add("Frame cap", s.FrameCap);
        if (s.Fsr4 is { Length: > 0 } fsr4 && fsr4 != "Off") Add("FSR 4", fsr4);
        panel.Children.Add(grid);

        try
        {
            if (card.Info.TargetDir is { } dir && OptiScalerInstaller.ReadManifest(dir) is { Removed: false } manifest)
            {
                var ini = Path.Combine(dir, "OptiScaler.ini");
                var helix = File.Exists(ini) && HelixSr.InUse(IniFile.Load(ini), AppServices.DataDir);
                if (OptiScalerOptionsView.InGamePick(card, manifest, helix).Line is { } line)
                {
                    var pick = new Grid { ColumnSpacing = 8 };
                    pick.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    pick.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    pick.Children.Add(new FontIcon { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
                    var t = Text(line, 13);
                    Grid.SetColumn(t, 1);
                    pick.Children.Add(t);
                    panel.Children.Add(pick);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        panel.Children.Add(Text("In game: press Insert to open the OptiScaler menu.", 12, subtle: true));
        Content = panel;
    }
}

/// <summary>The last measurement in one line, with a link to the Performance tab.</summary>
public sealed class LastRunView : CardPart
{
    /// <summary>Opens the Performance tab.</summary>
    public Action? OpenPerformance { get; set; }

    protected override void Build()
    {
        var card = Card;
        var panel = new StackPanel { Spacing = 6 };
        var runs = card is null ? new() : AppServices.Measurements.Get(card.Info.Id).Runs;
        if (runs.Count == 0)
            panel.Children.Add(Text("Not measured yet. Measure performance is under Performance.", 13, subtle: true));
        else
        {
            var r = runs[0];
            var verdict = r.Bottleneck is { } b ? $" · {b.Title}{(b.LessCertain ? " (less certain)" : "")}" : "";
            panel.Children.Add(Text($"{r.Utc.ToLocalTime():d MMM, HH:mm} · {Math.Round(r.AverageFps)} fps average · low {Math.Round(r.LowFps)} fps" +
                                    (r.MaxFps is { } m ? $" · max {Math.Round(m)} fps" : "") + verdict, 14));
            panel.Children.Add(Text($"{r.Settings.Upscaler}{(r.FrameGenOn ? ", frame generation on" : "")}{(r.Note is { Length: > 0 } n ? " · " + n : "")}", 12, subtle: true));
        }
        if (OpenPerformance is { } open)
        {
            var link = new HyperlinkButton { Content = runs.Count == 0 ? "Go to Performance" : "All results", Padding = new Thickness(0), FontSize = 12 };
            link.Click += (_, _) => open();
            panel.Children.Add(link);
        }
        Content = panel;
    }
}

/// <summary>
/// The Files tab's "Files you supply": each optional file the user adds in Settings (Upshift never downloads them),
/// whether it's on this PC, and whether this game uses it. "Add or change in Settings" opens Settings.
/// </summary>
public sealed class UserFilesSummaryView : CardPart
{
    private static readonly (Core.UserFiles.UserFileKind Kind, string Title)[] Kinds =
    {
        (Core.UserFiles.UserFileKind.DlssNr, "DLSS 5 (nvngx_dlssnr.dll)"),
        (Core.UserFiles.UserFileKind.Fsr4Int8, "FSR 4.0.2c INT8 (amdxcffx64.dll)"),
        (Core.UserFiles.UserFileKind.Fsr411bInt8, "FSR 4.1.1b INT8 for RX 6000"),
    };

    protected override void Build()
    {
        var card = Card;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text("Files you supply", 15, bold: true));
        panel.Children.Add(Text("Upshift never downloads these. You add them once in Settings, and a game's options can then use them.", 12, subtle: true));

        Fsr4Source fsr4 = Fsr4Source.Off;
        var switched = false;
        try
        {
            if (card?.Info.TargetDir is { } dir && OptiScalerInstaller.ReadManifest(dir) is { Removed: false } manifest)
            {
                var iniPath = Path.Combine(dir, "OptiScaler.ini");
                if (File.Exists(iniPath))
                    fsr4 = OptiScalerOptions.Fsr4InUse(dir, manifest, IniFile.Load(iniPath), AppServices.Catalog.Fsr4.CommunityInt8FileName).Source;
                switched = manifest.Switch is not null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        foreach (var (kind, title) in Kinds)
        {
            var info = AppServices.UserFiles.Get(kind);
            var used = kind switch
            {
                Core.UserFiles.UserFileKind.DlssNr => switched,
                Core.UserFiles.UserFileKind.Fsr4Int8 => fsr4 == Fsr4Source.File402c,
                _ => fsr4 == Fsr4Source.File411b
            };
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(Text(title, 13, bold: true));
            row.Children.Add(Text((info is null ? "Not added" : $"Added{(info.Version is { } v ? $", version {v}" : "")}, {info.AddedUtc.ToLocalTime():d MMM yyyy}") +
                                  (used ? " · used by this game" : ""), 12, subtle: true));
            panel.Children.Add(row);
        }
        var open = new Button { Content = "Add or change in Settings" };
        open.Click += (_, _) => (App.MainAppWindow as MainWindow)?.ShowPage("settings");
        panel.Children.Add(open);
        Content = new Border
        {
            Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardSurfaceBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["HairlineBrush"], Child = panel
        };
    }
}
