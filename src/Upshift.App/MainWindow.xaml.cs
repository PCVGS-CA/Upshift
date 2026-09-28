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
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        AppWindow.SetIcon(Path.Combine(assets, "Upshift.ico"));
        TitleBarLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(Path.Combine(assets, "Upshift.svg")));

        NavigateTo(typeof(LibraryPage));
        _ = ShowGpuAsync();
        // The "catalogUrl" catalog and, at most every 6 hours, the release check (both in the background).
        _ = Task.Run(Services.GameUpdates.StartupAsync);
    }

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
            _ => typeof(LibraryPage)
        });
    }

    private void NavigateTo(Type page)
    {
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
    }
}
