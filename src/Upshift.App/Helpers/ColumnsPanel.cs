using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Upshift.App.Helpers;

/// <summary>
/// Cards side by side: each child goes into the column set by <c>ColumnsPanel.Column</c>, stacked under the cards before
/// it in that column. When the panel is narrower than <see cref="Columns"/> × <see cref="MinColumnWidth"/>, fewer columns
/// are used, and with one column every child is stacked in document order (a narrow window reads top to bottom).
/// Collapsed children take no room.
/// </summary>
public sealed class ColumnsPanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int), typeof(ColumnsPanel),
        new PropertyMetadata(2, (d, _) => ((ColumnsPanel)d).InvalidateMeasure()));
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(ColumnsPanel),
        new PropertyMetadata(360.0, (d, _) => ((ColumnsPanel)d).InvalidateMeasure()));
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(ColumnsPanel),
        new PropertyMetadata(16.0, (d, _) => ((ColumnsPanel)d).InvalidateMeasure()));
    public static readonly DependencyProperty ColumnProperty = DependencyProperty.RegisterAttached("Column", typeof(int), typeof(ColumnsPanel),
        new PropertyMetadata(0, (d, _) => (VisualTreeHelperParent(d) as ColumnsPanel)?.InvalidateMeasure()));

    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public static int GetColumn(DependencyObject d) => (int)d.GetValue(ColumnProperty);
    public static void SetColumn(DependencyObject d, int value) => d.SetValue(ColumnProperty, value);

    private static DependencyObject? VisualTreeHelperParent(DependencyObject d) =>
        d is FrameworkElement fe ? fe.Parent : null;

    private int Effective(double width)
    {
        var n = Math.Max(1, Columns);
        if (double.IsInfinity(width)) return n;
        while (n > 1 && (width - Spacing * (n - 1)) / n < MinColumnWidth) n--;
        return n;
    }

    /// <summary>Which column each visible child goes in: its own (clamped) with several columns, 0 with one.</summary>
    private List<(UIElement Child, int Column)> Layout(int columns) =>
        Children.Where(c => c.Visibility == Visibility.Visible)
            .Select(c => (c, columns == 1 ? 0 : Math.Clamp(GetColumn(c), 0, columns - 1)))
            .ToList();

    protected override Size MeasureOverride(Size available)
    {
        var columns = Effective(available.Width);
        var width = double.IsInfinity(available.Width) ? MinColumnWidth * columns : available.Width;
        var colWidth = Math.Max(0, (width - Spacing * (columns - 1)) / columns);
        var heights = new double[columns];
        var counts = new int[columns];
        foreach (var child in Children.Where(c => c.Visibility != Visibility.Visible)) child.Measure(new Size(0, 0));
        foreach (var (child, column) in Layout(columns))
        {
            child.Measure(new Size(colWidth, double.PositiveInfinity));
            heights[column] += child.DesiredSize.Height + (counts[column]++ > 0 ? Spacing : 0);
        }
        return new Size(width, heights.DefaultIfEmpty(0).Max());
    }

    protected override Size ArrangeOverride(Size final)
    {
        var columns = Effective(final.Width);
        var colWidth = Math.Max(0, (final.Width - Spacing * (columns - 1)) / columns);
        var tops = new double[columns];
        foreach (var child in Children.Where(c => c.Visibility != Visibility.Visible)) child.Arrange(new Rect(0, 0, 0, 0));
        foreach (var (child, column) in Layout(columns))
        {
            var x = column * (colWidth + Spacing);
            child.Arrange(new Rect(x, tops[column], colWidth, child.DesiredSize.Height));
            tops[column] += child.DesiredSize.Height + Spacing;
        }
        return final;
    }
}
