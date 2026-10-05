using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Upshift.App.Views;

/// <summary>Logo, version, licence and disclaimer, and the optional Buy Me a Coffee link (the only place it appears).</summary>
public sealed partial class AboutPage : Page
{
    private static readonly Uri CoffeeUri = new("https://buymeacoffee.com/pcvgs");

    public AboutPage()
    {
        InitializeComponent();
        AboutLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(Path.Combine(AppInfo.AssetsDir, "Upshift.svg")));
        AboutVersionText.Text = $"Version {AppInfo.Version}";
    }

    private async void ReportProblem_Click(object sender, RoutedEventArgs e) => await ReportDialog.ShowAsync(XamlRoot, null);

    /// <summary>Opens the page in the default browser.</summary>
    private async void BuyMeACoffee_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(CoffeeUri);
}
