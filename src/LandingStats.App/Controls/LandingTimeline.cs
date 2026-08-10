using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LandingStats.App.Models;
using LandingStats.App.Settings;

namespace LandingStats.App.Controls;

public sealed class LandingTimeline : FrameworkElement
{
    private static readonly Brush TextBrush = FrozenBrush("#8A837B");
    private static readonly Brush ContactBrush = FrozenBrush("#F5F3F0");
    private LandingRecord? _record;
    private double? _zoomStart;
    private double? _zoomEnd;

    public LandingRecord? Record
    {
        get => _record;
        set
        {
            _record = value;
            InvalidateVisual();
        }
    }

    public void SetZoomRange(double? startSeconds, double? endSeconds)
    {
        _zoomStart = startSeconds;
        _zoomEnd = endSeconds;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var points = _record?.Series;
        if (points == null || points.Count < 2 || ActualWidth < 20)
        {
            return;
        }

        var minimum = _zoomStart ?? points[0].TimeSeconds;
        var maximum = _zoomEnd ?? points[points.Count - 1].TimeSeconds;
        minimum = Math.Max(points[0].TimeSeconds, minimum);
        maximum = Math.Min(points[points.Count - 1].TimeSeconds, maximum);
        var span = Math.Max(0.01, maximum - minimum);
        var interval = span <= 16 ? 2.0 : span <= 35 ? 5.0 : 10.0;
        var first = Math.Ceiling(minimum / interval) * interval;
        for (var tick = first; tick <= maximum + 0.0001; tick += interval)
        {
            var fraction = (tick - minimum) / span;
            var x = fraction * ActualWidth;
            var last = tick + interval > maximum + 0.0001;
            var label = Math.Abs(tick) < 0.0001
                ? "0"
                : $"{tick:+0;-0;0}";
            if (last)
            {
                label += " " + LocalizationManager.Text("Unit.SecondSuffix");
            }

            var formatted = Formatted(label, Math.Abs(tick) < 0.0001 ? ContactBrush : TextBrush);
            var left = fraction <= 0.001
                ? 0
                : last || fraction >= 0.999
                    ? ActualWidth - formatted.Width
                    : x - formatted.Width / 2.0;
            context.DrawText(formatted, new Point(Math.Max(0, left), 6));
        }
    }

    private FormattedText Formatted(string text, Brush brush)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono"),
            10,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private static Brush FrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
