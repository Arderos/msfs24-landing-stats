using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Settings;

namespace LandingStats.App.RegressionTests;

internal static class AltitudeChartRegressionTests
{
    public static void RunAll()
    {
        var chart = new LandingChart
        {
            Mode = LandingChartMode.HeightAboveGround,
            CompactLane = true,
            ShowLegend = false,
            ShowXAxisLabels = false,
            Record = Record(500, 250, 20, -5, 0),
        };
        // This record deliberately has neither engine data nor runway geometry.
        // Its unrelated MSL altitude must not replace the recorded terrain AGL.
        Render(chart);
        var approach = HeightCurve(chart);
        Check(approach.Figures.Count == 1 && approach.Bounds.Height > 70 && approach.Bounds.Top >= 0 && approach.Bounds.Bottom <= 96,
            "AGL approach must fit the compact lane instead of retaining the old power scale.");
        var points = Vertices(approach).ToArray();
        Check(points.Length == 5 && points[3].Y > points[4].Y,
            "Recorded negative AGL must remain below zero rather than being clamped away.");

        chart.SetZoomRange(-1, 1);
        Render(chart);
        var zoomed = HeightCurve(chart);
        Check(Vertices(zoomed).Count() == 3 && Math.Abs(zoomed.Bounds.Width - 400) < .001 && zoomed.Bounds.Height > 70,
            "AGL must follow the selected time window and rescale the visible heights.");
        var beforeHover = Pixels(Render(chart));
        chart.SetHoverTime(0);
        Check(!beforeHover.SequenceEqual(Pixels(Render(chart))), "The altitude lane must display the synchronized hover value.");
        chart.SetHoverTime(null);
        Check(beforeHover.SequenceEqual(Pixels(Render(chart))), "Clearing shared hover must restore the altitude lane.");

        chart.SetZoomRange(null, null);
        chart.Record = Record(100, 80, double.NaN, 20, 0);
        Render(chart);
        Check(HeightCurve(chart).Figures.Count == 2, "Missing AGL must break the curve rather than bridge the missing measurement.");
        chart.Record = Record(double.NaN, double.PositiveInfinity, double.NaN);
        Render(chart);
        Check(!Drawings(VisualTreeHelper.GetDrawing(chart)).OfType<GeometryDrawing>().Any(IsHeightCurve),
            "Unavailable AGL must not become a zero-height curve.");
        chart.Record = Record(0, 0, 0);
        Render(chart);
        Check(HeightCurve(chart).Figures.Count == 1, "A recorded zero height is valid data.");
    }

    private static LandingRecord Record(params double[] heights) => new LandingRecord
    {
        Series = heights.Select((height, index) => new LandingSeriesPoint
        { TimeSeconds = index - 2, AglFeet = height, PlaneAltitudeFeet = 90000, GForce = 1 }).ToList(),
    };
    private static RenderTargetBitmap Render(LandingChart chart)
    {
        chart.Measure(new Size(400, 96)); chart.Arrange(new Rect(0, 0, 400, 96)); chart.UpdateLayout();
        var bitmap = new RenderTargetBitmap(400, 96, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(chart); return bitmap;
    }
    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }
    private static PathGeometry HeightCurve(LandingChart chart) =>
        PathGeometry.CreateFromGeometry(Drawings(VisualTreeHelper.GetDrawing(chart)).OfType<GeometryDrawing>().Single(IsHeightCurve).Geometry);
    private static bool IsHeightCurve(GeometryDrawing drawing) => drawing.Geometry is StreamGeometry &&
        drawing.Pen?.Brush is SolidColorBrush brush && brush.Color == Color.FromRgb(0x5f, 0xa8, 0xf5);
    private static IEnumerable<Drawing> Drawings(Drawing? drawing)
    {
        if (drawing == null) yield break;
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var nested in Drawings(child)) yield return nested;
    }
    private static IEnumerable<Point> Vertices(PathGeometry geometry)
    {
        foreach (var figure in geometry.Figures)
        {
            yield return figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                if (segment is LineSegment line) yield return line.Point;
                if (segment is PolyLineSegment polyline) foreach (var point in polyline.Points) yield return point;
            }
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
