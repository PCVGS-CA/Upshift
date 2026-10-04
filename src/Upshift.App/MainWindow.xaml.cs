using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Upshift.App.Helpers;
using Upshift.App.Views;
using Windows.Graphics;

namespace Upshift.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredTheme = TitleBarTheme.Light;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 0x1B, 0x1B, 0x1B);
        titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 0x8A, 0x8D, 0x93);
        titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 0xE6, 0xE8, 0xEB);
        titleBar.ButtonHoverForegroundColor = ColorHelper.FromArgb(255, 0x1B, 0x1B, 0x1B);
        titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 0xD6, 0xD9, 0xDD);
        titleBar.ButtonPressedForegroundColor = ColorHelper.FromArgb(255, 0x1B, 0x1B, 0x1B);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1440, 920));

        // Window and taskbar icon, and the logo in the title bar, from the Assets folder next to Upshift.exe.
        var assets = AppInfo.AssetsDir;
        AppWindow.SetIcon(Path.Combine(assets, "Upshift.ico"));
        TitleBarLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(Path.Combine(assets, "Upshift.svg")));

        NavigateTo(typeof(LibraryPage));
        _ = ShowGpuAsync();
        // The "catalogUrl" catalog and, at most every 6 hours, the release check (both in the background).
        _ = Task.Run(Services.GameUpdates.StartupAsync);

        // A newer Upshift, when "Check for updates automatically" is on (never downloaded until the user chooses).
        Services.AppUpdates.Changed += () => DispatcherQueue.TryEnqueue(ShowAppUpdate);
        AppUpdateBar.Closed += (_, _) => _appUpdateBarDismissed = true;
        _ = Task.Run(Services.AppUpdates.StartupAsync);
    }

    private bool _appUpdateBarDismissed;

    private void ShowAppUpdate()
    {
        AppUpdateBadge.Visibility = Services.AppUpdates.NewerExists ? Visibility.Visible : Visibility.Collapsed;
        var state = Services.AppUpdates.State;
        if (state is not (Services.AppUpdateState.Available or Services.AppUpdateState.Downloading))
        {
            AppUpdateBar.IsOpen = false;
            return;
        }
        AppUpdateBar.Title = "Update for Upshift";
        AppUpdateBar.Message = Services.AppUpdates.Message;
        if (AppUpdateBar.ActionButton is Button button) button.IsEnabled = state == Services.AppUpdateState.Available;
        if (!_appUpdateBarDismissed) AppUpdateBar.IsOpen = true;
    }

    private async void RestartToUpdate_Click(object sender, RoutedEventArgs e) => await Services.AppUpdates.DownloadAndRestartAsync();

    private async Task ShowGpuAsync()
    {
        var gpu = await AppServices.GetGpuAsync();
        GpuNameText.Text = gpu?.Name ?? "Not detected";
        GpuGenText.Text = gpu?.Generation ?? "Unknown";

        var (background, foreground) = Ui.VendorColors(gpu?.Vendor ?? Core.Models.GpuVendor.Unknown);
        GpuChip.Background = Ui.Brush(background);
        GpuGenText.Foreground = Ui.Brush(foreground);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string;
        NavigateTo(tag switch
        {
            "updates" => typeof(UpdatesPage),
            "settings" => typeof(SettingsPage),
            "about" => typeof(AboutPage),
            _ => typeof(LibraryPage)
        });
    }

    /// <summary>Selects a page in the navigation ("library", "updates", "settings" or "about").</summary>
    public void ShowPage(string tag)
    {
        if (Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag) is { } item) Nav.SelectedItem = item;
    }

    private void NavigateTo(Type page)
    {
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
    }
}
