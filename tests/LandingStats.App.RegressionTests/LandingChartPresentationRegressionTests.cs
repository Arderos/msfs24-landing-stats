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

internal static class LandingChartPresentationRegressionTests
{
    public static void RunAll()
    {
        LocalizationManager.Apply("en");
        try
        {
            EveryModeBuildsAnImmutableOffscreenFrame();
            HoverAndSelectionPreserveTheStaticFrame();
            ChangedInputsInvalidateFrames();
            SparseZoomAndNearestSampleKeepTheirTimeMapping();
            EmptyFramesDiscardPreviousData();
            SceneSynchronizesHoverWithoutRebuildingStaticPixels();
            SelectionCancellationAndZoomPreserveLaneSynchronization();
            SceneHandlesDpiReplayAndRecordReplacement();
            ModalSnapshotPreservesPendingNativeChanges();
            ModalSnapshotReflectsCurrentContentAndDpi();
        }
        finally { LocalizationManager.Apply("en"); }
    }

    private static void EveryModeBuildsAnImmutableOffscreenFrame()
    {
        var chart = new LandingChart { Record = Record() };
        foreach (LandingChartMode mode in Enum.GetValues(typeof(LandingChartMode)))
        {
            chart.Mode = mode;
            var frame = chart.PrepareSurface(new Size(640, 320), 1);
            Check(frame.HasData && frame.Drawing.IsFrozen && !frame.Plot.IsEmpty,
                mode + " did not produce an immutable frame without a displayed WPF visual.");
            Check(frame.MinimumTime == -3 && frame.MaximumTime == 2,
                mode + " changed the source time range.");
            Check(frame.Plot.Left >= 0 && frame.Plot.Right <= 640 && frame.Plot.Top >= 0 && frame.Plot.Bottom <= 320,
                mode + " placed its plot outside the requested surface.");
            Check(Drawings(frame.Drawing).OfType<GeometryDrawing>().Any(drawing => drawing.Geometry is StreamGeometry),
                mode + " omitted the recorded curves.");
            var hover = chart.DrawSurfaceHover(frame, 2);
            Check(hover.IsFrozen && hover.Children.Count > 0, mode + " omitted the nearest sample hover.");
        }
    }

    private static void HoverAndSelectionPreserveTheStaticFrame()
    {
        var chart = new LandingChart { Record = Record() };
        var frame = chart.PrepareSurface(new Size(640, 320), 1);
        var bounds = frame.Drawing.Bounds;
        var childCount = frame.Drawing.Children.Count;
        chart.SetHoverTime(.14);
        chart.DrawSurfaceHover(frame, chart.NearestSurfaceIndex(.14));
        var selection = chart.DrawSurfaceSelection(frame, frame.Plot.Left + 10, frame.Plot.Right - 10);
        Check(selection.IsFrozen && selection.Children.Count > 0, "A valid drag did not produce a selection overlay.");
        chart.SetHoverTime(null);
        Check(chart.DrawSurfaceHover(frame, -1).Children.Count == 0 &&
              chart.DrawSurfaceHover(frame, chart.Record!.Series.Count).Children.Count == 0,
            "An absent or out-of-range hover sample retained an overlay.");
        Check(ReferenceEquals(frame.Drawing, chart.PrepareSurface(new Size(640, 320), 1).Drawing) &&
              frame.Drawing.Bounds == bounds && frame.Drawing.Children.Count == childCount,
            "Hover or drag rebuilt or modified the static drawing.");
    }

    private static void ChangedInputsInvalidateFrames()
    {
        var chart = new LandingChart { Record = Record() };
        var size = new Size(640, 320);
        var frame = chart.PrepareSurface(size, 1);
        foreach (var change in new Action[]
        {
            () => chart.Mode = LandingChartMode.Power,
            () => chart.SetIsolatedSeries(0),
            () => chart.SetZoomRange(-1, 1),
            () => chart.Record = Record(),
            () => chart.ShowLegend = false,
            () => chart.RefreshSurfaceLocalization(),
        })
        {
            change();
            var previous = frame;
            Throws<InvalidOperationException>(() => chart.DrawSurfaceHover(previous, 0),
                "A changed record or option accepted stale hover scales.");
            frame = chart.PrepareSurface(size, 1);
            Check(!ReferenceEquals(previous.Drawing, frame.Drawing), "Changed chart input reused a stale static drawing.");
        }

        var resized = chart.PrepareSurface(new Size(800, 400), 1);
        Check(!ReferenceEquals(frame.Drawing, resized.Drawing) && resized.Plot.Right == 800,
            "An offscreen resize retained the old plot geometry.");
        var higherDpi = chart.PrepareSurface(new Size(800, 400), 1.5);
        Check(!ReferenceEquals(resized.Drawing, higherDpi.Drawing) && higherDpi.Plot == resized.Plot,
            "A DPI change must refresh text while preserving DIP coordinates.");
        Throws<InvalidOperationException>(() => chart.DrawSurfaceHover(resized, 0),
            "A DPI change left the previous frame usable.");
        Throws<ArgumentOutOfRangeException>(() => chart.PrepareSurface(size, double.NaN),
            "An invalid DPI entered the drawing cache.");
    }

    private static void SparseZoomAndNearestSampleKeepTheirTimeMapping()
    {
        var chart = new LandingChart { Record = Record() };
        Check(chart.NearestSurfaceIndex(-100) == 0 && chart.NearestSurfaceIndex(100) == 4 &&
              chart.NearestSurfaceIndex(-.4) == 2 && chart.NearestSurfaceIndex(.14) == 3 &&
              chart.NearestSurfaceIndex(double.NaN) == -1,
            "Nearest hover selection changed for irregularly spaced samples or out-of-range input.");
        chart.SetZoomRange(-.8, -.6);
        var frame = chart.PrepareSurface(new Size(640, 320), 1);
        Check(frame.HasData && Close(frame.MinimumTime, -.8) && Close(frame.MaximumTime, -.6),
            "A zoom between recorded samples reverted to the full record.");
        Check(Close(frame.TimeAt(frame.Plot.Left - 10), -.8) && Close(frame.TimeAt(frame.Plot.Right + 10), -.6) &&
              Close(frame.TimeAt(frame.Plot.Left + frame.Plot.Width / 2), -.7),
            "Zoomed pointer coordinates no longer match the displayed time scale.");
        chart.SetZoomRange(50, 60);
        var reset = chart.PrepareSurface(new Size(640, 320), 1);
        Check(reset.MinimumTime == -3 && reset.MaximumTime == 2, "An out-of-range zoom retained stale bounds.");
        chart.SetZoomRange(1, -1);
        var reversed = chart.PrepareSurface(new Size(640, 320), 1);
        Check(reversed.MinimumTime == -1 && reversed.MaximumTime == 1, "A reverse-direction drag changed the normalized range.");
    }

    private static void EmptyFramesDiscardPreviousData()
    {
        var chart = new LandingChart { Record = Record() };
        chart.PrepareSurface(new Size(640, 320), 1);
        foreach (var record in new LandingRecord?[]
        {
            null,
            new LandingRecord(),
            new LandingRecord { Series = new List<LandingSeriesPoint> { new LandingSeriesPoint { TimeSeconds = 12 } } },
        })
        {
            chart.Record = record;
            var frame = chart.PrepareSurface(new Size(640, 320), 1);
            Check(!frame.HasData && frame.Plot.IsEmpty && frame.Drawing.IsFrozen,
                "An empty or single-point record retained the previous chart geometry.");
            Check(chart.DrawSurfaceHover(frame, 0).Children.Count == 0 &&
                  chart.DrawSurfaceSelection(frame, 120, 400).Children.Count == 0,
                "A frame without a time range retained interactive overlays.");
            Throws<InvalidOperationException>(() => frame.TimeAt(200), "An empty frame exposed a stale time mapping.");
        }
        chart.Record = Record();
        Check(chart.PrepareSurface(new Size(640, 320), 1).HasData,
            "Loading a populated record after an empty record did not restore the chart.");
    }

    private static void SceneSynchronizesHoverWithoutRebuildingStaticPixels()
    {
        using var scene = Scene();
        var events = new List<double?>();
        scene.HoverTimeChanged += (_, args) => events.Add(args.TimeSeconds);
        var initial = scene.BuildStaticLayers();
        Check(!scene.StaticDirty && initial.Any(layer => layer.Id == 0 && !layer.Remove),
            "Initial presentation did not produce the static chart layer.");
        var hover = scene.UpdateHover(566.08, 200);
        Check(scene.HoverTimeSeconds.HasValue && Close(scene.HoverTimeSeconds.Value, -.4) &&
              events.Count == 1 && events[0] == scene.HoverTimeSeconds,
            "Pointer hover lost the continuous time needed by replay synchronization.");
        Check(!scene.StaticDirty && hover.All(layer => layer.Id != 0) &&
              Enumerable.Range(1, 3).All(id => hover.Any(layer => layer.Id == id && !layer.Remove)),
            "Hover must update all three chart lanes without replacing the static background.");
        var sameSample = scene.UpdateHover(568, 200);
        Check(sameSample.Count == 0 && events.Count == 2,
            "A move within the same nearest sample rebuilt pixels or failed to publish replay time.");
        scene.SetHoverTime(.14);
        Check(events.Count == 2 && scene.HoverTimeSeconds == .14,
            "A synchronized replay hover raised a pointer event recursively.");
        var leave = scene.UpdateHover(0, 0, true);
        Check(scene.HoverTimeSeconds == null && events.Count == 3 && events[2] == null &&
              Enumerable.Range(1, 3).All(id => leave.Any(layer => layer.Id == id && layer.Remove)),
            "Leaving the charts did not clear every hover lane and notify its consumer.");
    }

    private static void SelectionCancellationAndZoomPreserveLaneSynchronization()
    {
        using var scene = Scene();
        scene.BuildStaticLayers();
        var zooms = new List<LandingChartZoomEventArgs>();
        scene.ZoomRangeSelected += (_, args) => zooms.Add(args);
        Check(!scene.BeginSelection(40, 200), "A drag in the axis labels started a zoom selection.");
        Check(scene.BeginSelection(300, 200) && scene.HasSelection, "A drag inside the plot did not start.");
        scene.UpdateSelection(700, 200);
        Check(scene.UpdateHover(700, 200).Any(layer => layer.Id == 5 && !layer.Remove),
            "The current drag has no selection layer.");
        scene.CancelSelection();
        Check(!scene.HasSelection && !scene.StaticDirty && zooms.Count == 0 &&
              scene.SetHoverTime(scene.HoverTimeSeconds).Any(layer => layer.Id == 5 && layer.Remove),
            "Cancelling capture changed zoom or retained the selection layer.");

        Check(scene.BeginSelection(700, 200) && scene.EndSelection(300, 200),
            "A reverse-direction drag failed to commit.");
        Check(!scene.HasSelection && scene.StaticDirty && zooms.Count == 1 &&
              zooms[0].StartSeconds > -3 && zooms[0].EndSeconds < 2 &&
              zooms[0].StartSeconds < zooms[0].EndSeconds,
            "A committed drag did not publish a normalized zoom range.");
        scene.BuildStaticLayers();
        var expected = (zooms[0].StartSeconds!.Value + zooms[0].EndSeconds!.Value) / 2;
        foreach (var y in new[] { 200d, 440, 520 })
        {
            scene.UpdateHover(548, y);
            Check(scene.HoverTimeSeconds.HasValue && Close(scene.HoverTimeSeconds.Value, expected),
                "The main, G and altitude lanes disagree about zoomed pointer time.");
        }
        scene.ResetZoom();
        Check(zooms.Count == 2 && zooms[1].StartSeconds == null && zooms[1].EndSeconds == null,
            "Reset did not notify consumers that the full time range is restored.");
        scene.BuildStaticLayers();
        scene.UpdateHover(548, 200);
        Check(scene.HoverTimeSeconds.HasValue && Close(scene.HoverTimeSeconds.Value, -.5),
            "Reset retained the previous zoom mapping.");
    }

    private static void SceneHandlesDpiReplayAndRecordReplacement()
    {
        using var scene = Scene();
        foreach (var dpi in new[] { new Point(96, 96), new Point(144, 120) })
        {
            scene.Resize(1000, 586, dpi.X, dpi.Y);
            var layers = scene.BuildStaticLayers().Concat(scene.UpdateHover(548, 200)).ToArray();
            foreach (var layer in layers.Where(layer => !layer.Remove))
            {
                Check(layer.Width > 0 && layer.Height > 0 && layer.Stride >= layer.Width * 4 &&
                      layer.Bgra.LongLength == (long)layer.Stride * layer.Height,
                    "A chart raster does not meet the native premultiplied BGRA upload contract.");
                Check(layer.XDip >= 0 && layer.YDip >= 0 &&
                      layer.XDip * dpi.X / 96 + layer.Width <= Math.Ceiling(1000 * dpi.X / 96) &&
                      layer.YDip * dpi.Y / 96 + layer.Height <= Math.Ceiling(586 * dpi.Y / 96),
                    "A cropped chart layer escaped the surface after DPI scaling.");
            }
        }
        scene.SetMode(LandingChartMode.Power);
        Check(scene.StaticDirty && scene.Mode == LandingChartMode.Power, "Mode changes did not invalidate the scene.");
        scene.BuildStaticLayers();
        scene.SetIsolatedSeries(1);
        Check(scene.StaticDirty, "Series isolation did not invalidate the chart pixels.");
        scene.BuildStaticLayers();
        scene.RefreshLocalization();
        Check(scene.StaticDirty, "Localization refresh did not invalidate chart text.");
        scene.BuildStaticLayers();

        scene.SetZoomRange(-1, 1);
        var refreshed = Record();
        refreshed.Id = scene.Record!.Id;
        foreach (var point in refreshed.Series) point.TimeSeconds += 20;
        scene.SetRecord(refreshed);
        scene.BuildStaticLayers();
        scene.UpdateHover(548, 200);
        Check(scene.HoverTimeSeconds.HasValue && Close(scene.HoverTimeSeconds.Value, 19.5),
            "Refreshing the same record identity retained a zoom outside its new coverage.");

        scene.SetReplayVisible(true);
        scene.SetLaneHeights(0, 80, 0, 26);
        scene.Resize(1000, 106, 96, 96);
        scene.BuildStaticLayers();
        scene.SetHoverTime(null);
        var replayHover = scene.UpdateHover(548, 30);
        Check(scene.IsOverPlot(548, 30) && !scene.IsOverPlot(548, 200) &&
              replayHover.Any(layer => layer.Id == 2 && !layer.Remove) &&
              replayHover.All(layer => layer.Id != 1 && layer.Id != 3 || layer.Remove),
            "Replay layout retained hidden main or altitude plots, or lost the G lane.");
        scene.SetRecord(null);
        scene.BuildStaticLayers();
        Check(!scene.IsOverPlot(548, 30) && scene.HoverTimeSeconds == null && !scene.BeginSelection(548, 30),
            "Clearing the record retained an interactive replay lane.");
    }

    private static void ModalSnapshotPreservesPendingNativeChanges()
    {
        using var scene = Scene();
        var hoverEvents = 0;
        scene.HoverTimeChanged += (_, __) => hoverEvents++;
        Check(scene.StaticDirty, "The new scene should require its first native upload.");
        Check(scene.CreateSnapshot() != null && scene.StaticDirty,
            "Creating a modal snapshot consumed the pending initial native scene.");
        Check(scene.BuildStaticLayers().Any(layer => layer.Id == 0 && !layer.Remove),
            "Native presentation lost its static pixels after taking a snapshot.");
        scene.SetHoverTime(.14);
        scene.CreateSnapshot();
        Check(scene.SetHoverTime(.14).Count == 0 && !scene.StaticDirty && hoverEvents == 0,
            "Snapshot creation changed the hover cache, published a pointer event or invalidated static data.");

        Check(scene.BeginSelection(300, 200), "Snapshot fixture could not begin a drag.");
        scene.UpdateSelection(700, 200);
        scene.CreateSnapshot();
        Check(scene.HasSelection && scene.SetHoverTime(.14).Any(layer => layer.Id == 5 && !layer.Remove),
            "Creating a snapshot consumed the pending native drag layer.");
        scene.CancelSelection();
        scene.CreateSnapshot();
        Check(!scene.HasSelection && scene.SetHoverTime(.14).Any(layer => layer.Id == 5 && layer.Remove),
            "Creating a snapshot consumed the pending removal of a cancelled drag.");

        scene.SetMode(LandingChartMode.Power);
        scene.CreateSnapshot();
        Check(scene.StaticDirty && scene.Mode == LandingChartMode.Power && hoverEvents == 0,
            "A modal snapshot acknowledged a mode change before native presentation received it.");
        Check(scene.BuildStaticLayers().Any(layer => layer.Id == 0 && !layer.Remove),
            "Restoring native presentation omitted the changed mode.");
    }

    private static void ModalSnapshotReflectsCurrentContentAndDpi()
    {
        using var scene = Scene();
        var initial = Snapshot(scene);
        Check(initial.IsFrozen && initial.Format == PixelFormats.Pbgra32 &&
              initial.PixelWidth == 1000 && initial.PixelHeight == 586 && Close(initial.DpiX, 96) && Close(initial.DpiY, 96),
            "A modal snapshot did not preserve the complete surface and premultiplied pixel format.");
        scene.SetHoverTime(.14);
        var hovered = Snapshot(scene);
        Check(!Pixels(initial).SequenceEqual(Pixels(hovered)), "The snapshot omitted the current synchronized hover.");
        scene.SetMode(LandingChartMode.Power);
        var power = Snapshot(scene);
        Check(!Pixels(hovered).SequenceEqual(Pixels(power)), "A modal snapshot retained the previous chart mode.");
        scene.SetRecord(null);
        var empty = Snapshot(scene);
        Check(!Pixels(power).SequenceEqual(Pixels(empty)), "Clearing the record retained its pixels in the modal background.");
        scene.SetRecord(Record());
        scene.Resize(1000, 586, 144, 120);
        var scaled = Snapshot(scene);
        Check(scaled.IsFrozen && scaled.PixelWidth == 1500 && scaled.PixelHeight == 733 &&
              Close(scaled.DpiX, 144) && Close(scaled.DpiY, 120),
            "Mixed-DPI modal snapshot dimensions do not match the native surface.");
        LocalizationManager.Apply("ru");
        scene.RefreshLocalization();
        var localized = Snapshot(scene);
        Check(!Pixels(scaled).SequenceEqual(Pixels(localized)) && scene.StaticDirty,
            "Localization did not update the modal background or consumed the native redraw.");
        LocalizationManager.Apply("en");
        scene.RefreshLocalization();

        scene.SetReplayVisible(true);
        scene.SetLaneHeights(0, 80, 0, 26);
        scene.Resize(1000, 106, 96, 96);
        var replay = Snapshot(scene);
        Check(replay.PixelWidth == 1000 && replay.PixelHeight == 106 && replay.IsFrozen,
            "The replay modal snapshot retained the hidden main and altitude rows.");
        scene.Resize(0, 0, 96, 96);
        Check(scene.CreateSnapshot() == null && scene.StaticDirty,
            "An unarranged chart created a snapshot or consumed its pending redraw.");
    }

    private static BitmapSource Snapshot(ChartPanelScene scene) => scene.CreateSnapshot() ??
        throw new InvalidOperationException("An arranged chart returned no modal snapshot.");

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static ChartPanelScene Scene()
    {
        var scene = new ChartPanelScene();
        scene.SetRecord(Record());
        scene.SetLaneHeights(400, 80, 80, 26);
        scene.Resize(1000, 586, 96, 96);
        return scene;
    }

    private static LandingRecord Record()
    {
        var times = new[] { -3d, -1, 0, .25, 2 };
        var record = new LandingRecord
        {
            Series = times.Select((time, index) => new LandingSeriesPoint
            {
                TimeSeconds = time, InertialFpm = -500 + index * 110, IndicatedFpm = -450 + index * 80,
                GForce = 1 + index * .08, LongitudinalLoadG = -.1 + index * .03, LateralLoadG = .02 * index,
                AglFeet = 100 - index * 24, PitchDegrees = 2 + index, BankDegrees = index - 2,
                AngleOfAttackDegrees = 4 + index * .2, PilotRollPercent = 10 * index, PilotPitchPercent = 5 * index,
                PilotYawPercent = -3 * index, AileronPercent = index, ElevatorPercent = 2 * index,
                RudderPercent = -index, OnGround = time >= 0,
            }).ToList(),
        };
        for (var engine = 1; engine <= 2; engine++)
            record.Engines.Add(new LandingEngineSeries
            {
                EngineNumber = engine,
                Points = times.Select((time, index) => new LandingEnginePoint
                {
                    TimeSeconds = time, N1Percent = 60 - index * 8 + engine,
                    ThrottlePercent = 70 - index * 15, ReversePercent = index > 2 ? 40 : 0,
                    Rpm = 2000 - index * 200,
                }).ToList(),
            });
        for (var contact = 0; contact < 3; contact++)
            record.ContactPoints.Add(new LandingContactSeries
            {
                ContactPointIndex = contact,
                Points = times.Select((time, index) => new LandingContactPoint
                {
                    TimeSeconds = time, CompressionPercent = time >= 0 ? 10 + index * 4 + contact : 0,
                    PositionPercent = 100, OnGround = time >= 0,
                }).ToList(),
            });
        return record;
    }

    private static IEnumerable<Drawing> Drawings(Drawing drawing)
    {
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var nested in Drawings(child)) yield return nested;
    }

    private static bool Close(double actual, double expected) => Math.Abs(actual - expected) < 1e-9;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}
