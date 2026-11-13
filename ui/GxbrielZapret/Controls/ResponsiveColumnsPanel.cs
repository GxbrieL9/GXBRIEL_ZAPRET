using System.Windows;
using System.Windows.Controls;

namespace GxbrielZapret.Controls;

public sealed class ResponsiveColumnsPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(340d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double width && double.IsFinite(width) && width > 0);
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double gap && double.IsFinite(gap) && gap >= 0);

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    private UIElement[] VisibleChildren => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToArray();
    private int ColumnCount(double width, int count) => Math.Clamp((int)((width + Gap) / (MinColumnWidth + Gap)), 1, Math.Max(1, count));

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = VisibleChildren;
        if (children.Length == 0) return new Size();
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : children.Length * (MinColumnWidth + Gap) - Gap;
        int columns = ColumnCount(width, children.Length);
        double columnWidth = Math.Max(0, (width - (columns - 1) * Gap) / columns);
        foreach (var child in children) child.Measure(new Size(columnWidth, double.PositiveInfinity));
        double height = 0;
        for (int row = 0; row < children.Length; row += columns)
            height += children.Skip(row).Take(columns).Max(c => c.DesiredSize.Height) + (row == 0 ? 0 : Gap);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = VisibleChildren;
        int columns = ColumnCount(finalSize.Width, children.Length);
        double columnWidth = Math.Max(0, (finalSize.Width - (columns - 1) * Gap) / columns);
        double top = 0;
        for (int row = 0; row < children.Length; row += columns)
        {
            double height = children.Skip(row).Take(columns).Max(c => c.DesiredSize.Height);
            for (int column = 0; column < columns && row + column < children.Length; column++)
                children[row + column].Arrange(new Rect(column * (columnWidth + Gap), top, columnWidth, height));
            top += height + Gap;
        }
        return finalSize;
    }
}
