using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Upshift.App.ViewModels;
using Upshift.Core.Hardware;
using Upshift.Core.Install;
using Upshift.Core.Models;
using Upshift.Core.UserFiles;

namespace Upshift.App.Views;

/// <summary>
/// "OptiScaler options" for a game with OptiScaler installed by this app. Every change is written to the game's
/// OptiScaler.ini (and recorded in .upshift\manifest.json) straight away, so Uninstall still restores the folder exactly.
/// </summary>
public sealed class OptiScalerOptionsView : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(GameCardViewModel), typeof(OptiScalerOptionsView),
        new PropertyMetadata(null, (d, _) => ((OptiScalerOptionsView)d).Build()));

    public GameCardViewModel? Card
    {
        get => (GameCardViewModel?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    public LibraryViewModel? Library { get; set; }
    public Func<ContentDialog, Task<ContentDialogResult>>? ShowDialog { get; set; }

    /// <summary>"Update DLSS" for the game (the Library page's, so results are shown the same way).</summary>
    public Func<GameCardViewModel, Task>? UpdateDlss { get; set; }

    /// <summary>True while controls are being filled in, so setting their initial values doesn't write anything.</summary>
    private bool _building;

    public OptiScalerOptionsView()
    {
        // The developer preview's pretend card changes which options are offered (FSR 4, DLSS models…).
        AppServices.EffectiveGpuChanged += () => DispatcherQueue.TryEnqueue(Build);
    }

    public void Build()
    {
        var card = Card;
        var dir = card?.Info.TargetDir;
        var manifest = dir is null ? null : OptiScalerInstaller.ReadManifest(dir);
        var iniPath = dir is null ? null : Path.Combine(dir, "OptiScaler.ini");
        if (card is null || manifest is not { Removed: false } || iniPath is null || !File.Exists(iniPath))
        {
            Content = null;
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;

        _building = true;
        var ini = IniFile.Load(iniPath);
        // The pretend card from Settings > Developer options when one is set, so its options can be previewed.
        var gpu = AppServices.EffectiveGpu;
        var catalog = AppServices.Catalog;
        var body = new StackPanel { Spacing = 14 };
        if (AppServices.PretendGpu is { } pretend)
            body.Children.Add(new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
                Title = $"Preview for {pretend.Name}",
                Message = "These are the options that card would get. Nothing is changed while a pretend card is set (Settings > Developer options)."
            });

        // ---- FSR 4 (INT8) state first: it decides whether FSR 4 is in the upscaler list ----
        var int8Shown = OptiScalerOptions.Int8OptionsShown(gpu, catalog.Fsr4);
        var communityName = catalog.Fsr4.CommunityInt8FileName;
        var fsr4On = OptiScalerOptions.Fsr4InUse(dir!, manifest, ini, communityName).Source != Fsr4Source.Off;

        // ---- Upscaler ----
        var upscalers = OptiScalerOptions.Upscalers(gpu, card.Info.Api, catalog.Fsr4, int8Shown && fsr4On);
        var suggested = card.Recommendation?.ChoiceId;
        var upscalerBox = Combo("Upscaler", upscalers.Select(u => Item(u.Id, u.Id == suggested ? $"{u.Label} (suggested)" : u.Label)));
        upscalerBox.SelectedItem = upscalerBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i =>
            (string)i.Tag == CurrentUpscaler(ini, upscalers)) ?? upscalerBox.Items[0];
        upscalerBox.SelectionChanged += async (_, _) =>
        {
            if (_building || upscalerBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            await Save(card, upscalers.First(u => u.Id == id).Settings);
        };
        body.Children.Add(WithGuide(upscalerBox, catalog.Guides.Upscaler, upscalers.Select(u => (u.Id, u.Label)), gpu));

        // ---- Frame generation ----
        var modes = OptiScalerOptions.FrameGenModes(gpu, card.Info, card.Suggestions?.HiddenFrameGen ?? new HashSet<string>());
        var fgBox = Combo("Frame generation", modes.Select(m => Item(m.Id, m.Label)));
        var current = OptiScalerOptions.CurrentFrameGen(ini);
        fgBox.SelectedItem = fgBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current?.Id) ?? fgBox.Items[0];
        var fgNote = Subtle(current?.Note ?? "");
        fgBox.SelectionChanged += async (_, _) =>
        {
            if (_building || fgBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            await Save(card, OptiScalerOptions.FrameGenSettings(modes.First(m => m.Id == id)));
        };
        body.Children.Add(Group(WithGuide(fgBox, catalog.Guides.FrameGen, modes.Select(m => (m.Id, m.Label)), gpu), fgNote,
            card.Suggestions?.HiddenFrameGen.Count > 0 ? Subtle("Some modes are hidden because the OptiScaler wiki says they don't work for this game.") : null));

        // ---- Frame cap (OptiScaler's Reflex-based limiter) ----
        body.Children.Add(FrameCapSection(card, ini, gpu));

        // ---- DLSS model (NVIDIA RTX only) ----
        if (OptiScalerOptions.IsNvidiaRtx(gpu))
            body.Children.Add(DlssSection(card, ini, gpu!));

        // ---- FSR 4 (INT8) ----
        if (int8Shown)
            body.Children.Add(Fsr4Section(card, gpu!, ini, manifest, communityName));

        body.Children.Add(Subtle("Set the game's upscaler to DLSS or XeSS, load a save, then press Insert to check."));
        if (card.Suggestions is { FromWiki: true })
            body.Children.Add(Subtle("Tips for this game from the OptiScaler wiki are under Suggestions for this game."));

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "OptiScaler options", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14),
            Background = Res("CardSurfaceBrush"),
            BorderBrush = Res("HairlineBrush"),
            BorderThickness = new Thickness(1),
            Child = body
        });
        Content = panel;
        _building = false;
    }

    private FrameworkElement DlssSection(GameCardViewModel card, IniFile ini, GpuInfo gpu)
    {
        var gameDlss = OptiScalerOptions.GameDlssVersion(card.Info);
        var presets = AppServices.Catalog.DlssPresets;
        var models = OptiScalerOptions.DlssModels(presets, gameDlss);
        var currentId = OptiScalerOptions.CurrentDlssModel(ini, models);

        // Each model with its one-line description from the guide under its name. Models the game's DLSS file is too
        // old for are greyed out, with an "Update DLSS to unlock" link when Upshift can update the file.
        var guide = AppServices.Catalog.Guides.DlssModel;
        var canUpdateDlss = Services.UpscalerUpdates.DlssUpdate(card.Info) is not null && UpdateDlss is not null;
        var items = models.Select(m => m.Available
            ? DescribedItem(m.Id, m.Label, guide.Rows.FirstOrDefault(r => r.Id == m.Id)?.TextFor(gpu.Vendor, gpu.Generation))
            : LockedItem(m.Id, m.Label, canUpdateDlss ? () => UpdateDlss!(card) : null)).ToList();
        items.Add(Item("advanced", "Advanced (one model per quality mode)"));
        var box = Combo("DLSS model", items);
        box.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == currentId) ?? items[0];

        var note = Subtle(models.FirstOrDefault(m => m.Id == currentId)?.Note ?? "");
        var advanced = AdvancedPresets(card, ini, presets, gameDlss);
        advanced.Visibility = currentId == "advanced" ? Visibility.Visible : Visibility.Collapsed;

        var previous = box.SelectedItem;
        box.SelectionChanged += async (_, _) =>
        {
            if (_building || box.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            if (models.FirstOrDefault(m => m.Id == id) is { Available: false })
            {
                // Picked with the keyboard: a locked model can't be used until the DLSS file is updated.
                _building = true;
                box.SelectedItem = previous;
                _building = false;
                return;
            }
            previous = box.SelectedItem;
            if (id == "advanced")
            {
                advanced.Visibility = Visibility.Visible;
                note.Text = "Pick a model for each quality mode below.";
                return;
            }
            await Save(card, models.First(m => m.Id == id).Settings);
        };

        var dlssFile = card.Info.Upscalers.FirstOrDefault(u => u.FileName.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase))?.Version;
        FrameworkElement? locked = null;
        if (dlssFile is null)
            locked = Subtle("This game has no DLSS file of its own.");
        else if (models.Any(m => !m.Available))
        {
            var text = Subtle($"This game has DLSS {dlssFile}, which is too old for the DLSS 4 and 4.5 models. Update its DLSS file to unlock them.");
            if (canUpdateDlss)
            {
                var link = new HyperlinkButton { Content = "Update DLSS to unlock", Padding = new Thickness(0), FontSize = 12 };
                link.Click += async (_, _) => await UpdateDlss!(card);
                locked = Group(text, link);
            }
            else locked = text;
        }
        return Group(WithGuide(box, guide, models.Select(m => (m.Id, m.Label)), gpu), note, advanced, locked,
            Subtle("If you've set a DLSS override for this game in the NVIDIA App, the app's setting may be used instead of this one."));
    }

    /// <summary>
    /// A model the game's DLSS file is too old for: greyed out, with "Update DLSS to unlock" under it. The item itself
    /// stays enabled so the link can be clicked; choosing the model is refused in SelectionChanged.
    /// </summary>
    private static ComboBoxItem LockedItem(string id, string label, Func<Task>? unlock)
    {
        var content = new StackPanel { Spacing = 1, Opacity = 0.9 };
        content.Children.Add(new TextBlock { Text = label, Foreground = Res("SubtleTextBrush") });
        if (unlock is not null)
        {
            var link = new HyperlinkButton { Content = "Update DLSS to unlock", Padding = new Thickness(0), FontSize = 12 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(link, $"Update DLSS to unlock {label}");
            link.Click += async (_, _) => await unlock();
            content.Children.Add(link);
        }
        else
        {
            content.Children.Add(new TextBlock { Text = "Needs a newer DLSS file in the game", FontSize = 12, Foreground = Res("SubtleTextBrush") });
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(content, $"{label} (locked)");
        return new ComboBoxItem { Tag = id, Content = content };
    }

    /// <summary>One dropdown per quality mode (DLAA … Ultra Performance), each "Game default" or a preset.</summary>
    private StackPanel AdvancedPresets(GameCardViewModel card, IniFile ini, IReadOnlyList<Core.Catalog.DlssPreset> presets, Version? gameDlss)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(12, 4, 0, 0) };
        var boxes = new Dictionary<string, ComboBox>();
        foreach (var mode in OptiScalerOptions.QualityModes)
        {
            var items = new List<ComboBoxItem> { Item("0", "Game default") };
            items.AddRange(presets.Select(p => Item(p.Value.ToString(),
                OptiScalerOptions.PresetAvailable(p, gameDlss) ? $"{p.Name} — {p.Note}" : $"{p.Name} — update this game's DLSS file to unlock",
                OptiScalerOptions.PresetAvailable(p, gameDlss))));
            var box = Combo(OptiScalerOptions.QualityModeLabel(mode), items);
            var value = ini.Get("DLSS", "RenderPreset" + mode);
            box.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == value) ?? items[0];
            box.SelectionChanged += async (_, _) =>
            {
                if (_building) return;
                var perMode = boxes.ToDictionary(b => b.Key, b => int.Parse((string)((ComboBoxItem)b.Value.SelectedItem).Tag));
                await Save(card, OptiScalerOptions.PerModeSettings(perMode));
            };
            boxes[mode] = box;
            panel.Children.Add(box);
        }
        return panel;
    }

    /// <summary>
    /// One "FSR 4" switch and one source picker under it: OptiScaler's built-in FSR 4, the user's modified 4.1.1b file
    /// (in place of OptiScaler's amd_fidelityfx_upscaler_dx12.dll) or the user's 4.0.2c file (amdxcffx64.dll next to the
    /// exe). File choices appear only on AMD cards and only for files added in Settings (or already in use). A game uses
    /// one source at a time: switching takes the previous source's file out of the game folder. Turning it on picks, on an
    /// RX 6000 (RDNA 2), 4.1.1b, then 4.0.2c, then built-in; on other cards, built-in. Plus OptiScaler's watermark.
    /// </summary>
    private FrameworkElement Fsr4Section(GameCardViewModel card, GpuInfo gpu, IniFile ini, InstallManifest manifest, string communityName)
    {
        var dir = card.Info.TargetDir!;
        var (inUse, inUseVersion) = OptiScalerOptions.Fsr4InUse(dir, manifest, ini, communityName);
        var file402 = AppServices.UserFiles.Get(UserFileKind.Fsr4Int8);
        var file411 = AppServices.UserFiles.Get(UserFileKind.Fsr411bInt8);
        var fsrDll = OptiScalerOptions.FsrUpscalerPath(manifest);
        var amd = gpu.Vendor == GpuVendor.Amd;
        var rdna2 = amd && gpu.Generation.StartsWith("RDNA 2", StringComparison.OrdinalIgnoreCase);

        var offered = new List<(Fsr4Source Source, string Label)> { (Fsr4Source.BuiltIn, "OptiScaler's built-in FSR 4") };
        if (inUse == Fsr4Source.File411b || (amd && file411 is not null && fsrDll is not null))
            offered.Add((Fsr4Source.File411b, "Your 4.1.1b file" + (file411?.Version is { } v1 ? $" (v{v1})" : "")));
        if (inUse == Fsr4Source.File402c || (amd && file402 is not null))
            offered.Add((Fsr4Source.File402c, "Your 4.0.2c file" + (file402?.Version is { } v2 ? $" (v{v2})" : "")));
        bool Has(Fsr4Source s) => offered.Any(o => o.Source == s);
        var preferred = rdna2 && Has(Fsr4Source.File411b) ? Fsr4Source.File411b
            : rdna2 && Has(Fsr4Source.File402c) ? Fsr4Source.File402c
            : Fsr4Source.BuiltIn;

        var toggle = new ToggleSwitch { Header = "FSR 4", IsOn = inUse != Fsr4Source.Off };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, "FSR 4");

        var source = new ComboBox
        {
            Header = "Source", MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = offered.Count > 1 ? Visibility.Visible : Visibility.Collapsed
        };
        foreach (var (s, label) in offered) source.Items.Add(new ComboBoxItem { Content = label, Tag = s });
        var shown = inUse != Fsr4Source.Off ? inUse : preferred;
        source.SelectedItem = source.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (Fsr4Source)i.Tag == shown) ?? source.Items[0];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(source, "FSR 4 source");

        Fsr4Source Chosen() => source.SelectedItem is ComboBoxItem { Tag: Fsr4Source s } ? s : Fsr4Source.BuiltIn;

        // Turns FSR 4 on with one source and takes the other sources' files out, or turns it off and takes both out.
        async Task Apply(Fsr4Source with)
        {
            if (with == inUse) return;
            if ((with == Fsr4Source.File411b && (file411 is null || fsrDll is null)) || (with == Fsr4Source.File402c && file402 is null))
            {
                Build();
                return;
            }
            var settings = with switch
            {
                Fsr4Source.File411b => OptiScalerOptions.Fsr411bSettings(true),
                Fsr4Source.BuiltIn => new List<IniSetting> { new("FSR", "Fsr4ForceEnableInt8", "true"), new("FSR", "Fsr4Update", null) },
                _ => OptiScalerOptions.Fsr411bSettings(false)
            };
            await Save(card, settings,
                addFileFrom: with == Fsr4Source.File402c ? file402!.Path : null,
                addFileAs: with == Fsr4Source.File402c ? communityName : null,
                removeFile: inUse == Fsr4Source.File402c ? communityName : null,
                overrideFrom: with == Fsr4Source.File411b ? file411!.Path : null,
                overrideAs: with == Fsr4Source.File411b ? fsrDll : null,
                removeOverride: inUse == Fsr4Source.File411b ? manifest.Overrides!.First(o => o.Kind == OptiScalerOptions.Fsr411bKind).Path : null);
        }

        toggle.Toggled += async (_, _) =>
        {
            if (_building) return;
            await Apply(toggle.IsOn ? Chosen() : Fsr4Source.Off);
        };
        source.SelectionChanged += async (_, _) =>
        {
            if (_building || !toggle.IsOn) return;
            await Apply(Chosen());
        };

        string Explain() => Chosen() switch
        {
            Fsr4Source.File411b => $"Your modified 4.1.1b file takes the place of OptiScaler's {fsrDll}, which is kept and comes back when you turn this off or uninstall. Sets Fsr4ForceEnableInt8 and Fsr4Update to true.",
            Fsr4Source.File402c => $"Copies your 4.0.2c file in next to the game as {communityName} (the game's own copy, if any, is backed up). OptiScaler.ini isn't changed.",
            _ => "Uses OptiScaler's built-in FSR 4 (Fsr4ForceEnableInt8). No extra file needed; cards without INT8 support still won't run it."
        };

        var watermarkOn = string.Equals(ini.Get("FSR", "Fsr4EnableWatermark"), "true", StringComparison.OrdinalIgnoreCase);
        var watermark = new ToggleSwitch { Header = "Show FSR 4 watermark", IsOn = watermarkOn };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(watermark, "Show FSR 4 watermark");
        watermark.Toggled += async (_, _) =>
        {
            if (_building) return;
            await Save(card, OptiScalerOptions.WatermarkSettings(watermark.IsOn));
        };

        return Group(
            toggle,
            source,
            Subtle(Explain()),
            inUse != Fsr4Source.Off ? Subtle("In use: " + OptiScalerOptions.Fsr4Label(inUse, inUseVersion)!["FSR 4: ".Length..]) : null,
            rdna2 && file411 is null && file402 is null
                ? Subtle("RX 6000 cards run FSR 4 best with a modified file (4.1.1b, or the older 4.0.2c) - add one in Settings.")
                : null,
            amd && file411 is not null && fsrDll is null
                ? Subtle("This OptiScaler has no amd_fidelityfx_upscaler_dx12.dll, so your 4.1.1b file can't be used here.")
                : null,
            Subtle("With it on, pick FSR 4 as the upscaler above."),
            watermark,
            Subtle("Takes effect after restarting the game. The watermark shows whether FSR 4 or the FSR 3 fallback is really running."));
    }

    /// <summary>
    /// "Frame cap": Off, the suggested cap for the main display (OptiScaler's VRR calculator: one frame time plus
    /// 0.3 ms) or a number typed in. Writes [Framerate] FramerateLimit, which OptiScaler applies through Reflex.
    /// </summary>
    private FrameworkElement FrameCapSection(GameCardViewModel card, IniFile ini, GpuInfo? gpu)
    {
        var current = OptiScalerOptions.CurrentFrameCap(ini);
        var refresh = Helpers.Display.MainRefreshRate();
        double? suggested = refresh is { } hz ? OptiScalerOptions.SuggestedFrameCap(hz) : null;
        string Fps(double v) => v.ToString(v % 1 == 0 ? "0" : "0.0", System.Globalization.CultureInfo.CurrentCulture);

        var items = new List<ComboBoxItem> { Item("off", "Off") };
        if (suggested is { } sv) items.Add(Item("suggested", $"Suggested: {Fps(sv)} fps (for your {refresh} Hz display)"));
        items.Add(Item("custom", "A number I type"));
        var box = Combo("Frame cap", items);
        var currentId = current is null ? "off" : suggested is { } s2 && Math.Abs(current.Value - s2) < 0.05 ? "suggested" : "custom";
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == currentId);

        var number = new NumberBox
        {
            Header = "Frames per second", Minimum = 10, Maximum = 1000, SmallChange = 1, LargeChange = 10, Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = current ?? (suggested is { } s3 ? Math.Floor(s3) : 60),
            Visibility = currentId == "custom" ? Visibility.Visible : Visibility.Collapsed
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(number, "Frame cap in frames per second");

        box.SelectionChanged += async (_, _) =>
        {
            if (_building || box.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            number.Visibility = id == "custom" ? Visibility.Visible : Visibility.Collapsed;
            if (id == "off") await Save(card, OptiScalerOptions.FrameCapSettings(null));
            else if (id == "suggested") await Save(card, OptiScalerOptions.FrameCapSettings(suggested));
            else if (!double.IsNaN(number.Value)) await Save(card, OptiScalerOptions.FrameCapSettings(Math.Round(number.Value, 1)));
        };
        // Saved when the number is confirmed (Enter, the spin buttons or leaving the box), not on every keystroke.
        number.ValueChanged += async (_, e) =>
        {
            if (_building || double.IsNaN(e.NewValue) || box.SelectedItem is not ComboBoxItem { Tag: "custom" }) return;
            if (current is { } c && Math.Abs(c - e.NewValue) < 0.05) return;
            await Save(card, OptiScalerOptions.FrameCapSettings(Math.Round(e.NewValue, 1)));
        };

        var offered = items.Select(i => ((string)i.Tag, i.Content as string ?? "")).ToList();
        return Group(
            WithGuide(box, AppServices.Catalog.Guides.FrameCap, offered, gpu),
            number,
            Subtle("Uses OptiScaler's own limiter, which works through Reflex: the game must support Reflex (on AMD and Intel cards OptiScaler's fakenvapi stands in for it). " +
                   "With frame generation on, the cap is the final frame rate you see, generated frames included. Takes effect after restarting the game."));
    }

    /// <summary>The upscaler choice whose settings match what the ini has now.</summary>
    private static string CurrentUpscaler(IniFile ini, IReadOnlyList<UpscalerChoice> choices) =>
        choices.FirstOrDefault(c => c.Settings.All(s =>
            string.Equals(ini.Get(s.Section, s.Key) ?? OptiScalerOptions.Auto, s.Value ?? OptiScalerOptions.Auto, StringComparison.OrdinalIgnoreCase)))?.Id
        ?? "auto";

    private async Task Save(GameCardViewModel card, IReadOnlyList<IniSetting> settings,
        string? addFileFrom = null, string? addFileAs = null, string? removeFile = null,
        string? overrideFrom = null, string? overrideAs = null, string? removeOverride = null)
    {
        if (Library is null) return;
        if (AppServices.PretendGpu is not null)
        {
            // A preview of another card's options: never written to a real game.
            if (ShowDialog is not null)
                await ShowDialog(new ContentDialog
                {
                    Title = "Nothing was changed",
                    Content = new TextBlock { Text = "A pretend graphics card is set in Settings > Developer options, so OptiScaler settings aren't changed. Turn it off to change them.", TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot
                });
            Build();
            return;
        }
        var result = await Library.ConfigureOptiScalerAsync(card, settings, addFileFrom, addFileAs, removeFile,
            overrideFrom, overrideAs, overrideFrom is null ? null : OptiScalerOptions.Fsr411bKind, removeOverride);
        if (result is { Success: false } && ShowDialog is not null)
        {
            await ShowDialog(new ContentDialog
            {
                Title = "The setting wasn't saved",
                Content = new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            });
            Build();
        }
    }

    private static ComboBox Combo(string header, IEnumerable<ComboBoxItem> items)
    {
        var box = new ComboBox { Header = header, MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var item in items) box.Items.Add(item);
        return box;
    }

    private static ComboBoxItem Item(string id, string label, bool enabled = true) =>
        new() { Tag = id, Content = label, IsEnabled = enabled };

    /// <summary>An option with a short grey description under its name.</summary>
    private static ComboBoxItem DescribedItem(string id, string label, string? description, bool enabled = true)
    {
        if (description is null) return Item(id, label, enabled);
        var content = new StackPanel { Spacing = 1 };
        content.Children.Add(new TextBlock { Text = label });
        content.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = Res("SubtleTextBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(content, label);
        return new ComboBoxItem { Tag = id, Content = content, IsEnabled = enabled };
    }

    /// <summary>The dropdown with a "Which one should I pick?" link beside it; the link opens the guide for the options offered.</summary>
    private static FrameworkElement WithGuide(ComboBox box, Core.Catalog.Guide guide, IEnumerable<(string Id, string Label)> offered, GpuInfo? gpu)
    {
        if (guide.Rows.Count == 0) return box;
        var flyout = new Flyout
        {
            Content = GuideTable(guide, offered.ToList(), gpu),
            // Opens below the link and extends to the left, so it stays inside the window; wider than WinUI's default.
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters = { new Setter(FrameworkElement.MaxWidthProperty, 700.0) }
            }
        };
        var link = new HyperlinkButton { Content = "Which one should I pick?", Padding = new Thickness(0), FontSize = 13 };
        link.Click += (_, _) => flyout.ShowAt(link);

        // The dropdown's label and the link share one line above it, so a wide dropdown doesn't squeeze the link.
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new TextBlock { Text = box.Header as string ?? "", VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(link);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, box.Header as string ?? "");
        box.Header = null;
        return new StackPanel { Children = { header, box } };
    }

    /// <summary>The guide as a small table (option, what it is, best for) plus a line about the user's card.</summary>
    private static StackPanel GuideTable(Core.Catalog.Guide guide, List<(string Id, string Label)> offered, GpuInfo? gpu)
    {
        var panel = new StackPanel { Spacing = 10, MaxWidth = 620 };
        panel.Children.Add(new TextBlock { Text = guide.Title, FontWeight = FontWeights.SemiBold, FontSize = 15 });

        var rows = guide.Rows.Where(r => offered.Any(o => o.Id == r.Id)).ToList();
        var bestFor = rows.Any(r => r.BestFor is not null);
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (bestFor) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });

        void Cell(string text, int row, int column, bool strong = false, bool subtle = false)
        {
            var block = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, MaxWidth = column == 0 ? 170 : double.PositiveInfinity,
                FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal
            };
            if (subtle) block.Foreground = Res("SubtleTextBrush");
            Grid.SetRow(block, row);
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell("Option", 0, 0, subtle: true);
        Cell("What it is", 0, 1, subtle: true);
        if (bestFor) Cell("Best for", 0, 2, subtle: true);
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = rows[i];
            Cell(row.Name ?? offered.First(o => o.Id == row.Id).Label, i + 1, 0, strong: true);
            Cell(row.TextFor(gpu?.Vendor, gpu?.Generation), i + 1, 1);
            if (bestFor) Cell(row.BestFor ?? "", i + 1, 2);
        }
        panel.Children.Add(grid);

        if (guide.CardNotes.FirstOrDefault(n => n.Matches(gpu?.Vendor, gpu?.Generation)) is { } note)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(new FontIcon { Glyph = "\uE7F4", FontSize = 14, Foreground = Res("UpshiftAccentBrush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            line.Children.Add(new TextBlock { Text = $"{gpu?.Name}: {note.Text}", TextWrapping = TextWrapping.Wrap, FontSize = 13, MaxWidth = 580 });
            panel.Children.Add(line);
        }
        if (guide.Footer is { } footer) panel.Children.Add(new TextBlock { Text = footer, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Res("SubtleTextBrush") });
        return panel;
    }

    private static TextBlock Subtle(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Res("SubtleTextBrush"), TextWrapping = TextWrapping.Wrap,
        Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible
    };

    private static StackPanel Group(params FrameworkElement?[] children)
    {
        var panel = new StackPanel { Spacing = 4 };
        foreach (var child in children.Where(c => c is not null)) panel.Children.Add(child!);
        return panel;
    }

    private static Microsoft.UI.Xaml.Media.Brush Res(string key) => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];
}
