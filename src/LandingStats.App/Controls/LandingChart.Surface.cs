using System;
using System.Windows;
using System.Windows.Media;
using LandingStats.App.Settings;

namespace LandingStats.App.Controls;

// The chart owns scales, nearest-sample selection and drawing semantics. A
// presentation backend consumes this immutable vector frame without placing
// the chart in a displayed WPF visual tree.
internal sealed class LandingChartFrame
{
    internal LandingChartFrame(DrawingGroup drawing, Rect plot, double minimumTime,
        double maximumTime, double minimumValue, double maximumValue, bool hasData)
    {
        Drawing = drawing; Plot = plot; MinimumTime = minimumTime; MaximumTime = maximumTime;
        MinimumValue = minimumValue; MaximumValue = maximumValue; HasData = hasData;
    }
    internal DrawingGroup Drawing { get; }
    internal Rect Plot { get; }
    internal double MinimumTime { get; }
    internal double MaximumTime { get; }
    internal double MinimumValue { get; }
    internal double MaximumValue { get; }
    internal bool HasData { get; }
    internal double TimeAt(double x)
    {
        if (!HasData) throw new InvalidOperationException("An empty chart has no time mapping.");
        var fraction = Math.Max(0, Math.Min(1, (x - Plot.Left) / Plot.Width));
        return MinimumTime + fraction * (MaximumTime - MinimumTime);
    }
}

public sealed partial class LandingChart
{
    internal void RefreshSurfaceLocalization() => InvalidateChart();

    internal LandingChartFrame PrepareSurface(Size size, double pixelsPerDip)
    {
        VerifyAccess();
        if (size.IsEmpty || !Finite(size.Width) || !Finite(size.Height) ||
            !Finite(pixelsPerDip) || pixelsPerDip <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        var language = LocalizationManager.Text("Chart.NoData");
        if (_staticDrawing == null || _drawingSize != size || _drawingLanguage != language || _drawingDpi != pixelsPerDip)
        {
            _drawingSize = size; _drawingLanguage = language; _drawingDpi = pixelsPerDip;
            // Empty frames must not retain a prior record's plot or scales.
            _drawingPlot = Rect.Empty;
            _drawingMinTime = _drawingMaxTime = _drawingMinValue = _drawingMaxValue = 0;
            _staticDrawing = new DrawingGroup();
            using (var drawing = _staticDrawing.Open()) BuildStaticDrawing(drawing);
            _staticDrawing.Freeze();
        }
        return new LandingChartFrame(_staticDrawing, _drawingPlot, _drawingMinTime,
            _drawingMaxTime, _drawingMinValue, _drawingMaxValue, _drawingHasData);
    }

    internal int NearestSurfaceIndex(double time)
    {
        var points = _record?.Series;
        return points == null || points.Count == 0 || !Finite(time)
            ? -1 : ClosestIndex(points.Count, index => points[index].TimeSeconds, time);
    }

    internal DrawingGroup DrawSurfaceHover(LandingChartFrame frame, int index)
    {
        VerifySurface(frame);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            var points = _record?.Series;
            if (frame.HasData && points != null && index >= 0 && index < points.Count)
            {
                context.PushClip(new RectangleGeometry(frame.Plot));
                DrawHover(context, points[index], frame.Plot, frame.MinimumTime,
                    frame.MaximumTime, frame.MinimumValue, frame.MaximumValue);
                context.Pop();
            }
        }
        drawing.Freeze();
        return drawing;
    }

    internal DrawingGroup DrawSurfaceSelection(LandingChartFrame frame, double startX, double currentX)
    {
        VerifySurface(frame);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            if (frame.HasData)
            {
                context.PushClip(new RectangleGeometry(frame.Plot));
                DrawSelection(context, frame.Plot, new Point(startX, 0), new Point(currentX, 0));
                context.Pop();
            }
        }
        drawing.Freeze();
        return drawing;
    }

    private void VerifySurface(LandingChartFrame frame)
    {
        VerifyAccess();
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (!ReferenceEquals(frame.Drawing, _staticDrawing))
            throw new InvalidOperationException("Chart data or options changed; prepare a current frame before drawing hover.");
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
