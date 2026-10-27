using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Settings;

namespace LandingStats.App.RegressionTests;

internal static class ReplayOverlayRegressionTests
{
    public static void RunAll()
    {
        LocalizationManager.Apply("en");
        var bounds = ReplayControlOverlay.StickBounds;
        var neutral = ReplayControlOverlay.StickPoint(0, 0);
        Near(neutral.X, bounds.Left + bounds.Width / 2); Near(neutral.Y, bounds.Top + bounds.Height / 2);
        Require(ReplayControlOverlay.StickPoint(50, 0).Y > neutral.Y, "Pull moves the dot down.");
        Require(ReplayControlOverlay.StickPoint(-50, 0).Y < neutral.Y, "Push moves the dot up.");
        Require(ReplayControlOverlay.StickPoint(0, -50).X < neutral.X && ReplayControlOverlay.StickPoint(0, 50).X > neutral.X,
            "Roll follows pilot left/right.");
        Near(ReplayControlOverlay.StickPoint(1000, -1000).Y, bounds.Bottom);
        Near(ReplayControlOverlay.StickPoint(1000, -1000).X, bounds.Left);

        var control = new ReplayControlOverlay();
        Set(control, "_sample", new ReplayControlSample(50, -25, -60));
        var drawings = Draw(control);
        Require(Text(drawings).Contains("SIDESTICK"), "English control label is Sidestick.");
        var dot = Colored(drawings, ReplayOverlayStyle.Orange).Select(d => d.Geometry).OfType<EllipseGeometry>().Single();
        Near(dot.Center.X, ReplayControlOverlay.StickPoint(50, -25).X); Near(dot.Center.Y, ReplayControlOverlay.StickPoint(50, -25).Y);
        var fill = Colored(drawings, ReplayOverlayStyle.Purple).Select(d => d.Geometry).OfType<RectangleGeometry>().Single();
        Near(fill.Rect.Right, neutral.X); Require(fill.Rect.Left < neutral.X, "Left pedal fill ends at centre and grows left.");
        Set(control, "_sample", new ReplayControlSample(0, 0, 60));
        drawings = Draw(control);
        dot = Colored(drawings, ReplayOverlayStyle.Orange).Select(d => d.Geometry).OfType<EllipseGeometry>().Single();
        Near(dot.Center.X, neutral.X); Near(dot.Center.Y, neutral.Y);
        fill = Colored(drawings, ReplayOverlayStyle.Purple).Select(d => d.Geometry).OfType<RectangleGeometry>().Single();
        Near(fill.Rect.Left, neutral.X); Require(fill.Rect.Right > neutral.X, "Right pedal fill starts at centre and grows right.");
        Set(control, "_sample", new ReplayControlSample(20, null, null));
        drawings = Draw(control);
        dot = Colored(drawings, ReplayOverlayStyle.Orange).Select(d => d.Geometry).OfType<EllipseGeometry>().Single();
        Near(dot.Center.X, neutral.X); Near(dot.Center.Y, ReplayControlOverlay.StickPoint(20, 0).Y);
        Require(Text(drawings).Contains("Roll not recorded"), "Pitch-only dot clearly labels the missing roll axis.");
        Require(!((ReplayControlSample)Get(control, "_sample")).RollPercent.HasValue, "Centre-guide display never fills in the missing raw measurement.");
        Set(control, "_sample", new ReplayControlSample(null, -40, null));
        drawings = Draw(control);
        dot = Colored(drawings, ReplayOverlayStyle.Orange).Select(d => d.Geometry).OfType<EllipseGeometry>().Single();
        Near(dot.Center.X, ReplayControlOverlay.StickPoint(0, -40).X); Near(dot.Center.Y, neutral.Y);
        Require(Text(drawings).Contains("Pitch not recorded"), "Roll-only dot clearly labels the missing pitch axis.");
        Require(!((ReplayControlSample)Get(control, "_sample")).PitchPercent.HasValue, "Partial input remains nullable.");
        control.Update(0, false);
        drawings = Draw(control);
        Require(!Colored(drawings, ReplayOverlayStyle.Orange).Any() && !Colored(drawings, ReplayOverlayStyle.Purple).Any(),
            "Missing pose clears physical stick and pedal data.");

        var record = Record();
        Require(LandingReplayTrack.TryCreate(record, out var track), "Overlay fixture track.");
        var metrics = new ReplayMetricsOverlay(); metrics.SetTrack(track, 358);
        for (var i = 0; i < 6; i++)
        {
            Near(ReplayMetricsOverlay.Card(i).X, i < 3 ? 0 : 176);
            Near(ReplayMetricsOverlay.Card(i).Y, i % 3 * 76);
        }
        var altitudeCard = ReplayMetricsOverlay.Card(6);
        Near(altitudeCard.Left, 0); Near(altitudeCard.Top, 228); Near(altitudeCard.Width, metrics.Width);
        Near(altitudeCard.Bottom, metrics.Height);
        Require(((string[])Get(metrics, "_labels")).SequenceEqual(new[] { "Replay.Pitch", "Replay.Bank", "Replay.Yaw",
                "Replay.GroundSpeed", "Replay.VerticalSpeed", "Replay.VerticalG", "Chart.HeightAboveGround" }),
            "Pitch/bank/yaw and GS/VS/vertical G retain their positions, with terrain AGL spanning the row below.");
        var paths = ((Geometry?[])Get(metrics, "_paths")).ToArray();
        Require(paths.All(p => p != null && p.IsFrozen), "Mini-chart geometry is prepared once and frozen.");
        Require(paths.All(p => PathGeometry.CreateFromGeometry(p!).Figures.Count == 2), "Mini charts never connect samples across the telemetry gap.");
        Require(track!.TrySample(0, out var pose), "Overlay fixture contact pose.");
        metrics.Update(0, pose);
        drawings = Draw(metrics);
        var curveImages = drawings.OfType<ImageDrawing>().Distinct().ToArray();
        Require(curveImages.Length == 7, "Each complete mini-chart curve is rasterized once, including its future portion.");
        foreach (var image in curveImages)
        {
            var bitmap = (BitmapSource)image.ImageSource;
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var card = ReplayMetricsOverlay.Card(Array.IndexOf(curveImages, image));
            var gapX = card.X + 10 + (.65 + .2) / 1.4 * (card.Width - 20);
            var column = (int)((gapX - image.Rect.Left) * bitmap.DpiX / 96);
            Require(Enumerable.Range(0, bitmap.PixelHeight).All(y => pixels[(y * bitmap.PixelWidth + column) * 4 + 3] == 0),
                "Rasterized curves preserve the transparent telemetry gap without bridging it.");
        }
        var text = Text(drawings);
        Require(text.Contains((3.5).ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture)) &&
                text.Contains((1.25).ToString("0.00", CultureInfo.CurrentCulture)) && text.Contains("123") && text.Contains("-321 fpm") &&
                text.Contains((2d).ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + "°") && text.Contains(LocalizationManager.Format("Chart.HeightFeetFormat", 37.25)),
            "Numeric mini-chart readouts use recorded pose values and the active culture.");
        metrics.InvalidateVisual();
        Require(Draw(metrics).OfType<ImageDrawing>().Distinct().SequenceEqual(curveImages),
            "A scene redraw at the same replay time reuses every cached curve, including terrain AGL.");
        metrics.Update(.65, null);
        drawings = Draw(metrics);
        Require(drawings.OfType<ImageDrawing>().Distinct().SequenceEqual(curveImages),
            "Seeking changes cursor/readouts while reusing the rasterized curves.");
        Require(Text(drawings).Count(c => c == '—') == 7, "Each metric clears its current numeric value inside a telemetry gap.");
        Require(((Geometry?[])Get(metrics, "_paths")).Select((path, i) => ReferenceEquals(path, paths[i])).All(value => value),
            "Playback updates reuse chart geometry, including gap frames.");
        var setDpi = typeof(VisualTreeHelper).GetMethod("SetRootDpi", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        setDpi.Invoke(null, new object[] { metrics, new DpiScale(1.5, 1.5) }); metrics.InvalidateVisual();
        var highDpiImages = Draw(metrics).OfType<ImageDrawing>().Distinct().ToArray();
        Require(highDpiImages.Length == 7 && highDpiImages.All(image => ((BitmapSource)image.ImageSource).DpiX == 144) &&
                !highDpiImages.Any(curveImages.Contains), "Moving to 150% DPI regenerates sharp curves at native pixel resolution.");
        setDpi.Invoke(null, new object[] { metrics, new DpiScale(1, 1) }); metrics.InvalidateVisual();
        metrics.SetTrack(track); metrics.Update(0, pose);
        Require(Text(Draw(metrics)).Count(c => c == '—') == 1 && ((Geometry?[])Get(metrics, "_paths"))[2] == null,
            "Missing runway direction leaves yaw unavailable, not a heading or fabricated zero.");
        Require(Draw(metrics).OfType<ImageDrawing>().Distinct().Count() == 6 &&
                !Draw(metrics).OfType<ImageDrawing>().Any(curveImages.Contains),
            "Changing the track replaces cached curves and removes an unavailable yaw curve.");
        var wrap = Record(); wrap.Series.RemoveAll(p => p.TimeSeconds > -.1);
        wrap.Series[0].HeadingTrueDegrees = 359; wrap.Series[1].HeadingTrueDegrees = 1;
        Require(LandingReplayTrack.TryCreate(wrap, out var wrapTrack) && wrapTrack!.TrySample(-.15, out _), "Yaw wrap fixture.");
        wrapTrack!.TrySample(-.15, out var wrapPose); metrics.SetTrack(wrapTrack, 0); metrics.Update(-.15, wrapPose);
        var yawDot = Colored(Draw(metrics), ReplayOverlayStyle.Purple).Select(d => d.Geometry).OfType<EllipseGeometry>().Single();
        Near(yawDot.Center.Y, ReplayMetricsOverlay.Card(2).Y + 29 + 14.5);
        Require(PathGeometry.CreateFromGeometry(((Geometry?[])Get(metrics, "_paths"))[2]!).Figures.Count == 1,
            "Crossing north is continuous relative to the runway, without a 360-degree spike.");

        HeightAboveGroundMissingSamples();
        HeightAboveGroundRepairsInvalidatePresentation();

        var runway = new ReplayRunwayOverlay(); runway.Update(-7.5, 45);
        Require(Text(Draw(runway)).Contains("←"), "Negative runway distance is labelled left.");
        runway.Update(7.5, 45); Require(Text(Draw(runway)).Contains("→"), "Positive runway distance is labelled right.");
        runway.Update(null, 45); Require(Text(Draw(runway)).Contains("—"), "Missing runway offset is not shown as zero.");

        var view = new LandingReplayView { Width = 900, Height = 450, Record = record };
        view.SetTime(.65);
        var viewMetrics = (ReplayMetricsOverlay)Get(view, "_metrics");
        var viewControls = (ReplayControlOverlay)Get(view, "_controls");
        Require(GetNullable(viewMetrics, "_pose") == null && !((ReplayControlSample)Get(viewControls, "_sample")).HasStick,
            "Integrated replay clears overlays, not just the aircraft, inside a telemetry gap.");
        view.Record = new LandingRecord();
        Require(viewMetrics.Visibility == Visibility.Collapsed && viewControls.Visibility == Visibility.Collapsed,
            "Switching to legacy/no-position history hides all new overlays.");
        LocalizationManager.Apply("ru"); control.RefreshLocalization(); runway.RefreshLocalization(); metrics.InvalidateVisual();
        Require(Text(Draw(metrics)).Contains("Высота AGL") && Text(Draw(metrics)).Contains("фт"),
            "Russian terrain height uses the same localized title and feet formatter as the 2D chart.");
        Set(control, "_sample", new ReplayControlSample(20, null, null));
        Require(Text(Draw(control)).Contains("Крен не записан"), "Russian pitch-only label explicitly names missing roll.");
        Require(Text(Draw(control)).Contains("САЙДСТИК"), "Russian control label is Sidestick.");
        Require(!Text(Draw(control)).Contains("Replay.") && !Text(Draw(runway)).Contains("Replay."), "Russian overlay keys are available.");
        LocalizationManager.Apply("en");
    }

    private static void HeightAboveGroundMissingSamples()
    {
        var record = Record();
        var heights = new[] { 10d, 5, double.NaN, -2, -3, double.PositiveInfinity, double.NegativeInfinity };
        for (var i = 0; i < heights.Length; i++) record.Series[i].AglFeet = heights[i];
        Require(LandingReplayTrack.TryCreate(record, out var track), "Missing AGL keeps the valid recorded 3D poses.");
        var metrics = new ReplayMetricsOverlay(); metrics.SetTrack(track, 358);
        var path = ((Geometry?[])Get(metrics, "_paths"))[6];
        Require(path != null && PathGeometry.CreateFromGeometry(path).Figures.Count == 2,
            "Non-finite terrain height breaks the AGL curve while retaining finite samples on either side.");
        Require(track!.TrySample(0, out var missingPose), "A 3D pose remains available where AGL is missing.");
        metrics.Update(0, missingPose);
        var drawings = Draw(metrics);
        var altitudeCard = ReplayMetricsOverlay.Card(6);
        Require(Text(drawings).Count(c => c == '—') == 1 && !Colored(drawings, ReplayOverlayStyle.Blue)
                .Select(d => d.Geometry).OfType<EllipseGeometry>().Any(dot => altitudeCard.Contains(dot.Center)),
            "Missing AGL clears its readout and dot without hiding the other recorded metrics.");
        var altitudeImage = ((ImageDrawing?[])Get(metrics, "_chartImages"))[6]!;
        var bitmap = (BitmapSource)altitudeImage.ImageSource;
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var gapX = altitudeCard.Left + 10 + (0 - track.StartSeconds) / (track.EndSeconds - track.StartSeconds) * (altitudeCard.Width - 20);
        var column = (int)((gapX - altitudeImage.Rect.Left) * bitmap.DpiX / 96);
        Require(Enumerable.Range(0, bitmap.PixelHeight).All(y => pixels[(y * bitmap.PixelWidth + column) * 4 + 3] == 0),
            "The cached AGL image leaves a transparent gap instead of joining across a non-finite sample.");
        Require(track.TrySample(-.15, out var validPose), "Finite AGL is interpolated on the shared replay timeline.");
        metrics.Update(-.15, validPose);
        Require(Text(Draw(metrics)).Contains(LocalizationManager.Format("Chart.HeightFeetFormat", 7.5)),
            "Terrain height comes from recorded AGL, independently of the pose's MSL and terrain elevation.");
        Require(track.TrySample(.15, out validPose), "Negative terrain heights remain finite recorded values.");
        metrics.Update(.15, validPose);
        drawings = Draw(metrics);
        Require(Text(drawings).Contains(LocalizationManager.Format("Chart.HeightFeetFormat", -2.5)) &&
                Colored(drawings, ReplayOverlayStyle.Blue).Select(d => d.Geometry).OfType<EllipseGeometry>()
                    .Any(dot => altitudeCard.Contains(dot.Center)), "Negative AGL is displayed and fits inside its plot.");
        Require(ReferenceEquals(altitudeImage, ((ImageDrawing?[])Get(metrics, "_chartImages"))[6]),
            "Seeking between missing, positive and negative AGL keeps the same cached curve.");

        foreach (var height in new[] { 0d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            foreach (var point in record.Series) point.AglFeet = height;
            Require(LandingReplayTrack.TryCreate(record, out var changedTrack) && changedTrack!.TrySample(0, out _), "Changed AGL fixture.");
            changedTrack!.TrySample(0, out var changedPose); metrics.SetTrack(changedTrack, 358); metrics.Update(0, changedPose);
            drawings = Draw(metrics);
            var changedImage = ((ImageDrawing?[])Get(metrics, "_chartImages"))[6];
            if (height == 0)
            {
                Require(changedImage != null && !ReferenceEquals(altitudeImage, changedImage) &&
                        Text(drawings).Contains(LocalizationManager.Format("Chart.HeightFeetFormat", 0)),
                    "A new track replaces the AGL bitmap and valid zero height remains visible.");
            }
            else
            {
                Require(changedImage == null && ((Geometry?[])Get(metrics, "_paths"))[6] == null &&
                        drawings.OfType<ImageDrawing>().Distinct().Count() == 6 && Text(drawings).Count(c => c == '—') == 1,
                    "An entirely non-finite AGL channel has no curve or fabricated value; other metrics remain available.");
            }
        }
        metrics.SetTrack(null); metrics.Update(0, null);
        Require(!Draw(metrics).OfType<ImageDrawing>().Any(), "Clearing a track removes every cached curve including AGL.");
    }

    private static void HeightAboveGroundRepairsInvalidatePresentation()
    {
        var record = Record();
        var prepared = ReplayPresentationData.Prepare(record, new[] { record });
        Require(ReferenceEquals(prepared, ReplayPresentationData.Prepare(record, new[] { record })),
            "Unchanged replay presentation is reused.");
        var metrics = new ReplayMetricsOverlay(); metrics.SetTrack(prepared.Track, 358);
        Require(prepared.Track!.TrySample(0, out var pose), "Prepared terrain height fixture.");
        metrics.Update(0, pose); Draw(metrics);
        var initialImage = ((ImageDrawing?[])Get(metrics, "_chartImages"))[6];

        record.Series[2].AglFeet = 80;
        var repaired = ReplayPresentationData.Prepare(record, new[] { record });
        Require(!ReferenceEquals(prepared, repaired) && !ReferenceEquals(prepared.Track, repaired.Track),
            "Repairing AGL in place invalidates the prepared replay and its track.");
        metrics.SetTrack(repaired.Track, 358);
        Require(repaired.Track!.TrySample(0, out pose), "Repaired terrain height fixture.");
        metrics.Update(0, pose);
        Require(Text(Draw(metrics)).Contains(LocalizationManager.Format("Chart.HeightFeetFormat", 80)) &&
                !ReferenceEquals(initialImage, ((ImageDrawing?[])Get(metrics, "_chartImages"))[6]),
            "The rebuilt replay displays repaired AGL in both the current readout and a new curve bitmap.");
        Require(ReferenceEquals(repaired, ReplayPresentationData.Prepare(record, new[] { record })),
            "The repaired presentation is then reused until its data changes again.");
    }

    private static LandingRecord Record()
    {
        var record = new LandingRecord();
        foreach (var time in new[] { -.2, -.1, 0, .1, .2, 1.1, 1.2 })
            record.Series.Add(new LandingSeriesPoint { TimeSeconds = time, LatitudeDegrees = 42 + time * .001, LongitudeDegrees = 23,
                PlaneAltitudeFeet = 1000, GroundAltitudeFeet = 990, HeadingTrueDegrees = 0,
                PitchDegrees = -3.5, BankDegrees = -1.5, GroundSpeedKnots = 123, InertialFpm = 321, GForce = 1.25, AglFeet = 37.25 - time * 10 });
        return record;
    }

    private static List<Drawing> Draw(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height)); element.Arrange(new Rect(0, 0, element.Width, element.Height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)element.Width, (int)element.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var result = new List<Drawing>();
        void Visit(Drawing drawing) { if (drawing is DrawingGroup group) foreach (var child in group.Children) Visit(child); else result.Add(drawing); }
        Visit(VisualTreeHelper.GetDrawing(element)); return result;
    }
    private static string Text(IEnumerable<Drawing> drawings) => string.Concat(drawings.OfType<GlyphRunDrawing>().Select(d => d.GlyphRun.Characters == null ? "" : new string(d.GlyphRun.Characters.ToArray())));
    private static IEnumerable<GeometryDrawing> Colored(IEnumerable<Drawing> drawings, Brush color) => drawings.OfType<GeometryDrawing>().Where(d => Same(d.Brush, color));
    private static bool Same(Brush? a, Brush b) => a is SolidColorBrush first && b is SolidColorBrush second && first.Color == second.Color;
    private static object Get(object instance, string name) => GetNullable(instance, name)!;
    private static object? GetNullable(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static void Set(object instance, string name, object value)
    {
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
        if (instance is FrameworkElement element) element.InvalidateVisual();
    }
    private static void Near(double actual, double expected) => Require(Math.Abs(actual - expected) < .000001, $"Expected {expected}, got {actual}.");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
