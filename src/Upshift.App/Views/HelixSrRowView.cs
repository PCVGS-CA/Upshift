using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Upshift.App.Services;
using Upshift.Core.Install;
using Upshift.Core.Services;

namespace Upshift.App.Views;

/// <summary>
/// HelixSR's row on the Updates page (experimental, still in testing), on every PC. On RDNA 1 and RDNA 2 cards (or with
/// the developer testing switch) it has a working "Set up" button; elsewhere the button is off and the row says why.
/// Setting up shows a notice first (what HelixSR is, everything its setup downloads and runs, that nothing it ships is
/// signed, time and disk space, and Smart App Control), then starts the setup in HelixSrSetupSession, which runs
/// independently of any page: each Updates page makes its own row, which shows the session's state and follows it.
/// "Remove" switches the games that use HelixSR back first, then deletes Upshift's shared folder, and offers to delete
/// the setup's tools too. Drawing the row and every button are guarded: HelixSR's state never closes Upshift.
/// </summary>
public sealed class HelixSrRowView : UserControl
{
    public const string StillInTesting = "Still in testing";

    private readonly StackPanel _body = new() { Spacing = 6 };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private TextBlock? _progressText;

    /// <summary>The last Remove's message (kept for the session, so it's still there after changing pages).</summary>
    private static string? _removeMessage;
    private static bool _removeFailed;

    public HelixSrRowView()
    {
        Content = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12),
            Background = Res("CardSurfaceBrush"), BorderBrush = Res("HairlineBrush"), BorderThickness = new Thickness(1),
            Child = _body
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "HelixSR");
        // The elapsed time counts every second while a setup runs, whether or not the setup prints anything.
        _clock.Tick += (_, _) => UpdateProgressText();
        Loaded += (_, _) =>
        {
            HelixSrSetupSession.Changed += OnChanged;
            AppServices.EffectiveGpuChanged += OnGpuChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            HelixSrSetupSession.Changed -= OnChanged;
            AppServices.EffectiveGpuChanged -= OnGpuChanged;
            _clock.Stop();
        };
        Refresh();
    }

    private void OnChanged() => Refresh();
    private void OnGpuChanged() => DispatcherQueue?.TryEnqueue(Refresh);

    /// <summary>Draws the row. Never throws: if anything about HelixSR can't be shown, a plain "Not set up" row says why.</summary>
    public void Refresh()
    {
        try { RefreshCore(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _body.Children.Clear();
            _body.Children.Add(new TextBlock { Text = $"{HelixSr.RowName} ({HelixSr.Experimental}, {StillInTesting.ToLowerInvariant()}): Not set up", FontWeight = FontWeights.SemiBold });
            _body.Children.Add(Line($"Upshift couldn't show HelixSR's state: {ex.Message}"));
        }
    }

    private void RefreshCore()
    {
        _body.Children.Clear();
        _progressText = null;
        var release = HelixSrGames.Release;
        var card = HelixSrGames.Card;
        var state = HelixSrGames.State;
        var installed = state.Installed;
        var running = HelixSrSetupSession.Running;

        // ---- name, Experimental, version, button ----
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = HelixSr.RowName, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(Badge(HelixSr.Experimental));

        var onPc = running ? "Setting up…" : installed is null ? "Not set up" : $"Set up: v{installed.Version}";
        var latest = release is null ? "" : $"Checked by Upshift: v{release.Version}";
        var button = new Button { MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 44, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "HelixSR action");
        var newer = installed is not null && release is not null && installed.Version != release.Version;
        if (running)
        {
            button.Content = "Cancel";
            button.Click += (_, _) => HelixSrSetupSession.Cancel();
        }
        else if (installed is not null && !newer)
        {
            button.Content = "Remove";
            button.Click += async (_, _) => await Guarded(RemoveAsync);
        }
        else
        {
            button.Content = newer ? $"Set up v{release!.Version}" : "Set up";
            button.IsEnabled = card.CanSetUp && release is not null;
            button.Click += async (_, _) => await Guarded(SetUpAsync);
        }

        // The same columns as the other rows on the Updates page; the button stops where theirs do (before their chevron).
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(UpdatesPage.NameWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(UpdatesPage.OnPcWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(UpdatesPage.LatestWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var onPcBlock = new TextBlock { Text = onPc, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var latestBlock = new TextBlock { Text = latest, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(onPcBlock, 1);
        Grid.SetColumn(latestBlock, 2);
        Grid.SetColumn(button, 3);
        grid.Children.Add(name);
        grid.Children.Add(onPcBlock);
        grid.Children.Add(latestBlock);
        grid.Children.Add(button);
        _body.Children.Add(grid);

        _body.Children.Add(Subtle(HelixSr.RowSubtitle));
        _body.Children.Add(Line($"{StillInTesting}: HelixSR's setup has completed once on a Radeon RX 6600, but HelixSR hasn't been run in a game by Upshift's developer yet. It stays off until you set it up and choose it for a game."));

        // ---- the running setup: step, elapsed time (counted every second), progress bar ----
        if (running)
        {
            _progressText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            UpdateProgressText();
            _body.Children.Add(_progressText);
            _body.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = HelixSrSetupSession.Fraction, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left });
            _body.Children.Add(Subtle("You can use the rest of Upshift meanwhile; the setup keeps running and this row follows it."));
            _clock.Start();
        }
        else _clock.Stop();

        // ---- the last result ----
        if (!running && HelixSrSetupSession.LastResult is { } last)
        {
            var took = HelixSrSetupSession.LastDuration is { } d ? $" It took {d:m\\:ss}." : "";
            if (last.Success)
                _body.Children.Add(Line($"{last.Message}{took} Choose \"{HelixSr.ChoiceLabel}\" as the upscaler in a game's OptiScaler options."));
            else
            {
                var sac = SmartAppControl.Read();
                _body.Children.Add(Line(last.Message + took + " Nothing was kept, and no game was changed."
                    + (last.MaybeBlocked && sac is SmartAppControlState.On or SmartAppControlState.Evaluation
                        ? " Smart App Control is on, and Windows may have blocked HelixSR's unsigned helper program. Upshift doesn't work around that: " +
                          "HelixSR can only be set up on this PC with Smart App Control off, which is your choice to make in Windows Security."
                        : ""), strong: true));
                if (last.Output is { Length: > 0 } output)
                    _body.Children.Add(new Expander
                    {
                        Header = "What HelixSR's setup printed", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Content = new TextBlock { Text = output, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
                    });
            }
        }
        if (!running && _removeMessage is { } removed) _body.Children.Add(Line(removed, strong: _removeFailed));

        // ---- a setup that's incomplete or can't be read: "Not set up", and why ----
        if (!running && state.Status == HelixSrStatus.Incomplete)
            _body.Children.Add(Line($"Not set up: {state.Missing} is missing from HelixSR's shared folder (a setup that didn't finish, or files removed by hand). " +
                                    (card.CanSetUp ? "Set it up again, or remove what's there." : "Remove what's there."), strong: true));
        if (!running && state.Status == HelixSrStatus.Unreadable)
            _body.Children.Add(Line($"Not set up: Upshift couldn't read HelixSR's state. {state.Problem?.TrimEnd('.')}. " +
                                    (card.CanSetUp ? "Set it up again, or remove what's there." : "Remove what's there."), strong: true));
        if (!running && state.Status is HelixSrStatus.Incomplete or HelixSrStatus.Unreadable)
        {
            var clean = new HyperlinkButton { Content = "Remove what's there", Padding = new Thickness(0), FontSize = 12 };
            clean.Click += async (_, _) => await Guarded(RemoveAsync);
            _body.Children.Add(clean);
        }

        // ---- why not, untested, testing ----
        if (!card.CanSetUp && card.Reason is { } why) _body.Children.Add(Line(why, strong: true));
        if (card.ShownForTesting && card.Reason is { } testing) _body.Children.Add(Line(testing));
        if (card.CanSetUp && card.Untested && !card.ShownForTesting)
            _body.Children.Add(Line("Untested: nobody has tried HelixSR on an RDNA 1 card (RX 5000) yet. Please report how it goes with Report a problem."));
        if (newer)
            _body.Children.Add(Line($"Upshift has checked HelixSR v{release!.Version}. Set it up again to use it; until then games keep using v{installed!.Version}."));
        if (AppServices.PretendGpu is { } pretend && card.CanSetUp)
            _body.Children.Add(Line($"Preview for {pretend.Name}: nothing is set up while a pretend card is set (Settings > Developer options)."));

        // ---- where, how many games, the project page ----
        if (installed is not null && !running)
        {
            var games = HelixSrGames.GamesUsingIt();
            _body.Children.Add(Subtle($"Shared folder: {HelixSrGames.FilesDir}. " +
                                      (games.Count == 0 ? "No game uses it yet: choose it as the upscaler in a game's OptiScaler options."
                                                        : $"Used by {string.Join(", ", games.Select(g => g.Name))}.")));
        }
        if (release is not null && Uri.TryCreate(release.PageUrl, UriKind.Absolute, out var page))
            _body.Children.Add(new HyperlinkButton { Content = "HelixSR on GitHub", NavigateUri = page, Padding = new Thickness(0), FontSize = 12 });
    }

    /// <summary>"Building the network, step 5 of 5 (…) · 4:12 so far".</summary>
    private void UpdateProgressText()
    {
        if (_progressText is null) return;
        if (!HelixSrSetupSession.Running) { _clock.Stop(); return; }
        var elapsed = HelixSrSetupSession.StartedUtc is { } s ? DateTime.UtcNow - s : TimeSpan.Zero;
        _progressText.Text = $"{HelixSrSetupSession.Step}… {elapsed:m\\:ss} so far";
    }

    // ---------------- set up ----------------

    private async Task SetUpAsync()
    {
        var release = HelixSrGames.Release;
        if (release is null || HelixSrSetupSession.Running || XamlRoot is null) return;
        if (AppServices.PretendGpu is not null)
        {
            await Dialog("Nothing was set up", "A pretend graphics card is set in Settings > Developer options, so HelixSR isn't set up. Turn it off first.");
            return;
        }
        if (!await NoticeAsync(release)) return;
        _removeMessage = null;
        HelixSrSetupSession.Start(release);
    }

    /// <summary>The notice the user must accept before the setup runs.</summary>
    private async Task<bool> NoticeAsync(Core.Catalog.HelixSrRelease release)
    {
        var panel = new StackPanel { Spacing = 10, MaxWidth = 560 };
        var sac = SmartAppControl.Read();
        if (sac is SmartAppControlState.On or SmartAppControlState.Evaluation)
            panel.Children.Add(new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
                Title = sac == SmartAppControlState.On ? "Smart App Control is on" : "Smart App Control is in evaluation mode",
                Message = "Windows may block HelixSR's setup or its DLL in games, because nothing HelixSR ships is signed. If that happens, " +
                          "Upshift says so and stops; it never works around Smart App Control."
            });

        panel.Children.Add(Para($"HelixSR is experimental and still in testing: its setup has completed once on a Radeon RX 6600, but it hasn't been run in a game by Upshift's developer. " +
                                "It's an unofficial project, not affiliated with or endorsed by NVIDIA or AMD. It runs NVIDIA's DLSS network on your Radeon's normal shader cores."));
        panel.Children.Add(Para("Its source code isn't published, so neither you nor Upshift can verify that it's safe. Nothing HelixSR ships is signed " +
                                "(its DLL and its helper program). Upshift only downloads the HelixSR release it has checked and runs the author's own setup, " +
                                "which it has read; it doesn't change what the setup does."));

        panel.Children.Add(new TextBlock { Text = "What the setup downloads", FontWeight = FontWeights.SemiBold });
        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(Bullet($"HelixSR {release.Version}, from its GitHub release (checked by SHA-256)."));
        foreach (var d in release.SetupDownloads) list.Children.Add(Bullet($"{d.Name}, from {d.From}."));
        panel.Children.Add(list);
        var license = new RichTextBlock { FontSize = 13 };
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = "NVIDIA's DLSS file is NVIDIA's, under NVIDIA's licence: " });
        if (Uri.TryCreate(release.DlssLicenseUrl, UriKind.Absolute, out var licenseUri))
        {
            var link = new Hyperlink { NavigateUri = licenseUri };
            link.Inlines.Add(new Run { Text = "read the licence" });
            p.Inlines.Add(link);
        }
        else p.Inlines.Add(new Run { Text = release.DlssLicenseUrl });
        p.Inlines.Add(new Run { Text = ". The setup uses it once to build HelixSR's two network files on this PC, then deletes it. Those files hold NVIDIA's network: they stay on this PC, Upshift never copies them anywhere else, and they're never put in a problem report." });
        license.Blocks.Add(p);
        panel.Children.Add(license);

        panel.Children.Add(Para("It runs the HelixSR author's setup script and helper program (launch_synth.exe) on this PC, without administrator rights."));
        panel.Children.Add(Para($"It takes about {release.SetupMinutes} minutes; its last step, compiling the shaders, takes most of that and shows no detail while it works. " +
                                $"Disk space: about {release.FilesMb} MB in Upshift's data folder (shared by all games), plus about {release.ToolsMb} MB of the setup's tools " +
                                $"(Python, numpy and the shader compiler) in {HelixSr.ToolsDir}, a folder the setup itself chooses. Remove deletes the first and offers to delete the second. " +
                                "You can use the rest of Upshift while it runs."));
        if (Uri.TryCreate(release.PageUrl, UriKind.Absolute, out var pageUri))
            panel.Children.Add(new HyperlinkButton { Content = "HelixSR on GitHub", NavigateUri = pageUri, Padding = new Thickness(0) });

        var dialog = new ContentDialog
        {
            Title = "Set up HelixSR (experimental, still in testing)?",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
            PrimaryButtonText = "Accept and set up",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // ---------------- remove ----------------

    private async Task RemoveAsync()
    {
        if (XamlRoot is null || HelixSrSetupSession.Running) return;
        var games = HelixSrGames.GamesUsingIt();
        var tools = new CheckBox { Content = $"Also delete the setup's tools in {HelixSr.ToolsDir} (Python, numpy and the shader compiler)", IsChecked = false };
        var panel = new StackPanel { Spacing = 10, MaxWidth = 520 };
        panel.Children.Add(Para(games.Count == 0
            ? $"This deletes HelixSR's shared folder ({HelixSrGames.FilesDir}), with its two network files."
            : $"{string.Join(", ", games.Select(g => g.Name))} {(games.Count == 1 ? "uses" : "use")} HelixSR. " +
              "Each goes back to the upscaler it had before; then HelixSR's shared folder is deleted, with its two network files."));
        if (Directory.Exists(HelixSr.ToolsDir)) panel.Children.Add(tools);
        var dialog = new ContentDialog
        {
            Title = "Remove HelixSR?", Content = panel, PrimaryButtonText = "Remove", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var messages = new List<string>();
        foreach (var game in games)
            messages.Add(await HelixSrGames.SwitchBackAsync(game, "HelixSR was removed"));
        if (HelixSrGames.GamesUsingIt() is { Count: > 0 } still)
        {
            // Never delete files a game still points at.
            _removeFailed = true;
            _removeMessage = $"HelixSR wasn't removed: {string.Join(", ", still.Select(g => g.Name))} couldn't be switched back. " + string.Join(" ", messages);
            HelixSrGames.RaiseNotices(messages);
            Refresh();
            return;
        }
        var problems = HelixSrSetup.Remove(AppServices.DataDir, tools.IsChecked == true);
        HelixSrGames.ClearPaused();
        _removeFailed = problems.Count > 0;
        _removeMessage = problems.Count == 0
            ? "HelixSR was removed." + (tools.IsChecked == true ? " Its setup's tools were deleted too." : "")
            : $"Part of HelixSR couldn't be removed (close any game that uses it, then try again): {string.Join("; ", problems)}";
        HelixSrGames.RaiseNotices(messages);
        AppServices.RaiseEffectiveGpuChanged();
        Refresh();
    }

    // ---------------- helpers ----------------

    /// <summary>Runs a button's action; anything unexpected is shown on the row instead of closing Upshift.</summary>
    private async Task Guarded(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _removeFailed = true;
            _removeMessage = $"Something went wrong: {ex.Message} No game was changed by it.";
            Refresh();
        }
    }

    private async Task Dialog(string title, string text)
    {
        if (XamlRoot is null) return;
        await new ContentDialog { Title = title, Content = Para(text), CloseButtonText = "OK", XamlRoot = XamlRoot }.ShowAsync();
    }

    private static Border Badge(string text) => new()
    {
        CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), VerticalAlignment = VerticalAlignment.Center,
        BorderBrush = Res("UpshiftAccentBrush"), BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = Res("UpshiftAccentBrush") }
    };

    private static TextBlock Para(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };

    private static TextBlock Bullet(string text) => new() { Text = "•  " + text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };

    private static TextBlock Line(string text, bool strong = false) => new()
    {
        Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
        FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal
    };

    private static TextBlock Subtle(string text) => Helpers.Hints.Note(text);

    private static Microsoft.UI.Xaml.Media.Brush Res(string key) => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];
}
