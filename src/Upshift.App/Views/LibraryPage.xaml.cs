using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Upshift.App.ViewModels;
using Upshift.Core.Components;
using Upshift.Core.Install;
using Windows.Storage.Pickers;

namespace Upshift.App.Views;

public sealed partial class LibraryPage : Page
{
    public LibraryViewModel ViewModel { get; } = new();

    private const string NotesKey = "game.notes";

    /// <summary>The suggestion notes show three lines until "Show all" is pressed; the choice is remembered.</summary>
    private void ApplyNotesState()
    {
        RecommendationNotes.MaxLines = Helpers.Sections.IsOpen(NotesKey) ? 0 : 3;
        UpdateShowAll();
    }

    /// <summary>"Show all" when lines are hidden, "Show less" when more than three are shown, nothing otherwise.</summary>
    private void UpdateShowAll()
    {
        var open = Helpers.Sections.IsOpen(NotesKey);
        var threeLines = 3 * RecommendationNotes.FontSize * 1.4;
        var more = open ? RecommendationNotes.ActualHeight > threeLines + 1 : RecommendationNotes.IsTextTrimmed;
        RecommendationShowAll.Content = open ? "Show less" : "Show all";
        RecommendationShowAll.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RecommendationNotes_TrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) => UpdateShowAll();

    private void RecommendationShowAll_Click(object sender, RoutedEventArgs e)
    {
        Helpers.Sections.Set(NotesKey, !Helpers.Sections.IsOpen(NotesKey));
        ApplyNotesState();
    }

    public LibraryPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;

        // The code-built panels carry out their actions through the view model and share the one-dialog-at-a-time guard.
        SuggestionsPanel.Library = ViewModel;
        SuggestionsPanel.ShowDialog = ShowDialogAsync;
        OptionsPanel.Library = ViewModel;
        OptionsPanel.ShowDialog = ShowDialogAsync;
        OptionsPanel.UpdateDlss = UpdateDlssAsync;
        Dlss5Panel.Library = ViewModel;
        Dlss5Panel.ShowDialog = ShowDialogAsync;
        MeasurePanel.Library = ViewModel;
        MeasurePanel.ShowDialog = ShowDialogAsync;

        // Collapsible sections open as they were last left, for every game and across restarts.
        Helpers.Sections.Bind(FilesExpander, "game.files");
        Helpers.Sections.Bind(UpscalerFilesExpander, "game.upscalerFiles");
        Helpers.Sections.Bind(ChangesExpander, "game.changes");
        ApplyNotesState();
        RecommendationNotes.SizeChanged += (_, _) => UpdateShowAll();

        // Back from playing: read OptiScaler's log again for the selected game.
        AppServices.WindowActivated += () => DispatcherQueue.TryEnqueue(() => ViewModel.Selected?.RefreshLoadStatus());

        // Middle-click auto-scroll in the game grid and the game panel.
        Helpers.AutoScroll.Attach(GamesGrid);
        Helpers.AutoScroll.Attach(DetailsScroller);

        // Keep the grid's highlight in step when the view model changes the selection (after a rescan, say).
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.Selected) && !ReferenceEquals(GamesGrid.SelectedItem, ViewModel.Selected))
                GamesGrid.SelectedItem = ViewModel.Selected;
        };

        // After a full rescan rebuilt the list, bring the selected game back into view.
        ViewModel.ScrollToSelectedRequested += () =>
        {
            if (ViewModel.Selected is { } selected)
            {
                GamesGrid.SelectedItem = selected;
                GamesGrid.ScrollIntoView(selected, ScrollIntoViewAlignment.Default);
            }
        };

        Loaded += async (_, _) => await ViewModel.InitializeAsync();
    }

    private void GamesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GamesGrid.SelectedItem is GameCardViewModel game) ViewModel.Selected = game;
    }

    private void FilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewModel.FilterIndex = Math.Max(0, sender.Items.IndexOf(sender.SelectedItem));
    }

    // ---------------- Play ----------------

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is { } card) await ViewModel.PlayAsync(card);
    }

    /// <summary>The play icon on a card: selects the game (so a failure shows in its details) and starts it.</summary>
    private async void CardPlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GameCardViewModel card) return;
        ViewModel.Selected = card;
        await ViewModel.PlayAsync(card);
    }

    private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => ShowPlayOverlay(sender, true);

    private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => ShowPlayOverlay(sender, false);

    private static void ShowPlayOverlay(object sender, bool show)
    {
        if (sender is FrameworkElement card && card.FindName("PlayOverlay") is UIElement play)
            play.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LaunchError_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (ViewModel.Selected is { } card) card.LaunchError = null;
    }

    private void LaunchOptions_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is { } card) ViewModel.SetLaunchOptions(card, LaunchOptionsBox.Text);
    }

    private void LaunchOptions_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && ViewModel.Selected is { } card)
        {
            ViewModel.SetLaunchOptions(card, LaunchOptionsBox.Text);
            e.Handled = true;
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) await ViewModel.AddFolderAsync(folder.Path);
    }

    private async void ChoosePicture_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GameCardViewModel card) return;

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow));

        var file = await picker.PickSingleFileAsync();
        if (file is not null) await ViewModel.ChoosePictureAsync(card, file.Path);
    }

    private async void ResetPicture_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GameCardViewModel card) await ViewModel.ResetPictureAsync(card);
    }

    private async void ChangeExe_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } card) return;

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow));

        var file = await picker.PickSingleFileAsync();
        if (file is not null) await ViewModel.ChangeExeAsync(card, file.Path);
    }

    private async void UseAutomaticExe_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is { } card) await ViewModel.UseAutomaticExeAsync(card);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || ViewModel.Selected is not { CanInstall: true } card || card.Info.TargetDir is not { } target) return;

        var component = await ViewModel.PrepareOptiScalerAsync();
        if (component is null) return;

        // Confirmation: version, loading name (Advanced), and which game files get backed up.
        var loadsAs = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var backups = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var blockers = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, Title = "Can't install" };
        var names = new ComboBox { ItemsSource = OptiScalerInstaller.ProxyNames, MinWidth = 200, Header = "Load OptiScaler as" };
        var (versions, releases, chosenVersion) = VersionChoice(component.Version);

        // A leftover OptiScaler.ini doesn't block: it's backed up and removed when "Remove it and continue" is ticked.
        var removeLeftover = new CheckBox { Content = "Remove it and continue", IsChecked = true };
        var leftoverBar = new InfoBar
        {
            Severity = InfoBarSeverity.Warning, IsClosable = false,
            Title = "OptiScaler.ini from an earlier install is still in the game folder",
            Message = "It's backed up to Upshift's saved settings first, then removed, so the new install starts clean.",
            Content = removeLeftover
        };

        // "Restore my previous OptiScaler settings": the newest of the copy saved at uninstall and a leftover ini.
        var settingsFolder = SavedSettings.FolderFor(AppServices.DataDir, card.Name);
        var saved = SavedSettings.Latest(settingsFolder);
        var leftoverPath = Path.Combine(target, "OptiScaler.ini");
        var leftoverTime = File.Exists(leftoverPath) && OptiScalerInstaller.ReadManifest(target) is not { Removed: false }
            ? File.GetLastWriteTimeUtc(leftoverPath) : (DateTime?)null;
        var useLeftover = leftoverTime is { } lt && (saved is null || lt > saved.SavedUtc);
        var restoreTime = useLeftover ? leftoverTime : saved?.SavedUtc;
        var restore = new CheckBox
        {
            IsChecked = true,
            Content = $"Restore my previous OptiScaler settings (from {restoreTime?.ToLocalTime():d MMM yyyy, HH:mm})",
            Visibility = restoreTime is null ? Visibility.Collapsed : Visibility.Visible
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Install OptiScaler into {card.Name}?",
            PrimaryButtonText = "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Spacing = 12,
                    MaxWidth = 520,
                    Children =
                    {
                        versions,
                        releases,
                        new TextBlock { Text = $"Into: {target}", TextWrapping = TextWrapping.Wrap, FontSize = 12, IsTextSelectionEnabled = true },
                        loadsAs,
                        backups,
                        restore,
                        new Expander { Header = "Advanced", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = names },
                        leftoverBar,
                        blockers
                    }
                }
            }
        };

        void Refresh(string? chosen)
        {
            var preview = OptiScalerInstaller.Preview(component.Folder, target, chosen);
            loadsAs.Text = $"Loads as: {preview.ProxyName}";
            backups.Text = preview.WillBackUp.Count == 0
                ? $"No game files are overwritten. {preview.WillAdd.Count} files are added."
                : $"Backed up first (the game's own copies, put back on uninstall): {string.Join(", ", preview.WillBackUp)}. {preview.WillAdd.Count} other files are added.";
            blockers.Message = string.Join(" ", preview.Blockers);
            blockers.IsOpen = preview.Blockers.Count > 0;
            leftoverBar.IsOpen = preview.Leftovers.Count > 0;
            dialog.IsPrimaryButtonEnabled = preview.Blockers.Count == 0 && (preview.Leftovers.Count == 0 || removeLeftover.IsChecked == true);
            if (names.SelectedItem as string != preview.ProxyName) names.SelectedItem = preview.ProxyName;
        }

        // Start on the loading name a suggestion picked for this game, if any.
        Refresh(AppServices.InstallPrefs.ProxyFor(card.Info.Id));
        names.SelectionChanged += (_, _) => Refresh(names.SelectedItem as string);
        removeLeftover.Checked += (_, _) => Refresh(names.SelectedItem as string);
        removeLeftover.Unchecked += (_, _) => Refresh(names.SelectedItem as string);

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var version = chosenVersion();
        if (version != component.Version)
        {
            component = await ViewModel.PrepareOptiScalerAsync(version);
            if (component is null)
            {
                await ShowMessageAsync("OptiScaler wasn't installed", ViewModel.StatusText);
                return;
            }
        }
        // The defaults of the version the saved settings came from, when that version is still downloaded.
        var oldDefaults = !useLeftover && saved is { ComponentId: { } savedId, Version: { } savedVersion }
            && AppServices.Catalog.Components.FirstOrDefault(c => c.Id == savedId) is { } savedComponent
            && AppServices.Components.TryGetCached(savedComponent, savedVersion) is { } cached
            && File.Exists(Path.Combine(cached.Folder, "OptiScaler.ini"))
            ? Path.Combine(cached.Folder, "OptiScaler.ini") : null;
        var restoreChosen = restore.IsChecked == true && restoreTime is not null;
        var result = await ViewModel.InstallOptiScalerAsync(card, component, names.SelectedItem as string ?? "dxgi.dll", new InstallRestore(
            settingsFolder,
            RemoveLeftovers: removeLeftover.IsChecked == true,
            RestoreFrom: restoreChosen && !useLeftover ? saved?.Path : null,
            RestoreFromLeftover: restoreChosen && useLeftover,
            RestoreDefaultsFrom: restoreChosen ? oldDefaults : null));
        if (result is { Success: false }) await ShowMessageAsync("OptiScaler wasn't installed", result.Message);
    }

    /// <summary>
    /// The Install dialog's version choice: Stable (the catalog's tested release, the default), the latest Beta, or any
    /// release from the last update check. Returns the two boxes and a function giving the chosen tag.
    /// </summary>
    private static (ComboBox Versions, ComboBox Releases, Func<string> Chosen) VersionChoice(string stable)
    {
        var opti = AppServices.OptiScaler;
        var status = AppServices.Updates.Status(opti.Id);
        var beta = status.Beta is { } b && ComponentStore.IsDownloadable(opti, b) ? b.Tag : null;
        var listed = status.Releases.Where(r => ComponentStore.IsDownloadable(opti, r)).ToList();

        var versions = new ComboBox { Header = "Version", MinWidth = 320 };
        versions.Items.Add(new ComboBoxItem { Content = $"Stable: {stable} (recommended)" });
        versions.Items.Add(new ComboBoxItem
        {
            Content = beta is null ? "Latest beta (check for updates first)" : $"Latest beta: {beta}{(beta == stable ? " (same as Stable)" : "")}",
            IsEnabled = beta is not null
        });
        versions.Items.Add(new ComboBoxItem
        {
            Content = listed.Count == 0 ? "Pick from the list (check for updates first)" : "Pick from the list…",
            IsEnabled = listed.Count > 0
        });
        versions.SelectedIndex = 0;

        var releases = new ComboBox { Header = "Release", MinWidth = 320, Visibility = Visibility.Collapsed };
        foreach (var r in listed)
            releases.Items.Add(new ComboBoxItem
            {
                Content = $"{r.Tag}{(r.Prerelease ? " (pre-release)" : "")}{(r.Tag == stable ? " (Stable)" : "")}{(r.Published is { } p ? $" · {p.LocalDateTime:d MMM yyyy}" : "")}",
                Tag = r.Tag
            });
        releases.SelectedIndex = listed.Count > 0 ? 0 : -1;
        versions.SelectionChanged += (_, _) => releases.Visibility = versions.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

        return (versions, releases, () => versions.SelectedIndex switch
        {
            1 when beta is not null => beta,
            2 when (releases.SelectedItem as ComboBoxItem)?.Tag is string tag => tag,
            _ => stable
        });
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || ViewModel.Selected is not { HasUpdate: true, UpdateVersion: { } version } card) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Update OptiScaler in {card.Name}?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"From {card.InstalledVersion} to {version}, loading under the same file name. Everything is saved first, so \"Undo last update\" " +
                       $"can put it back exactly. Your OptiScaler settings are kept; settings that are new in {version} start at their defaults."
            },
            PrimaryButtonText = "Update",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;
        if (await ViewModel.UpdateOptiScalerAsync(card, version) is { } result)
            await ShowMessageAsync(result.Success ? "OptiScaler updated" : "OptiScaler wasn't updated", result.Message);
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || ViewModel.Selected is not { CanUndo: true } card) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Undo the last update in {card.Name}?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "The previous OptiScaler files and OptiScaler.ini come back exactly as they were before the update. " +
                       "Settings changed since the update are lost."
            },
            PrimaryButtonText = "Undo update",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;
        if (await ViewModel.UndoUpdateAsync(card) is { } result)
            await ShowMessageAsync(result.Success ? "Update undone" : "The update wasn't undone", result.Message);
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || ViewModel.Selected is not { NeedsRepair: true } card) return;
        if (await ViewModel.RepairOptiScalerAsync(card) is { } result)
            await ShowMessageAsync(result.Success ? "OptiScaler repaired" : "OptiScaler wasn't repaired", result.Message);
    }

    /// <summary>
    /// Uninstall that explains itself. First a check (reads only): the game or its launcher still running, files in use,
    /// files changed since the install ("Leave them" or "Remove anyway and restore the originals"), missing backups, and
    /// whether Windows will ask for permission. Then the uninstall, and a check that the folder is back as it was.
    /// </summary>
    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || ViewModel.Selected is not { IsInstalledByUs: true } card || card.Info.TargetDir is not { } target) return;

        OptiScalerInstaller.UninstallCheck check;
        while (true)
        {
            check = await Task.Run(() => OptiScalerInstaller.CheckUninstall(target, card.Info.InstallDir));
            if (check.Running.Count == 0 && check.InUse.Count == 0) break;
            var what = new List<string>();
            if (check.Running.Count > 0) what.Add("Still running from the game's folder: " + string.Join(", ", check.Running) + ".");
            if (check.InUse.Count > 0) what.Add("In use by another program: " + string.Join(", ", check.InUse) + ".");
            var answer = await ShowDialogAsync(new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Close the game and try again",
                Content = Wrapped(string.Join("\n", what) + "\n\nOptiScaler's files can't be removed while the game (or its launcher) has them open. Close it, then press Try again."),
                PrimaryButtonText = "Try again", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
            });
            if (answer != ContentDialogResult.Primary) return;
        }

        var removeChanged = false;
        var permission = check.NeedsPermission ? "\n\nThis folder needs Windows permission to change, so Windows will ask next." : "";
        if (check.Changed.Count > 0 || check.MissingBackups.Count > 0)
        {
            var text = new List<string>();
            if (check.Changed.Count > 0)
            {
                text.Add("These files changed since Upshift installed them:");
                text.AddRange(check.Changed.Select(c => $"• {Path.Combine(target, c.Path)}: {c.What}"));
                text.Add("\nLeave them: they stay as they are, and Upshift keeps a note of them so you can remove them later.");
                text.Add("Remove anyway and restore the originals: Upshift's files go even though they changed, and the game's originals it backed up are put back.");
            }
            if (check.MissingBackups.Count > 0)
            {
                text.Add((text.Count > 0 ? "\n" : "") + "Upshift's backup of these originals is missing, so they can't be put back (the game launcher's \"Verify files\" can restore them):");
                text.AddRange(check.MissingBackups.Select(m => "• " + Path.Combine(target, m)));
            }
            var answer = await ShowDialogAsync(new ContentDialog
            {
                XamlRoot = XamlRoot, Title = $"Before removing OptiScaler from {card.Name}",
                Content = Scrollable(string.Join("\n", text) + permission),
                PrimaryButtonText = check.Changed.Count > 0 ? "Remove anyway and restore the originals" : "Uninstall",
                SecondaryButtonText = check.Changed.Count > 0 ? "Leave them" : "",
                CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            });
            if (answer == ContentDialogResult.None) return;
            removeChanged = answer == ContentDialogResult.Primary && check.Changed.Count > 0;
        }
        else
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = $"Remove OptiScaler from {card.Name}?",
                Content = Wrapped("Removes the files this app installed, OptiScaler.ini and the logs OptiScaler wrote, and puts back the game files it backed up. " +
                                  "Your OptiScaler settings are saved first, so installing again can restore them. Afterwards Upshift checks the folder file by file." + permission),
                PrimaryButtonText = "Uninstall", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            };
            if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;
        }

        InstallResult? result;
        while (true)
        {
            result = await ViewModel.UninstallOptiScalerAsync(card, removeChanged);
            if (result is not { PermissionRefused: true }) break;
            var again = await ShowDialogAsync(new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Windows permission was refused",
                Content = Wrapped("This game's folder can only be changed with Windows' permission, and the prompt was declined, so nothing was changed."),
                PrimaryButtonText = "Try again with permission", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
            });
            if (again != ContentDialogResult.Primary) return;
        }
        if (result is null) return;
        await ShowUninstallResultAsync(target, result);
    }

    /// <summary>"This game's folder is back to how it was before Upshift", or what remains (full paths) with Open folder.</summary>
    private async Task ShowUninstallResultAsync(string target, InstallResult result)
    {
        var left = result.Remaining.Concat(result.KeptChanged.Select(k => $"{Path.Combine(target, k)}: changed since the install, left in place as you chose")).ToList();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = result.FolderRestored ? "OptiScaler removed" : result.Success ? "OptiScaler removed, with files left" : "OptiScaler wasn't fully removed",
            Content = left.Count == 0 ? Wrapped(result.Message) : Scrollable(result.Message + "\n\nStill in the game's folder:\n" + string.Join("\n", left.Select(l => "• " + l))),
            CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close
        };
        if (left.Count > 0) dialog.PrimaryButtonText = "Open folder";
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            System.Diagnostics.Process.Start("explorer.exe", $"\"{target}\"")?.Dispose();
    }

    /// <summary>"Remove what Upshift added" without a record: lists OptiScaler's files, asks which to remove.</summary>
    private async void RemoveWithoutRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || ViewModel.Selected is not { RecordDamaged: true } card || card.Info.TargetDir is not { } target) return;
        var (found, backups) = await Task.Run(() => OptiScalerInstaller.ScanWithoutRecord(target));
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Wrapped("Upshift found these in the game's folder. The ones that are surely OptiScaler's are ticked; the others may be the game's own, so check before ticking them. Nothing is removed until you press Remove."));
        var boxes = new List<(CheckBox Box, string Path)>();
        foreach (var f in found)
        {
            var box = new CheckBox { IsChecked = f.SurelyOptiScaler, Content = Wrapped($"{f.Path}{(f.IsFolder ? "\\" : "")}: {f.What}") };
            boxes.Add((box, f.Path));
            panel.Children.Add(box);
        }
        var restore = new List<(CheckBox Box, RestoreJob Job)>();
        if (backups.Count > 0)
        {
            panel.Children.Add(Wrapped("\nOriginals Upshift backed up, to copy back first:"));
            foreach (var b in backups)
            {
                var box = new CheckBox { IsChecked = true, Content = Wrapped($"{b.Path} (from {b.Backup})") };
                restore.Add((box, b));
                panel.Children.Add(box);
            }
        }
        var answer = await ShowDialogAsync(new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Remove what Upshift added",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        });
        if (answer != ContentDialogResult.Primary) return;
        var paths = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Path).ToList();
        var jobs = restore.Where(r => r.Box.IsChecked == true).Select(r => r.Job).ToList();
        if (paths.Count == 0 && jobs.Count == 0) return;
        var result = await ViewModel.RemoveWithoutRecordAsync(card, paths, jobs);
        if (result is not null) await ShowUninstallResultAsync(target, result);
    }

    /// <summary>Sets OptiScaler's log to its lightest level for this game (recorded like any other setting).</summary>
    private async void TurnOnLogCheck_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } card) return;
        await ReportAsync(ViewModel.ConfigureOptiScalerAsync(card, OptiScalerLog.LogSettings(true)), "The check wasn't turned on");
    }

    private void CheckLogAgain_Click(object sender, RoutedEventArgs e) => ViewModel.Selected?.RefreshLoadStatus();

    private async void ReportGameProblem_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || ViewModel.Selected is not { } card) return;
        await ReportDialog.ShowAsync(XamlRoot, card, ShowDialogAsync);
    }

    private static TextBlock Wrapped(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    private static ScrollViewer Scrollable(string text) => new() { Content = Wrapped(text), MaxHeight = 420 };

    // ---------------- Upscaler files ----------------

    private async void UpdateFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not UpscalerFileRowViewModel { CanUpdate: true } row) return;
        await ReportAsync(ViewModel.UpdateUpscalerFilesAsync(row.Card, new[] { row.Item.RelativePath }), $"{row.Item.FileName} wasn't updated");
    }

    /// <summary>"Restore original" on one file: the game's own copy comes back, checked by SHA-256.</summary>
    private async void RestoreFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not UpscalerFileRowViewModel { CanRestore: true } row) return;
        if (await ViewModel.RestoreUpscalerFilesAsync(row.Card, new[] { row.Item.RelativePath }) is { } result && (!result.Success || result.KeptChanged.Count > 0))
            await ShowMessageAsync(result.Success ? "Original restored, with a file left in place" : $"{row.Item.FileName} wasn't restored", result.Message);
    }

    private async void UpdateAllFiles_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { CanUpdateAnyFile: true } card) return;
        await ReportAsync(ViewModel.UpdateUpscalerFilesAsync(card, card.UpdatablePaths), "The files weren't updated");
    }

    /// <summary>The DLSS bar's "Update DLSS" (and the same action from the DLSS model dropdown).</summary>
    private async void UpdateDlss_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is { HasDlssBar: true } card) await UpdateDlssAsync(card);
    }

    private Task UpdateDlssAsync(GameCardViewModel card) => ReportAsync(ViewModel.UpdateDlssAsync(card), "DLSS wasn't updated");

    private async void Reapply_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { CanReapply: true } card) return;
        await ReportAsync(ViewModel.UpdateUpscalerFilesAsync(card, card.RestoredPaths), "The update wasn't re-applied");
    }

    private async void RestoreFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || ViewModel.Selected is not { CanRestoreFiles: true } card) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Put back {card.Name}'s original files?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Every DLSS, FSR and XeSS file Upshift updated in this game goes back to the game's own copy, checked byte for byte. " +
                       "A file that changed after Upshift updated it (a game update, say) is left as it is."
            },
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;
        if (await ViewModel.RestoreUpscalerFilesAsync(card) is { } result && (!result.Success || result.KeptChanged.Count > 0))
            await ShowMessageAsync(result.Success ? "Originals restored, with files left in place" : "The originals weren't restored", result.Message);
    }

    /// <summary>Waits for an upscaler file action; failures get a message box, success shows on the status line.</summary>
    private async Task ReportAsync(Task<InstallResult?> action, string failTitle)
    {
        if (await action is { Success: false } result) await ShowMessageAsync(failTitle, result.Message);
    }

    private void HideGame_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GameCardViewModel card) ViewModel.HideGame(card);
    }

    private void DlssNoticeReview_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.DismissDlssNotice();
        (App.MainAppWindow as MainWindow)?.ShowPage("updates");
    }

    private void DlssNotice_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissDlssNotice();

    private void OpenIni_Click(object sender, RoutedEventArgs e)
    {
        var ini = ViewModel.Selected?.IniPath;
        if (string.IsNullOrEmpty(ini) || !File.Exists(ini)) return;
        Process.Start(new ProcessStartInfo(ini) { UseShellExecute = true });
    }

    private Task ShowMessageAsync(string title, string message) =>
        ShowDialogAsync(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "OK"
        });

    private bool _dialogOpen;

    /// <summary>
    /// WinUI allows one ContentDialog at a time and throws on a second one. A click that reaches the page while a
    /// dialog is up (accessibility tools can do that) must not bring the app down, so it's simply ignored.
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen) return ContentDialogResult.None;
        _dialogOpen = true;
        try { return await dialog.ShowAsync(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return ContentDialogResult.None;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.Selected?.TargetText;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
