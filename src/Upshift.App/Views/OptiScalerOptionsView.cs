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

    /// <summary>True while controls are being filled in, so setting their initial values doesn't write anything.</summary>
    private bool _building;

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
        var gpu = AppServices.Gpu;
        var catalog = AppServices.Catalog;
        var body = new StackPanel { Spacing = 14 };

        // ---- FSR 4 (INT8) state first: it decides whether FSR 4 is in the upscaler list ----
        var int8Shown = OptiScalerOptions.Int8OptionsShown(gpu, catalog.Fsr4);
        var forceInt8 = string.Equals(ini.Get("FSR", "Fsr4ForceEnableInt8"), "true", StringComparison.OrdinalIgnoreCase);
        var communityName = catalog.Fsr4.CommunityInt8FileName;
        var communityOn = manifest.Added.Concat(manifest.Replaced).Any(f => f.Path.Equals(communityName, StringComparison.OrdinalIgnoreCase));

        // ---- Upscaler ----
        var upscalers = OptiScalerOptions.Upscalers(gpu, card.Info.Api, catalog.Fsr4, int8Shown && (forceInt8 || communityOn));
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

        // ---- DLSS model (NVIDIA RTX only) ----
        if (OptiScalerOptions.IsNvidiaRtx(gpu))
            body.Children.Add(DlssSection(card, ini, gpu!));

        // ---- FSR 4 (INT8) ----
        if (int8Shown)
            body.Children.Add(Int8Section(card, forceInt8, communityOn, communityName));

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

        // Each model with its one-line description from the guide under its name.
        var guide = AppServices.Catalog.Guides.DlssModel;
        var items = models.Select(m => DescribedItem(m.Id,
            m.Available ? m.Label : $"{m.Label} — update this game's DLSS file to unlock",
            guide.Rows.FirstOrDefault(r => r.Id == m.Id)?.TextFor(gpu.Vendor, gpu.Generation),
            m.Available)).ToList();
        items.Add(Item("advanced", "Advanced (one model per quality mode)"));
        var box = Combo("DLSS model", items);
        box.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == currentId) ?? items[0];

        var note = Subtle(models.FirstOrDefault(m => m.Id == currentId)?.Note ?? "");
        var advanced = AdvancedPresets(card, ini, presets, gameDlss);
        advanced.Visibility = currentId == "advanced" ? Visibility.Visible : Visibility.Collapsed;

        box.SelectionChanged += async (_, _) =>
        {
            if (_building || box.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            if (id == "advanced")
            {
                advanced.Visibility = Visibility.Visible;
                note.Text = "Pick a model for each quality mode below.";
                return;
            }
            await Save(card, models.First(m => m.Id == id).Settings);
        };

        var dlssFile = card.Info.Upscalers.FirstOrDefault(u => u.FileName.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase))?.Version;
        var version = dlssFile is null ? "This game has no DLSS file of its own." : $"This game's DLSS file: {Helpers.Ui.DlssLabel(dlssFile)}.";
        var locked = models.Any(m => !m.Available)
            ? " Newer models need a newer nvngx_dlss.dll in the game; update this game's DLSS file to unlock them."
            : "";
        return Group(WithGuide(box, guide, models.Select(m => (m.Id, m.Label)), gpu), note, advanced, Subtle(version + locked),
            Subtle("If you've set a DLSS override for this game in the NVIDIA App, it may win over this setting."));
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

    private FrameworkElement Int8Section(GameCardViewModel card, bool forceInt8, bool communityOn, string communityName)
    {
        var builtIn = new ToggleSwitch
        {
            Header = "FSR 4 on this card (uses OptiScaler's built-in FSR 4)",
            IsOn = forceInt8
        };
        builtIn.Toggled += async (_, _) =>
        {
            if (_building) return;
            await Save(card, OptiScalerOptions.ForceInt8Settings(builtIn.IsOn));
        };

        var userFile = AppServices.UserFiles.Get(UserFileKind.Fsr4Int8);
        var community = new ToggleSwitch
        {
            Header = "FSR 4 with your own 4.0.2c file (recommended for AMD RX 6000)",
            IsOn = communityOn,
            IsEnabled = userFile is not null || communityOn
        };
        community.Toggled += async (_, _) =>
        {
            if (_building) return;
            if (community.IsOn && userFile is not null)
                await Save(card, Array.Empty<IniSetting>(), addFileFrom: userFile.Path, addFileAs: communityName);
            else if (!community.IsOn)
                await Save(card, Array.Empty<IniSetting>(), removeFile: communityName);
        };

        return Group(builtIn,
            Subtle("Sets Fsr4ForceEnableInt8. No extra file needed. Cards without INT8 support still won't run it."),
            community,
            Subtle(userFile is null && !communityOn
                ? "Add the file in Settings first."
                : $"Copies your file in as {communityName} (the game's own copy, if any, is backed up)."),
            Subtle("With either one on, pick FSR 4 as the upscaler above. OptiScaler's watermark (Fsr4EnableWatermark) shows whether FSR 4 or FSR 3 is really running."));
    }

    /// <summary>The upscaler choice whose settings match what the ini has now.</summary>
    private static string CurrentUpscaler(IniFile ini, IReadOnlyList<UpscalerChoice> choices) =>
        choices.FirstOrDefault(c => c.Settings.All(s =>
            string.Equals(ini.Get(s.Section, s.Key) ?? OptiScalerOptions.Auto, s.Value ?? OptiScalerOptions.Auto, StringComparison.OrdinalIgnoreCase)))?.Id
        ?? "auto";

    private async Task Save(GameCardViewModel card, IReadOnlyList<IniSetting> settings,
        string? addFileFrom = null, string? addFileAs = null, string? removeFile = null)
    {
        if (Library is null) return;
        var result = await Library.ConfigureOptiScalerAsync(card, settings, addFileFrom, addFileAs, removeFile);
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
