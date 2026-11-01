using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class ContactSourceAnalysisRegressionTests
{
    public static void RunAll()
    {
        foreach (var scenario in Scenarios())
        {
            var numeric = scenario.Samples.SelectMany(TelemetryBinaryCodec.Encode).ToArray();
            var source = ReplaySourceTimelineCodec.Encode(scenario.Samples)!;
            Check(TouchdownAnalysis.Analyze(scenario.Samples).Count == scenario.Expected, scenario.Name + ": raw analysis");
            var prepared = TelemetryAnalysisFrames.Prepare(scenario.Samples);
            var unique = TelemetryDeduplicator.Deduplicate(prepared);
            Check(TouchdownAnalysis.Analyze(unique).Count == scenario.Expected, scenario.Name + ": deduplicated analysis");
            var continuity = new TelemetryContactContinuity();
            var count = 0;
            foreach (var segment in TelemetryKinematicDiscontinuity.SplitPhysicalSegments(scenario.Samples))
                count += TouchdownAnalysis.Analyze(TelemetryDeduplicator.Deduplicate(
                    TelemetryAnalysisFrames.Prepare(segment)), null, continuity).Count;
            Check(count == scenario.Expected, scenario.Name + ": externally segmented analysis");
            Check(numeric.SequenceEqual(scenario.Samples.SelectMany(TelemetryBinaryCodec.Encode)) &&
                source.SequenceEqual(ReplaySourceTimelineCodec.Encode(scenario.Samples)!), scenario.Name + ": raw inputs changed");
        }
        // Sharing source eligibility between calibrated segments must never
        // share their airborne witness or allow a contact across their boundary.
        var state = new TelemetryContactContinuity();
        TouchdownAnalysis.Analyze(new[] { Frame(0) }, null, state);
        Check(TouchdownAnalysis.Analyze(new[] { Frame(.1, true) }, null, state).Count == 0,
            "An airborne witness crossed separately calibrated segments.");
    }

    internal static IEnumerable<(string Name, TelemetrySample[] Samples, int Expected)> Scenarios()
    {
        foreach (var validInterval in new[] { false, true })
        foreach (var interruption in new[] { "nonmotion", "flight segment", "paused", "same-time release", "SDK replay segment" })
        {
            var samples = new List<TelemetrySample> { Frame(0, true) };
            var blocked = Frame(.1, mask: 7);
            var released = Frame(.2);
            if (interruption == "nonmotion") blocked.MotionSimulation = false;
            if (interruption == "flight segment") released.FlightSegment = 2;
            if (interruption == "paused") blocked.SimulatorState = TelemetrySimulatorState.Paused;
            if (interruption == "same-time release") released = Frame(.1, sourceTime: .11);
            if (interruption == "SDK replay segment") blocked = Frame(.1, motion: false, sdk: SdkReplayState.Active);
            samples.Add(blocked); samples.Add(released);
            if (validInterval) samples.Add(Frame(.21, segment: released.FlightSegment));
            samples.Add(Frame(.3, true, segment: released.FlightSegment));
            yield return (interruption + (validInterval ? " recovered" : " unverified"), samples.ToArray(), validInterval ? 1 : 0);
        }

        var unknown = Frame(.3); unknown.SourceEvidence = null;
        yield return ("unknown same-time endpoint", new[] { Frame(0, true), Frame(.1, mask: 7), Frame(.2),
            unknown, Frame(.3, sourceTime: .31), Frame(.4, true) }, 0);
        yield return ("source change same-time endpoint", new[] { Frame(0, true), Frame(.1, mask: 7), Frame(.2),
            Frame(.3, generation: 2), Frame(.3, sourceTime: .31), Frame(.4, true) }, 0);
        yield return ("ground reset same-time endpoint", new[] { Frame(0, true), Frame(.1, mask: 7), Frame(.2),
            Frame(.3, true), Frame(.3, sourceTime: .31), Frame(.4, true) }, 0);
        yield return ("recovered endpoint before same-time contact", new[] { Frame(0, true), Frame(.1, mask: 7),
            Frame(.2), Frame(.3), Frame(.3, true, sourceTime: .31) }, 1);
        yield return ("ground reset after segment boundary", new[] { Frame(0, true), Frame(.1, mask: 7, motion: false),
            Frame(.2), Frame(.3, true), Frame(.3, sourceTime: .31), Frame(.4, true) }, 0);
        yield return ("same-time recovered contact after segment boundary", new[] { Frame(0, true), Frame(.1, mask: 7, motion: false),
            Frame(.2), Frame(.3), Frame(.3, true, sourceTime: .31) }, 1);

        // The ordinary frozen-time pause shortcut must retain a newly observed
        // source interruption even when neither clock nor ground flag changes.
        var pause = Frame(0); pause.SimulatorState = TelemetrySimulatorState.Paused;
        yield return ("blocked same-time resume", new[] { Frame(0), pause, Frame(0, mask: 7, sourceTime: .1),
            Frame(.2), Frame(.3, true) }, 0);
        var frozenPause = Frame(0, mask: 7, sourceTime: .1); frozenPause.SimulatorState = TelemetrySimulatorState.Paused;
        yield return ("released same-time resume", new[] { Frame(0), frozenPause, Frame(0, sourceTime: .2),
            Frame(.21), Frame(.3, true) }, 1);

        var unavailable = Frame(.2); unavailable.SourceEvidence = null;
        yield return ("unknown after physical boundary", new[] { Frame(0, true), Frame(.1, mask: 7, motion: false),
            unavailable, Frame(.3, true) }, 0);
        yield return ("ordinary bounce", new[] { Frame(0), Frame(.01, true), Frame(.02), Frame(.03, true) }, 2);
    }

    private static TelemetrySample Frame(double time, bool ground = false, int mask = 0, bool motion = true,
        long segment = 1, long generation = 1, double? sourceTime = null, SdkReplayState sdk = SdkReplayState.Inactive)
    {
        const long frequency = 1000000;
        var qpc = 10000000 + (long)Math.Round((sourceTime ?? time) * frequency);
        return new TelemetrySample
        {
            HostElapsedSeconds = time, SimulationTimeSeconds = time, SimulationDeltaSeconds = .01,
            MotionSimulation = motion, OnGround = ground, SimulatorState = TelemetrySimulatorState.Running,
            FlightSegment = segment, LatitudeDegrees = 42, LongitudeDegrees = 23,
            AboveGroundLevelFeet = ground ? 0 : 5, PlaneAltitudeFeet = 604, GroundAltitudeFeet = 600,
            GroundSpeedKnots = 0, IndicatedAirspeedKnots = 0, VelocityWorldYFps = ground ? 0 : -4,
            TouchdownNormalVelocityFps = ground ? 4 : 0, GForce = ground ? 1.2 : 1,
            SourceEvidence = new ReplaySourceEvidence(new Guid("28917b7b-90e2-4bb1-9efd-eab3279a986c"), generation, 1,
                qpc, qpc, frequency, sdk, new ReplaySourceObservation(SourceObservationAvailability.Available,
                    SourceObservationReason.None, mask, 0, 1, simulationTime: sourceTime ?? time,
                    latitude: 42, longitude: 23, altitudeFeet: 604, pitch: 0, bank: 0, heading: 180))
        };
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
