using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Upshift.App.Services;

namespace Upshift.App.Views;

/// <summary>
/// A Performance tab line about a companion tool (Tool = "SCSKiller" or "CapFrameX"): "Get …" opens its releases page
/// when it isn't found, "Open …" only starts it when it is. Looked for again whenever the window comes back. Upshift
/// never reads, changes, bundles or downloads these tools.
/// </summary>
public sealed class CompanionToolView : UserControl
{
    private readonly Button _button = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _problem = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    /// <summary>"SCSKiller" or "CapFrameX".</summary>
    public string Tool { get; set; } = "SCSKiller";

    private bool IsCapFrameX => Tool.Equals("CapFrameX", StringComparison.OrdinalIgnoreCase);
    private string Name2 => IsCapFrameX ? "CapFrameX" : "SCSKiller";
    private string? Find() => IsCapFrameX ? CapFrameX.Find() : ScsKiller.Find();
    private string? Open() => IsCapFrameX ? CapFrameX.Open() : ScsKiller.Open();
    private string ReleasesUrl => IsCapFrameX ? CapFrameX.ReleasesUrl : ScsKiller.ReleasesUrl;

    public CompanionToolView()
    {
        _button.Click += async (_, _) =>
        {
            if (Find() is null)
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(ReleasesUrl));
                return;
            }
            var problem = Open();
            _problem.Text = problem ?? "";
            _problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        };

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_text);
        Grid.SetColumn(_button, 1);
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
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, Name2);
        _text.Text = IsCapFrameX
            ? "Want deeper frame-time analysis? Use CapFrameX (free, open source)."
            : "Shader stutter on first play? Use SCSKiller (free, open source).";
        var found = Find() is not null;
        _button.Content = (found ? "Open " : "Get ") + Name2;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_button, (string)_button.Content);
        ToolTipService.SetToolTip(_button, found ? $"Starts {Name2}" : ReleasesUrl);
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}
