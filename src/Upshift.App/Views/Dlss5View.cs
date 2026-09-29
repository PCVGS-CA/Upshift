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

        // Can this card use it, and why; once switched, what's set up (from the DLSS 5 file actually in the game).
        if (installedByUs && manifest!.Switch is not null && !pretend)
        {
            var (title, detail) = SetUpText(dir!, manifest);
            body.Children.Add(Line("", title, "UpshiftAccentBrush", strong: true));
            body.Children.Add(Subtle(detail));
        }
        else
        {
            body.Children.Add(Line(option.Status == NeuralStatus.Unavailable ? "" : "", option.Title,
                option.Status == NeuralStatus.Unavailable ? "SubtleTextBrush" : "UpshiftAccentBrush", strong: true));
            body.Children.Add(Subtle($"{gpu?.Name ?? "This PC's card"}: {option.Message}"));
        }

        if (!installedByUs)
        {
            body.Children.Add(Subtle(option.Backend is null
                ? ""
                : "DLSS 5 comes as a build of OptiScaler: install OptiScaler first, then switch it to the DLSS 5 build here."));
            Content = Wrap(body);
            _building = false;
            return;
        }

        var switched = manifest!.Switch is not null;
        // Blockers are about switching; once switched only the warnings still matter.
        if (!switched)
            foreach (var blocker in status.Blockers.Distinct().Where(b => b != option.Message)) body.Children.Add(Line("", blocker, "#8A2B12"));
        foreach (var warning in status.Warnings) body.Children.Add(Line("", warning, null));
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
        // (Which build and file is in use is said in the header above.)
        var canEdit = ini is not null && !pretend && !card.Info.HasAntiCheat;

        var backend = BackendFor(manifest.ComponentId) ?? status.Option.Backend;

        // On / off, with the in-game hotkey beside it ([DlssNr] ToggleKey, a Windows key code).
        var enabled = Bool(ini?.Get(Dlss5.Section, "Enabled"));
        var toggle = new ToggleSwitch { Header = "Neural Rendering", IsOn = enabled, IsEnabled = canEdit, OnContent = "On", OffContent = "Off" };
        AutomationProperties.SetName(toggle, "Neural Rendering");
        toggle.Toggled += async (_, _) =>
        {
            if (_building) return;
            await Save(card, new IniSetting(Dlss5.Section, "Enabled", toggle.IsOn ? "true" : "false"));
        };
        var toggleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        toggleRow.Children.Add(toggle);
        toggleRow.Children.Add(HotkeyPicker(card, ini, backend, canEdit));
        body.Children.Add(toggleRow);

        // Detail strength ([DlssNr] TransferStrength) and Colour strength (ColourStrength): the names and 1.0 defaults
        // the fork's own menu uses. Model resolution is WorkingScale.
        body.Children.Add(SliderRow("Detail strength", "TransferStrength", Number(ini?.Get(Dlss5.Section, "TransferStrength"), 1.0),
            0, 2, 0.05, 1.0, v => v.ToString("0.00", CultureInfo.InvariantCulture), canEdit, card,
            "How far the picture moves toward the model's: 0 is the game's own image, 1 the model's."));
        body.Children.Add(SliderRow("Colour strength", "ColourStrength", Number(ini?.Get(Dlss5.Section, "ColourStrength"), 1.0),
            0, 2, 0.05, 1.0, v => v.ToString("0.00", CultureInfo.InvariantCulture), canEdit, card,
            "0 keeps the game's own colours, 1 takes the model's; above 1 over-saturates."));
        var scale = Number(ini?.Get(Dlss5.Section, "WorkingScale"), 1.0);
        body.Children.Add(SliderRow("Model resolution", "WorkingScale", scale, 0.5, Math.Max(1, scale), 0.05, 1.0,
            v => $"{v * 100:0}%", canEdit, card, "Lower is faster; below 75% can cause artifacts around hair and fine detail."));

        // The fork's advice when a game hands it no exposure (its menu then says "paper white is in use").
        if (manifest.ComponentId == "optiscaler-dlssnr" && !string.Equals(ini?.Get(Dlss5.Section, "WhitePointSource"), "2", StringComparison.Ordinal))
            body.Children.Add(Line("", WhitePointTip(card.Info.TargetDir!), null));

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

    /// <summary>
    /// A slider for one [DlssNr] value (as the ini stores it, e.g. 0.75), with its value shown and a Reset button that
    /// returns it to the fork's default (by writing "auto", as a fresh ini has it).
    /// </summary>
    private FrameworkElement SliderRow(string header, string key, double current, double min, double max, double step, double fallback,
        Func<double, string> format, bool enabled, GameCardViewModel card, string note)
    {
        var value = new TextBlock { Text = format(current), VerticalAlignment = VerticalAlignment.Center, MinWidth = 44 };
        var slider = new Slider
        {
            Minimum = min, Maximum = Math.Max(max, current), StepFrequency = step, Value = Math.Clamp(current, min, Math.Max(max, current)),
            Width = 260, IsEnabled = enabled, IsThumbToolTipEnabled = false
        };
        AutomationProperties.SetName(slider, header);
        slider.ValueChanged += (_, e) =>
        {
            value.Text = format(e.NewValue);
            if (_building) return;
            // Written once the slider rests, not on every step.
            _pendingSliders[key] = Math.Round(e.NewValue, 2).ToString("0.##", CultureInfo.InvariantCulture);
            _sliderTimer ??= CreateTimer(card);
            _sliderTimer.Stop();
            _sliderTimer.Start();
        };
        var reset = new Button { Content = "Reset", IsEnabled = enabled && Math.Abs(current - fallback) > 0.001, Padding = new Thickness(10, 3, 10, 4) };
        AutomationProperties.SetName(reset, $"Reset {header}");
        ToolTipService.SetToolTip(reset, $"Back to the default, {format(fallback)}");
        reset.Click += async (_, _) =>
        {
            _pendingSliders.Remove(key);
            _building = true;
            slider.Value = fallback;
            _building = false;
            await Save(card, new IniSetting(Dlss5.Section, key, "auto"));
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(slider);
        row.Children.Add(value);
        row.Children.Add(reset);
        return new StackPanel { Spacing = 2, Children = { new TextBlock { Text = header }, row, Subtle(note) } };
    }

    /// <summary>
    /// The header once switched: "DLSS 5 is set up (your modified file)" / "(official NVIDIA file)" from the DLSS 5
    /// file in the game folder, or "(AMD-NR)"; and a line on which build and file.
    /// </summary>
    private static (string Title, string Detail) SetUpText(string targetDir, InstallManifest manifest)
    {
        var fork = GameUpdates.ComponentFor(manifest.ComponentId);
        var backend = BackendFor(manifest.ComponentId);
        if (backend?.NeedsNvidiaDll != true) return ($"DLSS 5 is set up ({fork.Name})", $"Using {fork.Name} {manifest.Version}.");

        var path = Path.Combine(targetDir, Dlss5.NvidiaFileName);
        if (!File.Exists(path))
            return ("DLSS 5 build installed, but nvngx_dlssnr.dll is missing",
                $"Using {fork.Name} {manifest.Version}. Add your DLSS 5 file in Settings, then switch back and to DLSS 5 again.");
        var file = Dlss5.CheckFile(path, AppServices.Catalog);
        return (file.OfficialNvidia ? "DLSS 5 is set up (official NVIDIA file)" : "DLSS 5 is set up (your modified file)",
            $"Using {fork.Name} {manifest.Version} with {file.Label.ToLowerInvariant().Replace("nvidia", "NVIDIA")}.");
    }

    /// <summary>
    /// "Hotkey: Home" with "Set key…" (press any key to use it in the game) and "Default". The fork reads a Windows
    /// virtual-key code from [DlssNr] ToggleKey; "auto" is its default (none for DLSSNR, Home for AMD-NR).
    /// </summary>
    private FrameworkElement HotkeyPicker(GameCardViewModel card, IniFile? ini, NeuralBackend? backend, bool enabled)
    {
        var panel = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(new TextBlock { Text = "Hotkey" });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var name = Dlss5.HotkeyName(ini, backend);
        row.Children.Add(new TextBlock { Text = name ?? "None", VerticalAlignment = VerticalAlignment.Center, MinWidth = 60, FontWeight = FontWeights.SemiBold });
        var set = new Button { Content = "Set key…", IsEnabled = enabled };
        AutomationProperties.SetName(set, "Set Neural Rendering hotkey");
        set.Click += async (_, _) =>
        {
            if (await CaptureKeyAsync() is { } vk) await Save(card, new IniSetting(Dlss5.Section, "ToggleKey", vk.ToString(CultureInfo.InvariantCulture)));
        };
        var reset = new Button { Content = "Default", IsEnabled = enabled && !string.Equals(ini?.Get(Dlss5.Section, "ToggleKey") ?? "auto", "auto", StringComparison.OrdinalIgnoreCase) };
        AutomationProperties.SetName(reset, "Default Neural Rendering hotkey");
        ToolTipService.SetToolTip(reset, backend?.ToggleKey is { } k ? $"Back to {Dlss5.KeyName(k)}" : "Back to no key");
        reset.Click += async (_, _) => await Save(card, new IniSetting(Dlss5.Section, "ToggleKey", "auto"));
        row.Children.Add(set);
        row.Children.Add(reset);
        panel.Children.Add(row);
        panel.Children.Add(Subtle(name is null ? "Or bind one in the game: Insert, then Keybinds." : "Turns Neural Rendering on and off in the game."));
        return panel;
    }

    /// <summary>Asks for a key press; returns its Windows key code, or null when cancelled (Esc or the button).</summary>
    private async Task<int?> CaptureKeyAsync()
    {
        if (ShowDialog is null) return null;
        int? captured = null;
        var text = new TextBlock { Text = "Press the key to use in the game (Esc cancels).", TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Neural Rendering hotkey", Content = text, CloseButtonText = "Cancel" };
        dialog.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var vk = (int)e.Key;
            if (vk == 0x1B) { dialog.Hide(); return; }                      // Esc
            if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C) return;          // Shift, Ctrl, Alt, Windows: need a real key
            captured = vk;
            dialog.Hide();
        };
        await ShowDialog(dialog);
        return captured;
    }

    /// <summary>
    /// The fork's own advice for a game that hands it no exposure (its menu then says "paper white is in use. Try the
    /// scan instead"), with what its OptiScaler.log says the scan found in this game.
    /// </summary>
    private static string WhitePointTip(string targetDir)
    {
        var tip = "Tip: if the game's menu says \"This game supplies no exposure -- paper white is in use\", brightness can drift " +
                  "between dark and bright scenes. The fork suggests the scan: in the game press Insert, then under White point " +
                  "choose \"A buffer the scan found\", and press Anchor once where the picture looks right.";
        try
        {
            var log = Path.Combine(targetDir, "OptiScaler.log");
            if (File.Exists(log) && File.ReadLines(log).Any(l => l.Contains("ExposureScan::Adopt", StringComparison.Ordinal)))
                tip += " Its log shows the scan found candidates in this game.";
        }
        catch (IOException) { }
        return tip;
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
