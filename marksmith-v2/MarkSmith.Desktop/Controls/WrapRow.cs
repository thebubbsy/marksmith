using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MarkSmith.Controls;

/// <summary>
/// Lays its children out left to right and starts a new line when the next one would not fit,
/// so a toolbar wider than its pane wraps instead of clipping its right end. Collapsed children
/// take no space and no spacing. Each line is as tall as its tallest child; children are
/// centred vertically within their line.
/// </summary>
public sealed class WrapRow : Panel
{
    public double HorizontalSpacing { get; set; } = 8;
    public double VerticalSpacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxWidth = availableSize.Width;
        double lineWidth = 0, lineHeight = 0, totalHeight = 0, widest = 0;
        bool lineHasItems = false;

        foreach (var child in Children)
        {
            child.Measure(new Size(maxWidth, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (child.Visibility == Visibility.Collapsed || (size.Width <= 0 && size.Height <= 0)) continue;

            double needed = lineHasItems ? lineWidth + HorizontalSpacing + size.Width : size.Width;
            if (lineHasItems && needed > maxWidth)
            {
                widest = Math.Max(widest, lineWidth);
                totalHeight += lineHeight + VerticalSpacing;
                lineWidth = size.Width;
                lineHeight = size.Height;
            }
            else
            {
                lineWidth = needed;
                lineHeight = Math.Max(lineHeight, size.Height);
            }
            lineHasItems = true;
        }

        if (!lineHasItems) return new Size(0, 0);
        widest = Math.Max(widest, lineWidth);
        totalHeight += lineHeight;
        return new Size(double.IsInfinity(maxWidth) ? widest : Math.Min(widest, maxWidth), totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        int lineStart = 0;
        bool lineHasItems = false;

        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var size = child.DesiredSize;
            if (child.Visibility == Visibility.Collapsed || (size.Width <= 0 && size.Height <= 0))
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            if (lineHasItems && x + HorizontalSpacing + size.Width > finalSize.Width)
            {
                ArrangeLine(lineStart, i, y, lineHeight);
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
                lineStart = i;
                lineHasItems = false;
            }

            x += (lineHasItems ? HorizontalSpacing : 0) + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
            lineHasItems = true;
        }
        if (lineHasItems) ArrangeLine(lineStart, Children.Count, y, lineHeight);
        return finalSize;
    }

    private void ArrangeLine(int start, int end, double y, double lineHeight)
    {
        double x = 0;
        bool first = true;
        for (int i = start; i < end; i++)
        {
            var child = Children[i];
            var size = child.DesiredSize;
            if (child.Visibility == Visibility.Collapsed || (size.Width <= 0 && size.Height <= 0)) continue;
            if (!first) x += HorizontalSpacing;
            child.Arrange(new Rect(x, y + (lineHeight - size.Height) / 2, size.Width, size.Height));
            x += size.Width;
            first = false;
        }
    }
}
