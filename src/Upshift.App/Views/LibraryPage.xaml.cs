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

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || ViewModel.Selected is not { IsInstalledByUs: true } card) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Remove OptiScaler from {card.Name}?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Removes the files this app installed, OptiScaler.ini and the logs OptiScaler wrote, and puts back the game files it backed up. " +
                       "Your OptiScaler settings are saved first, so installing again can restore them. Other files that changed since the install are left in place and listed."
            },
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;

        var result = await ViewModel.UninstallOptiScalerAsync(card);
        if (result is null) return;
        if (!result.Success) await ShowMessageAsync("OptiScaler wasn't fully removed", result.Message);
        else if (result.KeptChanged.Count > 0 || result.LeftBehind.Count > 0)
        {
            var text = result.Message;
            if (result.LeftBehind.Count > 0)
                text += $" Also left in place, because OptiScaler created them while the game ran: {string.Join(", ", result.LeftBehind)}.";
            await ShowMessageAsync("OptiScaler removed, with files left behind", text);
        }
    }

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
