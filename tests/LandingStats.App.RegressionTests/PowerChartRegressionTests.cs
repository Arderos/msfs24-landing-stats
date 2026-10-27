using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.IO;
using LandingStats.Core;
using LandingStats.App.Storage;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LandingStats.App.Controls;
using LandingStats.App.Models;
using LandingStats.App.Settings;
using LandingStats.App.Runtime;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.RegressionTests;

internal static class PowerChartRegressionTests
{
    public static void RunAll()
    {
        LocalizationManager.Apply("en");
        try
        {
            RecordedSignalsRemainDistinct();
            RpmUsesRecordedUnitsAndCommonScale();
            PhysicalThrottleUsesRecordedAssignment();
            RecordedTelemetryAndHistoryReachTheRenderedSources();
            PhysicalThrottleBreaksAtProfileChanges();
            ScalingFollowsVisibleEnginesAndTime();
            MissingReverseRemainsMissing();
            PowerCurvesKeepTimeGaps();
            LocalizationNamesTheRecordedQuantities();
        }
        finally { LocalizationManager.Apply("en"); }
    }

    private static void PhysicalThrottleBreaksAtProfileChanges()
    {
        var record = Record(); record.ControllerAxisCount = 8; record.RawControllerAxesSourceIndices.AddRange(new[] { 3, 4 });
        foreach (var point in record.Series)
        { point.RawControllerAxesPercent = new[] { -40d, 60d }; point.RawControllerAxesValid = new[] { true, true }; point.RawControllerAxesAgeSeconds = new[] { 0d, 0d }; }
        var device = Guid.NewGuid(); var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Controllers);
        var descriptor = new ControllerDescriptor(0, 0, "Throttle", "DirectInput", 255, new uint[8], Enumerable.Repeat(65535u, 8).ToArray(), device);
        string Catalog(int axis) => Convert.ToBase64String(ControllerProvenanceCodec.Encode(new RuntimeEnvelope(worker, 0, 1, 100,
            new ControllerCatalogMessage(1, new[] { descriptor }, new[] { new ControllerBinding(ControllerControlKind.Throttle, 0, device, axis, false, "Profile " + axis) }))));
        record.ControllerTimeline = new ControllerCatalogTimeline { SampleCount = record.Series.Count, Catalogs = new[] { Catalog(3), Catalog(4) },
            Transitions = new[] { new[] { 0, 0 }, new[] { 2, 1 } } };
        var chart = Chart(record); Render(chart);
        Check(PathGeometry.CreateFromGeometry(Curves(chart, 0)[1].Geometry).Figures.Count == 2,
            "The physical throttle curve joined readings across a change of its recorded axis assignment.");
        Check(PathGeometry.CreateFromGeometry(Curves(chart, 0)[0].Geometry).Figures.Count == 1,
            "A controller profile change interrupted the independent N1 channel.");
    }

    private static void RecordedTelemetryAndHistoryReachTheRenderedSources()
    {
        var device = Guid.NewGuid();
        var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Controllers);
        var devices = Enumerable.Range(0, 12).Select(index => new ControllerDescriptor(index, index, "Device " + index, "DirectInput", 255,
            new uint[8], Enumerable.Repeat(65535u, 8).ToArray(), index == 11 ? device : Guid.NewGuid())).ToArray();
        var catalog = ControllerProvenanceCodec.Encode(new RuntimeEnvelope(worker, 0, 1, 100,
            new ControllerCatalogMessage(1, devices, new[] { new ControllerBinding(ControllerControlKind.Throttle, -1, device, 7, true, "Recorded profile") })));
        var n1 = new[] { 67d, 64, 59, 18, 22, 50 }; var throttle = new[] { 70d, 70, 70, 20, 0, 0 };
        var command = new[] { 20d, 33, 49, 35, -30, -55 }; var reverse = new[] { 0d, 0, 0, 0, 50, 100 };
        var samples = Enumerable.Range(0, n1.Length).Select(index =>
        {
            var sample = new TelemetrySample { Sequence = index + 1, SimulationTimeSeconds = 10 + index * .1, MotionSimulation = true,
                OnGround = index >= 3, AboveGroundLevelFeet = Math.Max(0, 3 - index), NumberOfEngines = 2, GForce = 1 };
            sample.ResizeControllers(12, 8); sample.ControllerCatalog = catalog;
            sample.RawControllerDeviceId[11] = 11; sample.RawControllerAxesPercent[95] = 100 - 2 * throttle[index]; sample.RawControllerAxesValid[95] = true;
            for (var engine = 0; engine < 2; engine++)
            { sample.EngineN1Percent[engine] = n1[index]; sample.EngineThrottlePercent[engine] = command[index]; sample.EngineReversePercent[engine] = reverse[index]; }
            return sample;
        }).ToArray();
        // Use the production numeric archive reader and history column layout.
        var csv = TelemetryCsv.Header + "\n" + string.Join("\n", samples.Select(TelemetryCsv.Format));
        var path = Path.Combine(Path.GetTempPath(), "power-source-" + Guid.NewGuid().ToString("N") + ".csv");
        TelemetrySample[] decoded;
        try { File.WriteAllText(path, csv); decoded = TelemetryCsv.ReadFile(path).ToArray(); }
        finally { if (File.Exists(path)) File.Delete(path); }
        for (var i = 0; i < decoded.Length; i++) decoded[i].ControllerCatalog = catalog;
        var record = LandingRecordFactory.Create(new TouchdownResult { ContactNumber = 1, Sequence = 4, SimulationTimeSeconds = 10.3, EstimatedContactTimeSeconds = 10.3 },
            decoded, "Recorded source fixture", "JET");
        record = LandingRecordFile.FromRecord(record).ToRecord();
        Check(record.Engines.Count == 2 && record.Series.Count == n1.Length, "History lost engine rows or channels.");
        var chart = Chart(record); Render(chart);
        for (var engine = 0; engine < 2; engine++)
        {
            var curves = Curves(chart, engine);
            CheckValues(chart, curves[0], n1, "Rendered engine speed differs from recorded N1.");
            CheckValues(chart, curves[1], throttle, "Rendered lever differs from the shared physical axis of device twelve.");
            CheckValues(chart, curves[2], reverse, "Rendered reverser doors differ from their recorded source.");
            Check(record.Engines[engine].Points.Select(p => p.ThrottlePercent).SequenceEqual(command), "The simulator command was overwritten by physical throttle values.");
        }
        SaveProof(chart, "recorded-n1-physical-throttle.png");
    }

    private static void SaveProof(LandingChart chart, string name)
    {
        var directory = Environment.GetEnvironmentVariable("MSFSLS_POWER_PROOF_DIRECTORY");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(1000, 320, 96, 96, PixelFormats.Pbgra32); bitmap.Render(chart);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }

    private static void RpmUsesRecordedUnitsAndCommonScale()
    {
        var record = Record(); record.Engines.Add(Engine(2));
        foreach (var engine in record.Engines) foreach (var point in engine.Points)
        { point.N1Percent = 0; point.Rpm = 1200 * engine.EngineNumber + (point.TimeSeconds + 2) * 100; }
        var chart = Chart(record); Render(chart);
        var maximum = Field<double>(chart, "_powerRpmMaximum");
        for (var engine = 0; engine < 2; engine++)
            CheckValues(chart, Curves(chart, engine)[0], record.Engines[engine].Points.Select(p => p.Rpm / maximum * 100).ToArray(), "RPM must use one explicitly labelled scale for all engines.");
        chart.SetHoverTime(0); Render(chart);
        Check(Text(chart).Contains(LocalizationManager.Format("Chart.RpmFormat", 1400)) && Text(chart).Contains(LocalizationManager.Format("Chart.RpmFormat", 2600)),
            "Hover lost recorded revolutions per minute or presented them as a percentage.");
        SaveProof(chart, "recorded-rpm.png");
        Check(Text(chart).Contains(LocalizationManager.Format("Chart.RpmFormat", maximum)), "The secondary scale must display its RPM units.");
        chart.SetIsolatedSeries(0); Render(chart);
        Check(Field<double>(chart, "_powerRpmMaximum") < maximum, "Hidden engines should not expand the visible RPM scale.");
        Check(Text(chart).Contains(LocalizationManager.Format("Chart.RpmFormat", 1400)), "Changing the scale changed the displayed RPM value.");
        record.Engines[0].Points[2].Rpm = double.NaN;
        chart.Record = record; Render(chart);
        Check(PathGeometry.CreateFromGeometry(Curves(chart, 0)[0].Geometry).Figures.Count == 2, "Missing RPM must remain a gap.");
    }

    private static void PhysicalThrottleUsesRecordedAssignment()
    {
        LandingRecord Physical(bool attached)
        {
            var record = Record(); record.ControllerAxisCount = 8; record.RawControllerAxesSourceIndices.Add(3);
            var values = new[] { -40d, -40, 0, 80, 100 };
            for (var i = 0; i < record.Series.Count; i++)
            {
                record.Series[i].RawControllerAxesPercent = new[] { values[i] };
                record.Series[i].RawControllerAxesValid = new[] { attached };
                record.Series[i].RawControllerAxesAgeSeconds = new[] { 0d };
            }
            var deviceId = Guid.NewGuid();
            var descriptor = new ControllerDescriptor(0, 0, "Physical lever", "test", 255, new uint[8], Enumerable.Repeat(65535u, 8).ToArray(), deviceId);
            var binding = new ControllerBinding(ControllerControlKind.Throttle, 0, deviceId, 3, true, "saved profile");
            var worker = new WorkerIdentity(Guid.NewGuid(), Guid.NewGuid(), 1, ComponentRole.Controllers);
            var catalog = ControllerProvenanceCodec.Encode(new RuntimeEnvelope(worker, 0, 1, 100,
                new ControllerCatalogMessage(1, attached ? new[] { descriptor } : Array.Empty<ControllerDescriptor>(), new[] { binding })));
            record.ControllerTimeline = new ControllerCatalogTimeline { SampleCount = record.Series.Count,
                Catalogs = new[] { Convert.ToBase64String(catalog) }, Transitions = new[] { new[] { 0, 0 } } };
            return record;
        }
        var record = Physical(true); var chart = Chart(record); Render(chart);
        var curves = Curves(chart, 0);
        CheckValues(chart, curves[1], new[] { 70d, 70, 50, 10, 0 }, "The dashed curve must use physical travel with the recorded inversion.");
        chart.SetHoverTime(.1); Render(chart);
        Check(Text(chart).Contains(LocalizationManager.Text("Chart.HoverPhysicalThrottle")), "The physical lever must be identified in the hover label.");
        Check(record.Engines[0].Points[3].ThrottlePercent == -20, "Rendering a physical lever overwrote the recorded simulator command.");
        chart.Record = Physical(false); Render(chart);
        Check(PathGeometry.CreateFromGeometry(Curves(chart, 0)[1].Geometry).Figures.Count == 0,
            "A known lever with no physical observations must remain a gap, without substitution from the simulator command.");
    }

    private static void RecordedSignalsRemainDistinct()
    {
        var record = Record(); var chart = Chart(record); Render(chart);
        var curves = Curves(chart, 0);
        Check(curves.Length == 3 && curves[0].Pen.DashStyle.Dashes.Count == 0 &&
                curves[1].Pen.DashStyle.Dashes.Count > 0 && curves[2].Pen.DashStyle.Dashes.Count > 0 &&
                !curves[1].Pen.DashStyle.Dashes.SequenceEqual(curves[2].Pen.DashStyle.Dashes),
            "N1, simulator throttle command and reverser doors must have three distinct line styles.");
        CheckValues(chart, curves[0], new[] { 60d, 45, 18, 30, 50 }, "The recorded N1 idle dip remains 18 percent.");
        CheckValues(chart, curves[1], new[] { 75d, 75, 0, -20, -40 }, "Negative simulator throttle command is preserved below zero.");
        CheckValues(chart, curves[2], new[] { 0d, 0, 0, 50, 100 }, "Reverser-door position is drawn independently of N1 and throttle.");
        var plot = Field<Rect>(chart, "_drawingPlot");
        var zero = Vertices(PathGeometry.CreateFromGeometry(curves[1].Geometry)).ElementAt(2).Y;
        Check(zero > plot.Top && zero < plot.Bottom && Drawings(VisualTreeHelper.GetDrawing(chart)).OfType<GeometryDrawing>()
                .Select(d => d.Geometry).OfType<LineGeometry>().Any(line => Math.Abs(line.StartPoint.Y - zero) < .001 &&
                    Math.Abs(line.EndPoint.Y - zero) < .001 && Math.Abs(line.StartPoint.X - plot.Left) < .001 && Math.Abs(line.EndPoint.X - plot.Right) < .001),
            "An explicit horizontal zero line separates positive and negative commands.");
        chart.SetHoverTime(.1); Render(chart);
        var text = Text(chart);
        Check(text.Contains(LocalizationManager.Text("Chart.HoverThrottle") + " " + (-20d).ToString("0.0", CultureInfo.CurrentCulture)) &&
                text.Contains(LocalizationManager.Text("Chart.HoverReverseDoors") + " " + 50d.ToString("0.0", CultureInfo.CurrentCulture)),
            "Hover identifies the negative MSFS command and the independently recorded reverser-door position.");

        chart.SetHoverTime(null);
        var before = curves.Select(curve => curve.Geometry.ToString(CultureInfo.InvariantCulture)).ToArray();
        record.Engines[0].Points[3].ReversePercent = 10;
        chart.Record = record; Render(chart);
        var after = Curves(chart, 0).Select(curve => curve.Geometry.ToString(CultureInfo.InvariantCulture)).ToArray();
        Check(before[0] == after[0] && before[1] == after[1] && before[2] != after[2],
            "Changing only recorded reverser doors changes only their curve.");
    }

    private static void ScalingFollowsVisibleEnginesAndTime()
    {
        var record = Record(); var second = Engine(2);
        second.Points[4].ThrottlePercent = -90; record.Engines.Add(second);
        var chart = Chart(record); Render(chart);
        Check(Field<double>(chart, "_drawingMinValue") <= -90, "Every displayed engine contributes its negative throttle extent.");
        chart.SetIsolatedSeries(0); Render(chart);
        Check(Field<double>(chart, "_drawingMinValue") <= -40 && Field<double>(chart, "_drawingMinValue") > -90 && Curves(chart, 1).Length == 0,
            "An isolated engine excludes the hidden engine from the scale and drawing.");
        chart.SetZoomRange(-.2, 0); Render(chart);
        Check(Field<double>(chart, "_drawingMinValue") == 0, "Negative commands outside the visible window do not expand its scale.");
        chart.SetZoomRange(0, .2); Render(chart);
        Check(Field<double>(chart, "_drawingMinValue") <= -40, "Zooming onto reverse-range commands restores space below zero.");
        chart.PowerShowThrottle = false; chart.SetHoverTime(.1); Render(chart);
        Check(Field<double>(chart, "_drawingMinValue") == 0 && Curves(chart, 0).Length == 2 &&
                !Text(chart).Contains(LocalizationManager.Text("Chart.HoverThrottle")) && Text(chart).Contains(LocalizationManager.Text("Chart.HoverReverseDoors")),
            "PowerShowThrottle hides only that channel and its scale contribution; N1 and reverser doors remain available.");
        chart.PowerShowThrottle = true; Render(chart);
        Check(Curves(chart, 0).Length == 3 && Field<double>(chart, "_drawingMinValue") <= -40,
            "Re-enabling throttle restores its curve and negative scale without changing the recorded data.");
    }

    private static void PowerCurvesKeepTimeGaps()
    {
        var record = Record(); var times = new[] { -.2, -.1, 2, 2.1, 2.2 };
        for (var index = 0; index < times.Length; index++)
        { record.Series[index].TimeSeconds = times[index]; record.Engines[0].Points[index].TimeSeconds = times[index]; }
        var chart = Chart(record); Render(chart);
        Check(Curves(chart, 0).All(curve => PathGeometry.CreateFromGeometry(curve.Geometry).Figures.Count == 2),
            "Engine, throttle or reverser curves invented observations across a recording gap.");
    }

    private static void MissingReverseRemainsMissing()
    {
        var record = Record(); record.Engines[0].Points[2].ReversePercent = double.NaN;
        var chart = Chart(record); Render(chart);
        Check(PathGeometry.CreateFromGeometry(Curves(chart, 0)[2].Geometry).Figures.Count == 2,
            "A missing reverser-door sample breaks its curve.");
        chart.SetHoverTime(0); Render(chart);
        Check(!HoverText(chart).Contains(LocalizationManager.Text("Chart.HoverReverseDoors")),
            "Hover does not invent a reverser-door measurement at a missing sample.");
        foreach (var point in record.Engines[0].Points) point.ReversePercent = double.PositiveInfinity;
        chart.Record = record; Render(chart);
        Check(PathGeometry.CreateFromGeometry(Curves(chart, 0)[2].Geometry).Figures.Count == 0 &&
                !HoverText(chart).Contains(LocalizationManager.Text("Chart.HoverReverseDoors")),
            "A non-finite reverser channel has neither a visible curve nor a fabricated hover value.");
    }

    private static void LocalizationNamesTheRecordedQuantities()
    {
        LocalizationManager.Apply("ru");
        var chart = Chart(Record()); chart.SetHoverTime(.1); Render(chart);
        Check(Text(chart).Contains("команда тяги MSFS") && Text(chart).Contains("створки реверса") && !Text(chart).Contains("РУД"),
            "Russian labels identify the MSFS throttle command and reverser doors without claiming physical lever or thrust measurements.");
    }

    private static LandingRecord Record() => new LandingRecord
    {
        Series = Enumerable.Range(0, 5).Select(index => new LandingSeriesPoint { TimeSeconds = (index - 2) * .1 }).ToList(),
        Engines = new List<LandingEngineSeries> { Engine(1) },
    };
    private static LandingEngineSeries Engine(int number)
    {
        var n1 = new[] { 60d, 45, 18, 30, 50 }; var throttle = new[] { 75d, 75, 0, -20, -40 }; var reverse = new[] { 0d, 0, 0, 50, 100 };
        return new LandingEngineSeries { EngineNumber = number, Points = Enumerable.Range(0, 5).Select(index =>
            new LandingEnginePoint { TimeSeconds = (index - 2) * .1, N1Percent = n1[index], ThrottlePercent = throttle[index], ReversePercent = reverse[index], Rpm = 2000 }).ToList() };
    }
    private static LandingChart Chart(LandingRecord record) => new LandingChart { Mode = LandingChartMode.Power, Record = record };
    private static void Render(LandingChart chart)
    {
        chart.Measure(new Size(1000, 320)); chart.Arrange(new Rect(0, 0, 1000, 320)); chart.UpdateLayout();
        new RenderTargetBitmap(1000, 320, 96, 96, PixelFormats.Pbgra32).Render(chart);
    }
    private static GeometryDrawing[] Curves(LandingChart chart, int engine) => Drawings(VisualTreeHelper.GetDrawing(chart))
        .OfType<GeometryDrawing>().Where(d => d.Geometry is StreamGeometry && d.Pen?.Brush is SolidColorBrush brush &&
            brush.Color == ((SolidColorBrush)LandingChart.SeriesBrushAt(engine)).Color).ToArray();
    private static void CheckValues(LandingChart chart, GeometryDrawing drawing, double[] expected, string message)
    {
        var plot = Field<Rect>(chart, "_drawingPlot"); var minimum = Field<double>(chart, "_drawingMinValue"); var maximum = Field<double>(chart, "_drawingMaxValue");
        var actual = Vertices(PathGeometry.CreateFromGeometry(drawing.Geometry)).Select(point => minimum + (plot.Bottom - point.Y) / plot.Height * (maximum - minimum)).ToArray();
        Check(actual.Length == expected.Length && actual.Zip(expected, (a, b) => Math.Abs(a - b) < .001).All(equal => equal), message);
    }
    private static string Text(LandingChart chart) => string.Concat(Drawings(VisualTreeHelper.GetDrawing(chart)).OfType<GlyphRunDrawing>()
        .Select(d => d.GlyphRun.Characters == null ? "" : new string(d.GlyphRun.Characters.ToArray())));
    private static string HoverText(LandingChart chart)
    {
        var all = Text(chart); chart.SetHoverTime(null); Render(chart); var withoutHover = Text(chart); chart.SetHoverTime(0); Render(chart);
        return all.StartsWith(withoutHover, StringComparison.Ordinal) ? all.Substring(withoutHover.Length) : all;
    }
    private static IEnumerable<Drawing> Drawings(Drawing? drawing)
    {
        if (drawing == null) yield break;
        yield return drawing;
        if (drawing is DrawingGroup group) foreach (var child in group.Children) foreach (var nested in Drawings(child)) yield return nested;
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
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
