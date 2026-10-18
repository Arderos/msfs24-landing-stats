using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LandingStats.App.Storage;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class RecorderDiscontinuityRegressionTests
{
    public static void RunAll()
    {
        foreach (var sdk in new[] { false, true })
        {
            using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
            var captures = new List<LandingEpisodeEventArgs>();
            recorder.EpisodeCompleted += (_, capture) => captures.Add(capture);
            if (sdk)
            {
                Call(recorder, "SetSdkSimRunning", true);
                Call(recorder, "SetSdkPauseFlags", 0);
            }
            Call(recorder, "ProcessSample", Frame(10, false));
            Call(recorder, "ProcessSample", Frame(10.1, true));
            Call(recorder, "ProcessSample", Frame(10.2, true));
            Call(recorder, "ProcessSample", Frame(10.21, false, 3000));
            var first = captures.Single(capture => !capture.IsDiagnosticOnly);
            Assert(first.Samples.All(sample => sample.LatitudeDegrees < 42.01), "recorder appended replacement position to old landing");
            var result = TouchdownAnalysis.Analyze(first.Samples).Single();
            Assert(!result.Impact.IsComplete && result.Impact.Status != TouchdownImpactStatus.Liftoff,
                "recorder certified a relocation as a complete bounce");
            Assert(first.DecisionJournal!.Snapshot().Entries.Any(entry => entry.Code == "position_discontinuity" &&
                entry.Details.ContainsKey("horizontal_distance_m")), "relocation has no measured decision evidence");
            Call(recorder, "ProcessSample", Frame(10.3, true, 3001));
            Call(recorder, "CompleteEpisode", false);
            var second = captures.Last(capture => !capture.IsDiagnosticOnly);
            Assert(!ReferenceEquals(first, second) && second.Samples.All(sample => sample.LatitudeDegrees > 42.02),
                "replacement flight reused the old pre-roll or contact");
        }

        // Historical raw-debug archives can contain both flights even though the
        // current recorder now separates them at capture time.
        var raw = new[] { Frame(10, false), Frame(10.1, true), Frame(10.2, true),
            Frame(10.21, false, 3000), Frame(10.3, true, 3001), Frame(10.4, true, 3002) };
        var contacts = TouchdownAnalysis.Analyze(raw);
        Assert(contacts.Count == 2, "fixture lost a real contact");
        for (var index = 0; index < contacts.Count; index++)
        {
            var record = LandingRecordFactory.Create(contacts[index], raw, "test", "jet");
            Assert(record.Series.Count > 0 && record.Series.All(point => point.LatitudeDegrees.HasValue &&
                (index == 0 ? point.LatitudeDegrees < 42.01 : point.LatitudeDegrees > 42.02)),
                "stored 3D position series contains a different flight");
        }
        Assert(raw.All(sample => sample.AnalysisDiscontinuity == null && sample.MotionSimulation), "raw archive observations were mutated");
    }

    private static TelemetrySample Frame(double time, bool ground, double northMeters = 0) => new TelemetrySample
    {
        Sequence = (long)Math.Round(time * 100), HostElapsedSeconds = time,
        SimulationTimeSeconds = time, SimulationDeltaSeconds = .01,
        MotionSimulation = true, OnGround = ground,
        LatitudeDegrees = 42 + northMeters / 111195, LongitudeDegrees = 23,
        PlaneAltitudeFeet = 604, GroundAltitudeFeet = 600, AboveGroundLevelFeet = 4,
        GroundSpeedKnots = 120, IndicatedAirspeedKnots = 120,
        VelocityWorldYFps = ground ? 0 : -4, TouchdownNormalVelocityFps = ground ? 4 : 0,
        GForce = ground ? 1.5 : 1,
    };

    private static void Call(object target, string method, params object[] arguments) =>
        RecorderTestAccess.Call(target, method, arguments);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
