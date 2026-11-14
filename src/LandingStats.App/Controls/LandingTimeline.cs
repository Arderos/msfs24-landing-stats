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
        LandingChart.NormalizeZoomRange(_record?.Series, ref startSeconds, ref endSeconds);
        _zoomStart = startSeconds;
        _zoomEnd = endSeconds;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        DrawSurface(context, RenderSize, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    internal DrawingGroup PrepareSurface(Size size, double pixelsPerDip)
    {
        VerifyAccess();
        if (size.IsEmpty || double.IsNaN(size.Width) || double.IsInfinity(size.Width) ||
            double.IsNaN(size.Height) || double.IsInfinity(size.Height) ||
            double.IsNaN(pixelsPerDip) || double.IsInfinity(pixelsPerDip) || pixelsPerDip <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) DrawSurface(context, size, pixelsPerDip);
        drawing.Freeze();
        return drawing;
    }

    private void DrawSurface(DrawingContext context, Size size, double pixelsPerDip)
    {
        var points = _record?.Series;
        if (points == null || points.Count < 2 || size.Width < 20)
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
            var x = fraction * size.Width;
            var last = tick + interval > maximum + 0.0001;
            var label = Math.Abs(tick) < 0.0001
                ? "0"
                : $"{tick:+0;-0;0}";
            if (last)
            {
                label += " " + LocalizationManager.Text("Unit.SecondSuffix");
            }

            var formatted = Formatted(label, Math.Abs(tick) < 0.0001 ? ContactBrush : TextBrush, pixelsPerDip);
            var left = fraction <= 0.001
                ? 0
                : last || fraction >= 0.999
                    ? size.Width - formatted.Width
                    : x - formatted.Width / 2.0;
            context.DrawText(formatted, new Point(Math.Max(0, left), 6));
        }
    }

    private static FormattedText Formatted(string text, Brush brush, double pixelsPerDip)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono"),
            10,
            brush,
            pixelsPerDip);
    }

    private static Brush FrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
