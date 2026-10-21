using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Upshift.App.Services;

namespace Upshift.App.Views;

/// <summary>
/// The Performance tab's line about SCSKiller, a companion tool for shader stutter: "Get SCSKiller" (its releases page)
/// when it isn't found, "Open SCSKiller" (only starts it) when it is. Looked for again whenever the window comes back.
/// </summary>
public sealed class ScsKillerView : UserControl
{
    private readonly Button _button = new();
    private readonly TextBlock _problem = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    public ScsKillerView()
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "SCSKiller");
        var text = new TextBlock { Text = "Shader stutter on first play? Use SCSKiller (free, open source).", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _button.Click += async (_, _) =>
        {
            if (ScsKiller.Find() is null)
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(ScsKiller.ReleasesUrl));
                return;
            }
            var problem = ScsKiller.Open();
            _problem.Text = problem ?? "";
            _problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        };

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(text);
        Grid.SetColumn(_button, 1);
        _button.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_button);

        Content = new Border
        {
            Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Background = Res("CardSurfaceBrush"), BorderBrush = Res("HairlineBrush"),
            Child = new StackPanel { Spacing = 6, Children = { row, _problem } }
        };

        Loaded += (_, _) => { Refresh(); AppServices.WindowActivated += OnActivated; };
        Unloaded += (_, _) => AppServices.WindowActivated -= OnActivated;
    }

    private void OnActivated() => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        var found = ScsKiller.Find() is not null;
        _button.Content = found ? "Open SCSKiller" : "Get SCSKiller";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_button, (string)_button.Content);
        ToolTipService.SetToolTip(_button, found ? "Starts SCSKiller" : ScsKiller.ReleasesUrl);
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}
