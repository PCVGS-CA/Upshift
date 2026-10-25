using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Upshift.App.Helpers;
using Upshift.App.ViewModels;
using Upshift.Core.Install;
using Upshift.Core.Models;

namespace Upshift.App.Views;

/// <summary>
/// "Updates for this game" at the top of the Upscaling tab: one line per technology with a newer file on offer (DLSS,
/// FSR, XeSS), each with its own button, plus "Update all". Ordered for the card in this PC (AMD: FSR first, Intel: XeSS
/// first, NVIDIA: DLSS first). On AMD and Intel cards DLSS is marked "Only used through OptiScaler". The per-file detail
/// (restore, re-apply, every file's state) stays in Files, under Advanced. Hidden when nothing is on offer (and never
/// shown for games with anti-cheat, whose files are never updated).
/// </summary>
public sealed class GameUpdatesView : CardPart
{
    /// <summary>Updates these files (relative paths) in the selected game; the page reports failures.</summary>
    public Func<GameCardViewModel, IReadOnlyList<string>, string, Task>? UpdateFiles { get; set; }

    public GameUpdatesView()
    {
        // The order and the notes follow the card (also the pretend one in Developer options).
        void OnGpu() => DispatcherQueue.TryEnqueue(Rebuild);
        Loaded += (_, _) => { AppServices.EffectiveGpuChanged += OnGpu; Rebuild(); };   // the page is kept while others show
        Unloaded += (_, _) => AppServices.EffectiveGpuChanged -= OnGpu;
    }

    public const string FsrDriverLine = "Lets AMD's driver upgrade this game to FSR 4.";
    public const string DlssOnlyThroughOptiScaler = "Only used through OptiScaler";

    /// <summary>The order of the lines for a card's maker.</summary>
    public static IReadOnlyList<UpscalerFamily> OrderFor(GpuVendor? vendor) => vendor switch
    {
        GpuVendor.Amd => new[] { UpscalerFamily.Fsr, UpscalerFamily.Xess, UpscalerFamily.Dlss },
        GpuVendor.Intel => new[] { UpscalerFamily.Xess, UpscalerFamily.Fsr, UpscalerFamily.Dlss },
        _ => new[] { UpscalerFamily.Dlss, UpscalerFamily.Xess, UpscalerFamily.Fsr }
    };

    protected override void Build()
    {
        var card = Card;
        var updatable = card is null || card.Info.HasAntiCheat ? new List<UpscalerFileItem>()
            : card.FileItems.Where(i => i.CanUpdate && i.Target is not null).ToList();
        if (card is null || updatable.Count == 0)
        {
            Content = null;
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        var vendor = AppServices.EffectiveGpu?.Vendor;
        var buttons = new List<Button>();

        var body = new StackPanel { Spacing = 12 };
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Updates for this game", FontWeight = FontWeights.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        var families = OrderFor(vendor).Where(f => updatable.Any(i => i.Family == f)).ToList();
        if (families.Count > 1 || updatable.Count > 1)
        {
            var all = new Button { Content = "Update all", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(all, "Update all for this game");
            all.Click += async (_, _) => await Run(card, updatable.Select(i => i.RelativePath).ToList(), "The files weren't updated", buttons);
            Grid.SetColumn(all, 1);
            header.Children.Add(all);
            buttons.Add(all);
        }
        body.Children.Add(header);

        foreach (var family in families)
        {
            var items = updatable.Where(i => i.Family == family).ToList();
            var name = Ui.FamilyLabel(family);
            var line = new Grid { ColumnSpacing = 12 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var texts = new StackPanel { Spacing = 2 };
            texts.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
            foreach (var item in items)
            {
                var from = item.CurrentVersion is { } v ? Ui.VersionLabel(v, item.FileName) : "unknown";
                var to = Ui.VersionLabel(item.Target!.Version, item.FileName);
                var later = Services.UpscalerUpdates.IsOnThisPc(item.Target) ? "" : " (downloads when you update)";
                texts.Children.Add(Small($"{item.FileName}: {from} → {to}{later}"));
            }
            if (family == UpscalerFamily.Dlss && vendor is GpuVendor.Amd or GpuVendor.Intel) texts.Children.Add(Small(DlssOnlyThroughOptiScaler));
            else if (family == UpscalerFamily.Dlss && items.FirstOrDefault(i => i.FileName.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase)) is { } sr
                     && ViewModels.UpscalerFileText.DlssUnlockLine(sr.CurrentVersion, sr.Target!.Version) is { } unlock)
                texts.Children.Add(Small(unlock));
            if (family == UpscalerFamily.Fsr && vendor == GpuVendor.Amd) texts.Children.Add(Small(FsrDriverLine));
            line.Children.Add(texts);
            var button = new Button { Content = $"Update {name}", VerticalAlignment = VerticalAlignment.Top };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Update {name}");
            button.Click += async (_, _) => await Run(card, items.Select(i => i.RelativePath).ToList(), $"{name} wasn't updated", buttons);
            Grid.SetColumn(button, 1);
            line.Children.Add(button);
            buttons.Add(button);
            body.Children.Add(line);
        }

        var border = new Border
        {
            Padding = new Thickness(16, 14, 16, 16), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Background = Res("CardSurfaceBrush"), BorderBrush = Res("HairlineBrush"), Child = body
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(border, "Updates for this game");
        Content = border;
    }

    private async Task Run(GameCardViewModel card, IReadOnlyList<string> paths, string failTitle, List<Button> buttons)
    {
        if (UpdateFiles is null) return;
        foreach (var b in buttons) b.IsEnabled = false;
        try { await UpdateFiles(card, paths, failTitle); }
        finally { foreach (var b in buttons) b.IsEnabled = true; }   // the card is rebuilt when the game's files change
    }

    private static TextBlock Small(string text) =>
        new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Res("SubtleTextBrush") };

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}
