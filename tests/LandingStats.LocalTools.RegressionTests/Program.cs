using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LandingStats.Core;
using LandingStats.Probe;

internal static class Program
{
    private static void Main()
    {
        SegmentsKeepTheirOwnGrid();
        InvalidClocksTerminate();
        ResearchChannelsSurviveCapture();
        Console.WriteLine("PASS local tools: physical segments, bounded sampling, all 33 research channels and capture ZIP");
    }
    private static void SegmentsKeepTheirOwnGrid()
    {
        foreach (var reset in new[] { false, true })
        {
            var samples = new List<TelemetrySample>();
            for (var segment = 0; segment < 2; segment++)
            for (var row = 0; row < 100; row++)
            {
                var sample = Sample(segment == 0 ? 100 + row * .02 : (reset ? 0 : 300) + row * .02);
                sample.FlightSegment = segment + 1; sample.Sequence = segment * 100 + row;
                sample.OnGround = row >= 50; samples.Add(sample);
            }
            var full = TouchdownAnalysis.Analyze(samples);
            var reduced = TelemetryDownsampler.Downsample(samples, 50, 0);
            Check(reduced.Count(row => row.MotionSimulation) == 200, "Original-rate sampling discarded physical rows.");
            Check(full.Count == 2 && TouchdownAnalysis.Analyze(reduced).Count == 2, "Sampling dropped a flight/contact.");
            Check(reduced.Any(row => !row.MotionSimulation), "New flight boundary vanished.");
            foreach (var phase in new[] { .25, .5, .75 })
            {
                var phased = TelemetryDownsampler.Downsample(samples, 20, phase);
                Check(phased.Any(row => row.FlightSegment == 1 && row.MotionSimulation) && phased.Any(row => row.FlightSegment == 2 && row.MotionSimulation), "Phase skipped an entire segment.");
            }
        }
        var first = Sample(0); var inactive = Sample(.1); inactive.SimulatorState = TelemetrySimulatorState.Inactive;
        var last = Sample(.2); last.OnGround = true;
        var barrier = TelemetryDownsampler.Downsample(new[] { first, inactive, last }, 1, 0);
        Check(barrier.Any(row => !row.MotionSimulation) && TouchdownAnalysis.Analyze(barrier).Count == 0,
            "A downsampled inactive boundary invented a touchdown.");
    }
    private static void InvalidClocksTerminate()
    {
        foreach (var rate in new[] { double.NaN, double.PositiveInfinity, 0, -1, double.Epsilon })
            Reject(() => TelemetryDownsampler.Downsample(new[] { Sample(0) }, rate, 0));
        foreach (var phase in new[] { double.NaN, double.PositiveInfinity, -.1, 1 })
            Reject(() => TelemetryDownsampler.Downsample(new[] { Sample(0) }, 50, phase));
        var invalid = Sample(double.PositiveInfinity); invalid.HostElapsedSeconds = double.PositiveInfinity;
        Reject(() => TelemetryDownsampler.Downsample(new[] { invalid }, 50, 0));
        var fallback = Sample(double.NaN); fallback.HostElapsedSeconds = 1;
        Check(TelemetryDownsampler.Downsample(new[] { fallback }, 50, 0).Count == 1, "Finite host fallback was rejected.");
        Check(TelemetryDownsampler.Downsample(new[] { Sample(0), Sample(1e300) }, 50, 0).Count == 2, "Huge gap did not advance in bounded time.");
        Check(TelemetryDownsampler.Downsample(new[] { Sample(0), Sample(0), Sample(1) }, double.MaxValue, 0).Count == 2, "Unrepresentable period duplicated rows or failed to advance.");
    }
    private static void ResearchChannelsSurviveCapture()
    {
        var names = ProbeResearchChannels.Header.Split(',').Skip(4).ToArray();
        Check(names.Length == 33 && names.Distinct().Count() == 33, "Research channel schema drifted.");
        object boxed = new SimFrameData();
        for (var i = 0; i < names.Length; i++)
        {
            var field = typeof(SimFrameData).GetField(names[i])!;
            field.SetValue(boxed, field.FieldType == typeof(int) ? (object)(i + 1) : i + .25);
        }
        var sample = Sample(20); sample.Sequence = 123; sample.HostElapsedSeconds = 10;
        ProbeResearchChannels.Attach(sample, (SimFrameData)boxed);
        var values = ProbeResearchChannels.Format(sample).Split(',');
        Check(values.Length == 37 && values[0] == "123" && values[3] == "1", "Research row identity/availability missing.");
        for (var i = 0; i < names.Length; i++)
        {
            var field = typeof(SimFrameData).GetField(names[i])!;
            var expected = names[i] is "OnAnyRunway" or "SpoilersArmed" ? 1 : field.FieldType == typeof(int) ? i + 1 : i + .25;
            Check(double.Parse(values[i + 4], CultureInfo.InvariantCulture) == expected, "Research channel lost: " + names[i]);
        }
        var absent = ProbeResearchChannels.Format(Sample(0)).Split(',');
        Check(absent[3] == "0" && absent.Skip(4).All(value => value == "NaN"), "Missing evidence became fabricated zeros.");
        var root = Path.Combine(Path.GetTempPath(), "ls-probe-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable(CaptureSession.CaptureRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(CaptureSession.CaptureRootEnvironmentVariable, root);
            using var capture = new CaptureSession(); capture.Write(sample);
            var archivePath = capture.CompleteAndCreateArchive();
            using var archive = ZipFile.OpenRead(archivePath);
            using var raw = new StreamReader(archive.GetEntry("telemetry.csv")!.Open());
            Check(raw.ReadLine() == TelemetryCsv.Header && TelemetryCsv.TryParse(raw.ReadLine()!, out var restored) && restored.Sequence == sample.Sequence,
                "Research migration broke shared schema7.");
            using var research = new StreamReader(archive.GetEntry(ProbeResearchChannels.FileName)!.Open());
            Check(research.ReadLine() == ProbeResearchChannels.Header && research.ReadLine() == ProbeResearchChannels.Format(sample), "Research evidence missing from ZIP.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CaptureSession.CaptureRootEnvironmentVariable, previous);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static TelemetrySample Sample(double time) => new()
    {
        SimulationTimeSeconds = time, HostElapsedSeconds = time, MotionSimulation = true,
        SimulatorState = TelemetrySimulatorState.Running, GForce = 1, PlaneAltitudeFeet = 105,
        GroundAltitudeFeet = 100, AboveGroundLevelFeet = 5, GroundSpeedKnots = 100,
        LatitudeDegrees = 40, LongitudeDegrees = 20, VelocityWorldYFps = -2
    };
    private static void Reject(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid sampling input was accepted.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
