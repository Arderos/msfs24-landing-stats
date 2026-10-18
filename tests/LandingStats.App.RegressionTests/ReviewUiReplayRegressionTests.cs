using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Runtime;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class ReviewUiReplayRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void RunAll()
    {
        LandingPresentationRegressionTests.RunAll();
        LandingDeletionLedgerRegressionTests.RunAll();
        PureMathMatchesWpf();
        var record = CatalogRecord();
        var data = Task.Run(() => ReplayPresentationData.Prepare(record, new[] { record })).GetAwaiter().GetResult();
        Check(data.Surface!.Terrain.IsFrozen && data.Surface.Pavement.IsFrozen, "Background scene cannot cross the UI boundary.");
        Check(ReferenceEquals(data, ReplayPresentationData.Prepare(record, new[] { record })), "Repeated presentation recomputed the same landing.");
        Check(!double.IsNaN(data.Pitch[record.Series[10]]) && double.IsNaN(data.Pitch[record.Series[50]]) && double.IsNaN(data.Pitch[record.Series[70]]),
            "Pitch chart reused a missing or reassigned controller slot or substituted simulator pitch.");
        var inputs = ReplayControlInputs.Resolve(record);
        var overlay = new ReplayControlOverlay(); overlay.SetInputs(inputs);
        overlay.Update(.5, true);
        overlay.Update(3.5, false);
        var hint = ((TextBlock)((ToolTip)overlay.ToolTip).Content).Text;
        Check(hint.Contains("Yoke B") && !hint.Contains("Stick A"), "A pose gap retained the previous controller source tooltip.");
        var journal = new TelemetryDecisionJournal(); ReplayControlInputs.Resolve(record, journal);
        Check(journal.Snapshot().Entries.Any(e => e.Details.TryGetValue("catalog_index", out var index) && index == 2), "Late controller generation is missing from binding diagnostics.");

        var view = new LandingReplayView { Record = record };
        view.SetTime(.5);
        typeof(LandingReplayView).GetField("_azimuth", Private)!.SetValue(view, 1.234);
        typeof(LandingReplayView).GetField("_distance", Private)!.SetValue(view, 456d);
        typeof(LandingReplayView).GetMethod("TogglePlayback", Private)!.Invoke(view, null);
        view.SetRecord(record);
        Check(view.CurrentTimeSeconds == .5 && (double)typeof(LandingReplayView).GetField("_azimuth", Private)!.GetValue(view)! == 1.234 &&
            (double)typeof(LandingReplayView).GetField("_distance", Private)!.GetValue(view)! == 456 &&
            (bool)typeof(LandingReplayView).GetField("_playing", Private)!.GetValue(view)!, "Same-id refresh reset replay time, camera or playback.");
        view.PausePlayback();

        var chart = new LandingChart { Record = record, Mode = LandingChartMode.FlightControls, Width = 800, Height = 300 };
        chart.Measure(new Size(800, 300)); chart.Arrange(new Rect(0, 0, 800, 300));
        void Render()
        {
            var drawing = new DrawingGroup(); using var context = drawing.Open();
            typeof(LandingChart).GetMethod("OnRender", Private)!.Invoke(chart, new object[] { context });
        }
        Render(); var cached = typeof(LandingChart).GetField("_staticDrawing", Private)!.GetValue(chart);
        for (var i = 0; i < 60; i++) { chart.SetHoverTime(i * .05); Render(); }
        Check(ReferenceEquals(cached, typeof(LandingChart).GetField("_staticDrawing", Private)!.GetValue(chart)), "Hover rebuilt static chart geometry.");
        chart.SetZoomRange(.1, 1.5); Render();
        Check(!ReferenceEquals(cached, typeof(LandingChart).GetField("_staticDrawing", Private)!.GetValue(chart)), "Zoom failed to refresh cached geometry.");
        typeof(LandingChart).GetField("_isSelecting", Private)!.SetValue(chart, true);
        typeof(LandingChart).GetMethod("OnLostMouseCapture", Private)!.Invoke(chart, new object[] { new MouseEventArgs(Mouse.PrimaryDevice, 0) });
        Check(!(bool)typeof(LandingChart).GetField("_isSelecting", Private)!.GetValue(chart)!, "Lost capture left a selection gesture active.");
        Check(RecordedTimeSearch.Nearest(3, index => index * 2, 1) == 0 && RecordedTimeSearch.LowerBound(3, index => index * 2, 2) == 1,
            "Shared time search changed tie or exact-match policy.");
    }

    private static void PureMathMatchesWpf()
    {
        var vector = new System.Windows.Media.Media3D.Vector3D(7.12, -4.5, 13.7);
        foreach (var heading in new[] { 0d, 45, 123, 179, 270, 359 })
        foreach (var pitch in new[] { -80d, -17, 0, 23, 85 })
        foreach (var bank in new[] { -170d, -31, 0, 67, 175 })
        {
            var pure = LandingReplayTrack.Orientation(heading, pitch, bank);
            var native = new System.Windows.Media.Media3D.Quaternion(new System.Windows.Media.Media3D.Vector3D(0, 1, 0), -heading) *
                new System.Windows.Media.Media3D.Quaternion(new System.Windows.Media.Media3D.Vector3D(1, 0, 0), -pitch) *
                new System.Windows.Media.Media3D.Quaternion(new System.Windows.Media.Media3D.Vector3D(0, 0, 1), bank);
            native.Normalize();
            foreach (var fraction in new[] { 0d, .1, .5, .9, 1 })
            {
                var pureNext = LandingReplayTrack.Orientation((heading + 3) % 360, pitch + 2, bank - 2);
                var actual = LandingStats.App.ReplayMath.Quaternion.Slerp(pure, pureNext, fraction, true).Transform(vector.ToReplay()).ToWpf();
                var quaternion = System.Windows.Media.Media3D.Quaternion.Slerp(native, pureNext.ToWpf(), fraction, true);
                var matrix = System.Windows.Media.Media3D.Matrix3D.Identity; matrix.Rotate(quaternion);
                Check((actual - matrix.Transform(vector)).Length < 1e-10, "Pure replay orientation/interpolation diverged from the original WPF transform.");
            }
        }
        foreach (var faa in new[] { false, true })
        foreach (var narrow in new[] { false, true })
        foreach (var text in Enumerable.Range(1, 36).Select(n => n.ToString(faa ? "0" : "00")).Concat(new[] { "L", "R", "C", "69" }))
        {
            var numericalBounds = LandingStats.App.ReplayMath.ReplayDesignation.Build(text, faa, narrow).Bounds;
            var renderedBounds = RunwayDesignationGlyphs.Build(text, faa, narrow).Bounds;
            // Native geometry bounds round to float precision (5.6 becomes
            // 5.599999904632568). One micrometre covers that representation error.
            Check(Math.Abs(numericalBounds.Width - renderedBounds.Width) < 1e-6 && Math.Abs(numericalBounds.Height - renderedBounds.Height) < 1e-6 &&
                Math.Abs(numericalBounds.Top - renderedBounds.Top) < 1e-6 && Math.Abs(numericalBounds.Bottom - renderedBounds.Bottom) < 1e-6,
                $"Pure runway designation bounds differ from polygon outlines: {text}, FAA={faa}, narrow={narrow}; width {numericalBounds.Width:R}/{renderedBounds.Width:R}");
        }
        var numerical = new ReplayGearState(); var rendered = new ReplayLandingGear();
        for (var leg = 0; leg < numerical.Legs.Count; leg++)
        {
            var points = numerical.Legs[leg].TyrePoints; var mesh = rendered.Legs[leg].TyrePoints;
            Check(points.Count == mesh.Count, "Pure tyre support count differs from rendering.");
            for (var i = 0; i < points.Count; i++) Check((points[i].ToWpf() - mesh[i]).Length < 1e-12, "Pure tyre support differs from the rendered model.");
        }
    }

    private static LandingRecord CatalogRecord()
    {
        var session = Guid.NewGuid();
        byte[] Catalog(long generation, string name) => RuntimeWire.Encode(new RuntimeEnvelope(
            new WorkerIdentity(session, Guid.NewGuid(), generation, ComponentRole.Controllers), 0, 2, 100,
            new ControllerCatalogMessage(1, new[] { new ControllerDescriptor(3, 7, name, "driver", 63, new uint[6], Enumerable.Repeat(65535u, 6).ToArray()) })));
        var first = Catalog(1, "Stick A"); var second = Catalog(2, "Yoke B");
        var samples = Enumerable.Range(0, 100).Select(i => new TelemetrySample { ControllerCatalog = i < 40 ? first : i < 60 ? Array.Empty<byte>() : second }).ToArray();
        var record = new LandingRecord { ControllerTimeline = ControllerCatalogTimeline.Capture(samples) };
        record.RawControllerAxesSourceIndices.Add(19);
        record.Series = Enumerable.Range(0, 100).Select(i =>
        {
            var value = Math.Sin(i * .31) * 30 + Math.Cos(i * .17) * 7;
            return new LandingSeriesPoint { TimeSeconds = i * .05, LatitudeDegrees = i * .00001, LongitudeDegrees = 0,
                PlaneAltitudeFeet = 100, GroundAltitudeFeet = 0, HeadingTrueDegrees = 0, GroundSpeedKnots = 100,
                PilotPitchPercent = i < 40 ? value : 0, PilotRollPercent = i >= 60 ? value : 0,
                RawControllerYAxisPercent = Array.Empty<double>(), RawControllerYAxisValid = Array.Empty<bool>(), RawControllerYAxisAgeSeconds = Array.Empty<double>(),
                RawControllerAxesPercent = new[] { value }, RawControllerAxesValid = new[] { i < 40 || i >= 60 }, RawControllerAxesAgeSeconds = new[] { .01 } };
        }).ToList();
        return record;
    }
    private static void Check(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
}
