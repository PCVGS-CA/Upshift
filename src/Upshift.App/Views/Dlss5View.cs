using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Upshift.App.Services;
using Upshift.App.ViewModels;
using Upshift.Core.Catalog;
using Upshift.Core.Install;
using Upshift.Core.UserFiles;

namespace Upshift.App.Views;

/// <summary>
/// "DLSS 5 (Neural Rendering)" in a game's details: whether this card can use it and why, the checks, the switch to
/// (and back from) the DLSS 5 build of OptiScaler, and, once switched, its main settings. Every setting is written to
/// OptiScaler.ini through the same recorded path as the OptiScaler options.
/// </summary>
public sealed class Dlss5View : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(GameCardViewModel), typeof(Dlss5View),
        new PropertyMetadata(null, (d, _) => ((Dlss5View)d).Build()));

    public GameCardViewModel? Card
    {
        get => (GameCardViewModel?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    public LibraryViewModel? Library
    {
        get => _library;
        set
        {
            _library = value;
            // The section is often rebuilt while an install or save is still finishing, so the switch buttons follow
            // the Library's busy state as it changes instead of keeping the one they were built with.
            if (value is not null)
                value.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(LibraryViewModel.IsIdle)) DispatcherQueue.TryEnqueue(UpdateActionButtons);
                };
        }
    }

    private LibraryViewModel? _library;
    public Func<ContentDialog, Task<ContentDialogResult>>? ShowDialog { get; set; }

    /// <summary>The switch buttons, each with whether it could be used when the Library isn't busy.</summary>
    private readonly List<(Button Button, bool Allowed)> _actionButtons = new();

    private void UpdateActionButtons()
    {
        var idle = _library?.IsIdle != false;
        foreach (var (button, allowed) in _actionButtons) button.IsEnabled = allowed && idle;
    }

    private Button ActionButton(Button button, bool allowed)
    {
        _actionButtons.Add((button, allowed));
        button.IsEnabled = allowed && _library?.IsIdle != false;
        return button;
    }

    private bool _building;
    private DispatcherQueueTimer? _sliderTimer;
    private readonly Dictionary<string, string> _pendingSliders = new();

    public Dlss5View()
    {
        AppServices.EffectiveGpuChanged += () => DispatcherQueue.TryEnqueue(Build);
    }

    public void Build()
    {
        var card = Card;
        if (card is null)
        {
            Content = null;
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        _building = true;
        _actionButtons.Clear();

        var gpu = AppServices.EffectiveGpu;
        var pretend = AppServices.PretendGpu is not null;
        var dir = card.Info.TargetDir;
        var manifest = dir is null ? null : OptiScalerInstaller.ReadManifest(dir);
        var installedByUs = manifest is { Removed: false };
        var iniPath = dir is null ? null : Path.Combine(dir, "OptiScaler.ini");
        var ini = iniPath is not null && File.Exists(iniPath) ? IniFile.Load(iniPath) : null;
        var userFile = AppServices.UserFiles.Get(UserFileKind.DlssNr) is { } f ? Dlss5.CheckFile(f.Path, AppServices.Catalog) : null;
        var status = Dlss5.Evaluate(card.Info, gpu, AppServices.Catalog, userFile, ini);
        var option = status.Option;

        var body = new StackPanel { Spacing = 10 };
        if (pretend)
            body.Children.Add(new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Title = "Preview",
                Message = $"Pretending this PC has an {gpu!.Name}. Nothing is installed or changed while this is on (Settings, developer options)."
            });

        // Can this card use it, and why.
        body.Children.Add(Line(option.Status == NeuralStatus.Unavailable ? "" : "", option.Title,
            option.Status == NeuralStatus.Unavailable ? "SubtleTextBrush" : "UpshiftAccentBrush", strong: true));
        body.Children.Add(Subtle($"{gpu?.Name ?? "This PC's card"}: {option.Message}"));

        if (!installedByUs)
        {
            body.Children.Add(Subtle(option.Backend is null
                ? ""
                : "DLSS 5 comes as a build of OptiScaler: install OptiScaler first, then switch it to the DLSS 5 build here."));
            Content = Wrap(body);
            _building = false;
            return;
        }

        foreach (var blocker in status.Blockers.Distinct().Where(b => b != option.Message)) body.Children.Add(Line("", blocker, "#8A2B12"));
        foreach (var warning in status.Warnings) body.Children.Add(Line("", warning, null));

        var switched = manifest!.Switch is not null;
        if (!switched)
            BuildSwitchTo(body, card, status, manifest, pretend);
        else
            BuildSwitched(body, card, status, manifest, ini, pretend);

        Content = Wrap(body);
        _building = false;
    }

    // ---------------- not switched yet ----------------

    private void BuildSwitchTo(StackPanel body, GameCardViewModel card, Dlss5Status status, InstallManifest manifest, bool pretend)
    {
        if (status.Option.Backend is not { } backend) return;
        var fork = GameUpdates.ComponentFor(backend.ComponentId);
        var nvidiaFile = backend.NeedsNvidiaDll ? ", copies in your DLSS 5 file" : "";
        body.Children.Add(Subtle(
            $"Replaces OptiScaler {manifest.Version} with {fork.Name} {fork.PinnedVersion}{nvidiaFile}, and keeps your OptiScaler settings. " +
            "Switch back returns the game folder to exactly what it has now."));
        if (status.IsAmd && status.PreferredRuntime is { } runtime)
            body.Children.Add(Subtle($"Runtime: {runtime.Name}, chosen for your card, so the game won't ask on its first launch. You can change it after switching."));

        var button = ActionButton(new Button
        {
            Content = "Switch to the DLSS 5 build of OptiScaler", Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        }, status.CanSwitch && !pretend);
        button.Click += async (_, _) =>
        {
            if (Library is null || status.Option.Backend is not { } b) return;
            var result = await Library.SwitchToDlss5Async(card, b, status.PreferredRuntime);
            if (result is { Success: false }) await Message("The DLSS 5 build wasn't switched in", result.Message);
        };
        body.Children.Add(button);
        if (pretend) body.Children.Add(Subtle("Switching is off during a preview."));
    }

    // ---------------- switched ----------------

    private void BuildSwitched(StackPanel body, GameCardViewModel card, Dlss5Status status, InstallManifest manifest, IniFile? ini, bool pretend)
    {
        var fork = GameUpdates.ComponentFor(manifest.ComponentId);
        body.Children.Add(new TextBlock { Text = $"Using {fork.Name} {manifest.Version}", FontWeight = FontWeights.SemiBold });
        var canEdit = ini is not null && !pretend && !card.Info.HasAntiCheat;

        // On / off
        var enabled = Bool(ini?.Get(Dlss5.Section, "Enabled"));
        var toggle = new ToggleSwitch { Header = "Neural Rendering", IsOn = enabled, IsEnabled = canEdit, OnContent = "On", OffContent = "Off" };
        AutomationProperties.SetName(toggle, "Neural Rendering");
        toggle.Toggled += async (_, _) =>
        {
            if (_building) return;
            await Save(card, new IniSetting(Dlss5.Section, "Enabled", toggle.IsOn ? "true" : "false"));
        };
        body.Children.Add(toggle);
        body.Children.Add(Subtle(Dlss5.HotkeyText(ini, status.Option.Backend ?? BackendFor(manifest.ComponentId))));

        // Strength (TransferStrength) and Model resolution (WorkingScale)
        var strength = Number(ini?.Get(Dlss5.Section, "TransferStrength"), 1.0);
        body.Children.Add(SliderRow("Strength", "TransferStrength", strength * 100, 0, 150, 5, canEdit, card,
            "How far the picture moves toward the model's: 0% is the game's own image, 100% the model's."));
        var scale = Number(ini?.Get(Dlss5.Section, "WorkingScale"), 1.0);
        body.Children.Add(SliderRow("Model resolution", "WorkingScale", scale * 100, 50, Math.Max(100, scale * 100), 5, canEdit, card,
            "Lower is faster; below 75% can cause artifacts around hair and fine detail."));

        // AMD-NR: which runtime (the game asks on first launch when it isn't set)
        if (status.IsAmd && status.Option.Runtimes.Count > 0)
        {
            var current = ini?.Get(Dlss5.Section, "NrBackend");
            var box = new ComboBox { Header = "Runtime", MinWidth = 260, IsEnabled = canEdit };
            foreach (var runtime in status.Option.Runtimes)
                box.Items.Add(new ComboBoxItem { Content = runtime == status.PreferredRuntime ? $"{runtime.Name} (suggested for your card)" : runtime.Name, Tag = runtime.IniValue });
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string?)i.Tag, current, StringComparison.OrdinalIgnoreCase))
                               ?? box.Items[0];
            box.SelectionChanged += async (_, _) =>
            {
                if (_building || box.SelectedItem is not ComboBoxItem { Tag: string value }) return;
                await Save(card, new IniSetting(Dlss5.Section, "NrBackend", value));
            };
            body.Children.Add(box);
            body.Children.Add(Subtle("A new runtime is used from the game's next start."));
        }

        var back = ActionButton(new Button { Content = "Switch back to regular OptiScaler" }, !pretend);
        back.Click += async (_, _) =>
        {
            if (Library is null) return;
            var result = await Library.SwitchBackAsync(card);
            if (result is { Success: false }) await Message("OptiScaler wasn't switched back", result.Message);
        };
        body.Children.Add(back);
        body.Children.Add(Subtle($"Returns to OptiScaler {manifest.Switch!.FromVersion} exactly as before the switch; settings you changed since are kept."));
    }

    private FrameworkElement SliderRow(string header, string key, double percent, double min, double max, double step, bool enabled,
        GameCardViewModel card, string note)
    {
        var value = new TextBlock { Text = $"{percent:0}%", VerticalAlignment = VerticalAlignment.Center, MinWidth = 44 };
        var slider = new Slider
        {
            Minimum = min, Maximum = max, StepFrequency = step, Value = Math.Clamp(percent, min, max), Width = 280,
            IsEnabled = enabled, IsThumbToolTipEnabled = false
        };
        AutomationProperties.SetName(slider, header);
        slider.ValueChanged += (_, e) =>
        {
            value.Text = $"{e.NewValue:0}%";
            if (_building) return;
            // Written once the slider rests, not on every step.
            _pendingSliders[key] = (e.NewValue / 100).ToString("0.##", CultureInfo.InvariantCulture);
            _sliderTimer ??= CreateTimer(card);
            _sliderTimer.Stop();
            _sliderTimer.Start();
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(slider);
        row.Children.Add(value);
        return new StackPanel { Spacing = 2, Children = { new TextBlock { Text = header }, row, Subtle(note) } };
    }

    private DispatcherQueueTimer CreateTimer(GameCardViewModel card)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(700);
        timer.IsRepeating = false;
        timer.Tick += async (_, _) =>
        {
            var settings = _pendingSliders.Select(p => new IniSetting(Dlss5.Section, p.Key, p.Value)).ToArray();
            _pendingSliders.Clear();
            if (settings.Length > 0) await Save(Card ?? card, settings);
        };
        return timer;
    }

    private async Task Save(GameCardViewModel card, params IniSetting[] settings)
    {
        if (Library is null) return;
        var result = await Library.ConfigureOptiScalerAsync(card, settings);
        if (result is { Success: false }) await Message("The DLSS 5 setting wasn't saved", result.Message);
    }

    private async Task Message(string title, string text)
    {
        if (ShowDialog is null) return;
        await ShowDialog(new ContentDialog
        {
            XamlRoot = XamlRoot, Title = title, CloseButtonText = "OK",
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
        });
    }

    private static NeuralBackend? BackendFor(string componentId) =>
        AppServices.Catalog.NeuralBackends.FirstOrDefault(b => b.ComponentId == componentId);

    private static bool Bool(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static double Number(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    /// <summary>An icon and text that wraps at the pane's width (a Grid, so the text isn't given unlimited width).</summary>
    private static FrameworkElement Line(string glyph, string text, string? color, bool strong = false)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon
        {
            Glyph = glyph, FontSize = strong ? 16 : 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
            Foreground = color is null ? Res("SubtleTextBrush") : color.StartsWith('#') ? Helpers.Ui.Brush(color) : Res(color)
        });
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = strong ? 14 : 13 };
        if (strong) block.FontWeight = FontWeights.SemiBold;
        Grid.SetColumn(block, 1);
        row.Children.Add(block);
        return row;
    }

    private static StackPanel Wrap(FrameworkElement body)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "DLSS 5 (Neural Rendering)", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 14, 16, 14),
            Background = Res("CardSurfaceBrush"), BorderBrush = Res("HairlineBrush"), BorderThickness = new Thickness(1),
            Child = body
        });
        return panel;
    }

    private static TextBlock Subtle(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Res("SubtleTextBrush"), TextWrapping = TextWrapping.Wrap,
        Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible
    };

    private static Microsoft.UI.Xaml.Media.Brush Res(string key) => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];
}
