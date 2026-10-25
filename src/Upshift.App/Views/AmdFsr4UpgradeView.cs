using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Upshift.Core.Hardware;
using Upshift.Core.Install;
using Upshift.Core.Models;

namespace Upshift.App.Views;

/// <summary>
/// "Allow AMD's FSR 4 upgrade" on the Upscaling tab: puts the game's exe on AMD's driver list (ADL application profiles,
/// area FSROVR, profile FsrOvrWhitelistProfile) so the driver can upgrade the game's own FSR 3.1 to FSR 4. No game file is
/// touched. Shown on RX 9000, and on RX 7000 / RX 6000 (labelled testing) with Adrenalin 26.10.1+, for DirectX 12 games
/// with their own FSR 3.1+ DLL. An exe the driver already lists (AMD's own list, or Adrenalin) shows as on and is left
/// alone; Upshift only ever removes what it added. Each change goes into the game's change history.
/// </summary>
public sealed class AmdFsr4UpgradeView : CardPart
{
    public const string Hint = "Then turn on FSR 4 in AMD Adrenalin for this game and pick FSR in the game's menu. " +
                               "For games without FSR 3.1, use OptiScaler's FSR 4 instead.";

    private bool _busy;
    private string? _message;
    private string? _messageGameId;
    private int _build;

    public AmdFsr4UpgradeView()
    {
        void OnGpu() => DispatcherQueue.TryEnqueue(Rebuild);
        Loaded += (_, _) => { AppServices.EffectiveGpuChanged += OnGpu; Rebuild(); };
        Unloaded += (_, _) => AppServices.EffectiveGpuChanged -= OnGpu;
    }

    protected override void Build()
    {
        var build = ++_build;
        var card = Card;
        if (card?.Info is not { } game)
        {
            Hide();
            return;
        }
        var gpu = AppServices.EffectiveGpu;
        var adrenalin = AppServices.PretendAmdDriver ?? AppServices.EffectiveDriver.AdrenalinVersion;
        var offer = AmdFsr4Upgrade.For(gpu, adrenalin, game, OptiScalerFiles(game));
        var record = AmdFsr4Upgrade.RecordFor(AppServices.DataDir, game.Id);
        if (record is null && offer is Fsr4UpgradeOffer.NotThisCard or Fsr4UpgradeOffer.NotThisGame)
        {
            Hide();
            return;
        }
        Visibility = Visibility.Visible;
        Content = Draw(game, offer, record, inDriver: null);
        // Whether the driver already lists this exe (AMD's list or Adrenalin): read in the background, then drawn again.
        if (record is null && game.ExePath is { } exe && AppServices.PretendGpu is null)
            _ = Task.Run(() =>
            {
                using var adl = AdlProfiles.Open(out _);
                return adl?.HasRecord(Path.GetFileName(exe), Path.GetDirectoryName(exe) ?? "", AmdFsr4Upgrade.DriverArea);
            }).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result == true)
                    DispatcherQueue.TryEnqueue(() => { if (build == _build) Content = Draw(game, offer, record, inDriver: true); });
            });
    }

    private void Hide()
    {
        Content = null;
        Visibility = Visibility.Collapsed;
    }

    private FrameworkElement Draw(GameInfo game, Fsr4UpgradeOffer offer, Fsr4UpgradeRecord? record, bool? inDriver)
    {
        var testing = offer == Fsr4UpgradeOffer.OfferedTesting
                      || AppServices.EffectiveGpu?.Generation is "RDNA 3" or "RDNA 2";
        var byDriver = record is null && inDriver == true;
        var pretend = AppServices.PretendGpu is not null;
        var header = "Allow AMD's FSR 4 upgrade" + (testing ? " (testing)" : "");
        var toggle = new ToggleSwitch
        {
            Header = header, IsOn = record is not null || byDriver,
            IsEnabled = !_busy && !pretend && !byDriver && (record is not null || offer is Fsr4UpgradeOffer.Offered or Fsr4UpgradeOffer.OfferedTesting)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, "Allow AMD's FSR 4 upgrade");
        toggle.Toggled += async (_, _) =>
        {
            if (_busy || toggle.IsOn == (record is not null)) return;
            await ChangeAsync(game, toggle.IsOn, record);
        };

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "AMD's FSR 4 upgrade", FontWeight = FontWeights.SemiBold, FontSize = 15 });
        if (game.HasAntiCheat)
            panel.Children.Add(Warning(UpscalerFiles.AntiCheatNote(game)));
        else if (game.Source == GameSourceKind.BattleNet)
            panel.Children.Add(Warning(GameNoticesView.BattleNetText));
        panel.Children.Add(toggle);
        if (offer == Fsr4UpgradeOffer.NeedsDriver) panel.Children.Add(Line(AmdOfficialFsr4.UpdateDriverLine, subtle: false));
        if (byDriver) panel.Children.Add(Line("Already allowed in AMD's driver.", subtle: false));
        if (pretend) panel.Children.Add(Line("Preview for the pretend card: nothing is changed.", subtle: false));
        panel.Children.Add(Line(Hint, subtle: true));
        panel.Children.Add(Line("Upshift only adds this game to AMD's list in the driver. No game files are changed.", subtle: true));
        if (_messageGameId == game.Id && _message is { } message) panel.Children.Add(Line(message, subtle: false));

        var border = new Border
        {
            Padding = new Thickness(16, 14, 16, 16), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Background = Res("CardSurfaceBrush"), BorderBrush = Res("HairlineBrush"), Child = panel
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(border, "AMD's FSR 4 upgrade");
        return border;
    }

    private async Task ChangeAsync(GameInfo game, bool allow, Fsr4UpgradeRecord? record)
    {
        _busy = true;
        Rebuild();
        var data = AppServices.DataDir;
        var problem = await Task.Run(() => allow ? AmdFsr4Upgrade.Allow(data, game) : AmdFsr4Upgrade.Remove(data, record!));
        if (problem is null)
            AppServices.History.Add(game.Id, allow ? "AMD's FSR 4 upgrade allowed" : "AMD's FSR 4 upgrade turned off",
                allow ? $"{Path.GetFileName(game.ExePath)} added to AMD's driver list (FSROVR). No game files changed."
                      : $"{(record?.FileName ?? Path.GetFileName(game.ExePath))} taken off AMD's driver list again.");
        (_message, _messageGameId) = (problem is null ? null : (allow ? "Not allowed: " : "Not turned off: ") + problem, game.Id);
        _busy = false;
        Rebuild();
    }

    /// <summary>Full paths of the files OptiScaler added in this game (its FSR DLLs aren't the game's own).</summary>
    private static List<string> OptiScalerFiles(GameInfo game) =>
        game.TargetDir is { } dir && OptiScalerInstaller.ReadManifest(dir) is { Removed: false } m
            ? m.Added.Select(f => Path.Combine(dir, f.Path)).ToList() : new List<string>();

    private static InfoBar Warning(string text) =>
        new() { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = text };

    private static TextBlock Line(string text, bool subtle) => new()
    {
        Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        Foreground = subtle ? Res("SubtleTextBrush") : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
    };

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}
