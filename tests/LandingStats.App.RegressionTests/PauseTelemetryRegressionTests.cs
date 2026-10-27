using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class PauseTelemetryRegressionTests
{
    public static void RunAll()
    {
        SchemaSevenPreservesRawAndAnalysisClocks();
        OldSchemasHaveNoInventedLifecycleEvidence();
        ConfirmedPausePreservesTheLandingAndImpact();
        PreparationIsIdempotentAndNeverMutatesRaw();
        SameTimeResumeDoesNotOverwriteAGenuinePeak();
        SameTimeGroundChangeDoesNotInventAContact();
        LifecycleSegmentsAndUnknownPhysicsRemainBarriers();
        NewFlightContactsDoNotBecomeBouncesOfThePreviousFlight();
        ReplayScreeningPreservesSdkFlightOrder();
        FlightSegmentsCannotBorrowGeometryFromEachOther();
        PauseBetweenBouncesPreservesBothContacts();
        InterruptedRolloutRetainsItsContact();
        ZeroIsAValidAnalysisTime();
    }

    private static void SchemaSevenPreservesRawAndAnalysisClocks()
    {
        var raw = Frame(220, false);
        raw.AnalysisTimeSeconds = 20;
        raw.SimulatorState = TelemetrySimulatorState.Paused;
        raw.PauseFlags = 5;
        raw.FlightSegment = 17;
        raw.RawControllerAxesPercent[0] = 38;
        raw.RawControllerAxesValid[0] = true;
        Assert(TelemetryCsv.V7Header == TelemetryCsv.V6Header + ",analysis_time_s,simulator_state,pause_flags,flight_segment", "schema7 header mismatch");
        Assert(TelemetryCsv.SchemaVersion == 8 && TelemetryCsv.Header == TelemetryCsv.V7Header + ",controller_data_v2", "current schema header mismatch");
        foreach (var header in new[] { TelemetryCsv.V7Header, TelemetryCsv.Header })
        {
            var values = TelemetryCsv.Format(raw).Split(',').Take(header.Split(',').Length).ToArray();
            Assert(TelemetryCsv.TryParse(string.Join(",", values), out var parsed), "lifecycle row rejected");
            Assert(parsed.SimulationTimeSeconds == 220 && parsed.AnalysisTimeSeconds == 20 &&
                parsed.SimulatorState == TelemetrySimulatorState.Paused && parsed.PauseFlags == 5 &&
                parsed.FlightSegment == 17 && parsed.RawControllerAxesPercent[0] == 38 &&
                parsed.RawControllerAxesValid[0], "raw or lifecycle data lost");
            values[Array.IndexOf(header.Split(','), "simulator_state")] = "4";
            Assert(!TelemetryCsv.TryParse(string.Join(",", values), out _), "unknown lifecycle enum accepted");
        }
    }

    private static void OldSchemasHaveNoInventedLifecycleEvidence()
    {
        var raw = Frame(10, false);
        raw.RawControllerYAxisPercent[0] = 19;
        raw.RawControllerYAxisValid[0] = true;
        raw.RawControllerAxesPercent[1] = 19;
        raw.RawControllerAxesValid[1] = true;
        var values = TelemetryCsv.Format(raw).Split(',');
        foreach (var header in new[] { TelemetryCsv.V5Header, TelemetryCsv.V6Header })
        {
            Assert(TelemetryCsv.TryParse(string.Join(",", values.Take(header.Split(',').Length)), out var parsed),
                "legacy compact telemetry rejected");
            Assert(double.IsNaN(parsed.AnalysisTimeSeconds) && parsed.SimulatorState == TelemetrySimulatorState.Legacy &&
                parsed.PauseFlags == -1 && parsed.FlightSegment == 0, "legacy row invented SDK evidence");
            Assert(parsed.RawControllerYAxisValid[0], "legacy Y input lost");
            Assert(parsed.RawControllerAxesValid[1] == (header == TelemetryCsv.V6Header), "legacy controller provenance changed");
        }
        var legacy = new TelemetrySample { SimulationTimeSeconds = 7, MotionSimulation = false };
        Assert(ReferenceEquals(TelemetryAnalysisFrames.Prepare(new[] { legacy })[0], legacy), "legacy frame was rewritten");
    }

    private static void ConfirmedPausePreservesTheLandingAndImpact()
    {
        var baseline = Landing();
        var expected = TouchdownAnalysis.Analyze(baseline).Single();
        Assert(expected.Impact.IsComplete, "baseline impact fixture is incomplete");
        foreach (var pauseAt in new[] { 10.85, 11.0, 11.10, 11.4 })
        {
            var paused = WithPause(baseline, pauseAt);
            var actual = TouchdownAnalysis.Analyze(paused).Single();
            Assert(actual.Impact.IsComplete && actual.Impact.Status == expected.Impact.Status &&
                actual.Impact.PeakG == expected.Impact.PeakG &&
                actual.Impact.EndOffsetSeconds == expected.Impact.EndOffsetSeconds &&
                actual.InertialVerticalFpm == expected.InertialVerticalFpm &&
                actual.LatchedNormalFpm.Equals(expected.LatchedNormalFpm) &&
                actual.InertialFitDurationSeconds == expected.InertialFitDurationSeconds,
                $"confirmed pause at {pauseAt} changed result: impact={actual.Impact.Status}/{expected.Impact.Status}, " +
                $"G={actual.Impact.PeakG}/{expected.Impact.PeakG}, end={actual.Impact.EndOffsetSeconds}/{expected.Impact.EndOffsetSeconds}, " +
                $"FPM={actual.InertialVerticalFpm}/{expected.InertialVerticalFpm}, latch={actual.LatchedNormalFpm}/{expected.LatchedNormalFpm}, " +
                $"fit={actual.InertialFitDurationSeconds}/{expected.InertialFitDurationSeconds}");
        }
    }

    private static void PreparationIsIdempotentAndNeverMutatesRaw()
    {
        var raw = WithPause(Landing(), 11.1);
        var before = raw.Select(TelemetryCsv.Format).ToArray();
        var first = TelemetryAnalysisFrames.Prepare(raw);
        var second = TelemetryAnalysisFrames.Prepare(first);
        Assert(first.Select(TelemetryCsv.Format).SequenceEqual(second.Select(TelemetryCsv.Format)), "preparation is not idempotent");
        Assert(before.SequenceEqual(raw.Select(TelemetryCsv.Format)), "preparation mutated raw telemetry");
        Assert(first.Count == Landing().Count && first.All(sample => sample.MotionSimulation), "confirmed pause remained a measurement barrier");
    }

    private static void SameTimeResumeDoesNotOverwriteAGenuinePeak()
    {
        var air = Frame(10.9, false);
        var contact = Frame(11, true);
        contact.GForce = 1.8;
        var pause = Frame(11, true);
        pause.SimulatorState = TelemetrySimulatorState.Paused;
        pause.MotionSimulation = false;
        pause.GForce = 9;
        var resumed = Frame(111, true);
        resumed.AnalysisTimeSeconds = 11;
        resumed.GForce = .1;
        var next = Frame(111.01, true);
        next.AnalysisTimeSeconds = 11.01;
        var prepared = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(new[] { air, contact, pause, resumed, next }));
        Assert(prepared.Count == 3 && prepared[1].GForce == 1.8, "same-time resume replaced the genuine contact/G frame");
        Assert(TouchdownAnalysis.Analyze(prepared).Single().GAtFirstGroundSample == 1.8, "same-time resume changed measured contact G");
    }

    private static void SameTimeGroundChangeDoesNotInventAContact()
    {
        var pause = Frame(10, false);
        pause.SimulatorState = TelemetrySimulatorState.Paused;
        pause.MotionSimulation = false;
        var ground = Frame(110, true);
        ground.AnalysisTimeSeconds = 10;
        var raw = new[] { Frame(10, false), pause, ground, Frame(11, true) };
        Assert(TouchdownAnalysis.Analyze(raw).Count == 0, "zero-physics-time ground change became a landing");
    }

    private static void LifecycleSegmentsAndUnknownPhysicsRemainBarriers()
    {
        var first = Frame(10, false);
        var ground = Frame(11, true);
        ground.FlightSegment = 2;
        var later = Frame(12, true);
        later.FlightSegment = 2;
        var source = new[] { first, ground, later };
        var prepared = TelemetryAnalysisFrames.Prepare(source);
        Assert(prepared.Count == 4 && !prepared[1].MotionSimulation, "flight transition did not retain a barrier");
        Assert(TouchdownAnalysis.Analyze(prepared).Count == 0, "flight segment transition created a contact");
        Assert(prepared.Select(TelemetryCsv.Format).SequenceEqual(
            TelemetryAnalysisFrames.Prepare(prepared).Select(TelemetryCsv.Format)), "segment barriers are not idempotent");

        foreach (var state in new[] { TelemetrySimulatorState.Running, TelemetrySimulatorState.Inactive })
        {
            var stopped = Frame(10.5, false);
            stopped.SimulatorState = state;
            stopped.MotionSimulation = state == TelemetrySimulatorState.Inactive;
            Assert(TouchdownAnalysis.Analyze(new[] { first, stopped, Frame(11, true) }).Count == 0,
                "unconfirmed physics or inactive frame was stitched as a pause");
        }
    }

    private static void PauseBetweenBouncesPreservesBothContacts()
    {
        var source = new[] { Frame(10.9, false), Frame(11, true), Frame(11.1, true),
            Frame(11.2, false), Frame(11.4, true), Frame(11.5, true) };
        var paused = WithPause(source, 11.3);
        var contacts = TouchdownAnalysis.Analyze(paused);
        Assert(contacts.Count == 2 && contacts[0].EpisodeNumber == contacts[1].EpisodeNumber &&
            contacts[0].SimulationTimeSeconds == 11 && contacts[1].SimulationTimeSeconds == 11.4,
            "pause between bounces split or lost contacts");
    }

    private static void NewFlightContactsDoNotBecomeBouncesOfThePreviousFlight()
    {
        var firstAir = Frame(100, false);
        var firstContact = Frame(100.01, true);
        var secondAir = Frame(10, false);
        secondAir.FlightSegment = 2;
        var secondContact = Frame(10.01, true);
        secondContact.FlightSegment = 2;
        var contacts = TouchdownAnalysis.Analyze(new[] { firstAir, firstContact, secondAir, secondContact });
        Assert(contacts.Count == 2 && contacts[0].EpisodeNumber == 1 && contacts[1].EpisodeNumber == 2 &&
            contacts.All(contact => contact.ContactNumber == 1), "new flight was grouped as a bounce after a clock reset");
    }

    private static void ReplayScreeningPreservesSdkFlightOrder()
    {
        var firstFlight = new List<TelemetrySample>();
        var secondFlight = new List<TelemetrySample>();
        for (var i = 0; i <= 160; i++)
        {
            var normal = Frame(100 + i * .1, i >= 150);
            normal.LatitudeDegrees = 0;
            normal.LongitudeDegrees = 10 + i * .1 * 120 / (3600 * 60);
            normal.PlaneAltitudeFeet = 2000 - i;
            normal.GroundSpeedKnots = normal.IndicatedAirspeedKnots = 120;
            normal.VelocityWorldYFps = -10;
            firstFlight.Add(normal);

            var replay = Frame(10 + i * .1, i >= 150);
            replay.FlightSegment = 2;
            replay.LatitudeDegrees = normal.LatitudeDegrees;
            replay.LongitudeDegrees = normal.LongitudeDegrees;
            replay.PlaneAltitudeFeet = normal.PlaneAltitudeFeet;
            replay.GroundSpeedKnots = .1;
            replay.IndicatedAirspeedKnots = 1;
            replay.VelocityWorldYFps = -.2;
            secondFlight.Add(replay);
        }
        Assert(ReplayTelemetryDetector.IsReplayLike(secondFlight), "SDK replay fixture failed to detect replay");
        Assert(!ReplayTelemetryDetector.IsReplayLike(firstFlight.Concat(secondFlight).ToArray()),
            "replay screening reordered flight segments and rejected the earlier genuine landing");
        foreach (var sample in firstFlight.Concat(secondFlight)) sample.SimulatorState = TelemetrySimulatorState.Legacy;
        Assert(ReplayTelemetryDetector.IsReplayLike(firstFlight.Concat(secondFlight).ToArray()),
            "legacy replay ordering changed");
    }

    private static void FlightSegmentsCannotBorrowGeometryFromEachOther()
    {
        var fixture = typeof(SingleNoseGeometryRegressionTests).GetMethod("Trace", BindingFlags.Static | BindingFlags.NonPublic)!;
        var complete = (List<TelemetrySample>)fixture.Invoke(null, new object[] { false, false })!;
        var interrupted = ((List<TelemetrySample>)fixture.Invoke(null, new object[] { false, false })!)
            .Where(sample => sample.SimulationTimeSeconds <= .05).ToList();
        foreach (var sample in complete)
        {
            sample.SimulatorState = TelemetrySimulatorState.Running;
            sample.FlightSegment = 1;
            sample.AnalysisTimeSeconds = sample.SimulationTimeSeconds += 100;
        }
        foreach (var sample in interrupted)
        {
            sample.SimulatorState = TelemetrySimulatorState.Running;
            sample.FlightSegment = 2;
            sample.AnalysisTimeSeconds = sample.SimulationTimeSeconds += 10;
        }
        var expected = new[] { TouchdownAnalysis.Analyze(complete).Single(), TouchdownAnalysis.Analyze(interrupted).Single() };
        Assert(expected[0].ClosureReconstructionAvailable && !expected[1].ClosureReconstructionAvailable,
            "mixed-flight geometry fixture must include one complete and one uncalibratable landing");
        var actual = TouchdownAnalysis.Analyze(complete.Concat(interrupted).ToArray());
        Assert(actual.Count == 2 && actual[0].EpisodeNumber == 1 && actual[1].EpisodeNumber == 2,
            "mixed-flight analysis did not retain global episode numbering");
        for (var index = 0; index < actual.Count; index++)
        {
            foreach (var property in typeof(TouchdownResult).GetProperties())
            {
                if (property.Name == nameof(TouchdownResult.EpisodeNumber) || property.Name == nameof(TouchdownResult.Impact)) continue;
                Assert(Equals(property.GetValue(actual[index]), property.GetValue(expected[index])),
                    "mixed-flight result differs from isolated analysis: " + property.Name);
            }
            Assert(actual[index].Impact.Status == expected[index].Impact.Status &&
                actual[index].Impact.PeakG.Equals(expected[index].Impact.PeakG), "another flight changed the impact result");
        }
    }

    private static void InterruptedRolloutRetainsItsContact()
    {
        var source = Landing().Where(sample => sample.AnalysisTimeSeconds <= 11.1).ToList();
        var pause = Frame(11.1, true);
        pause.SimulatorState = TelemetrySimulatorState.Paused;
        pause.MotionSimulation = false;
        source.Add(pause);
        var menu = Frame(211.1, true);
        menu.SimulatorState = TelemetrySimulatorState.Inactive;
        menu.FlightSegment = 2;
        source.Add(menu);
        var contact = TouchdownAnalysis.Analyze(source).Single();
        Assert(contact.SimulationTimeSeconds == 11 && !contact.Impact.IsComplete,
            "main-menu exit after contact discarded the landing or certified an unfinished impact");
    }

    private static void ZeroIsAValidAnalysisTime()
    {
        var raw = Frame(300, false);
        raw.HostElapsedSeconds = 1000;
        raw.AnalysisTimeSeconds = 0;
        var prepared = TelemetryAnalysisFrames.Prepare(new[] { raw }).Single();
        Assert(TelemetryAnalysisFrames.TimeOf(prepared) == 0 && prepared.SimulationTimeSeconds == 0,
            "zero analysis time fell back to wall time");
        Assert(raw.SimulationTimeSeconds == 300 && raw.HostElapsedSeconds == 1000, "zero-time preparation changed source clocks");
    }

    private static List<TelemetrySample> Landing()
    {
        var samples = new List<TelemetrySample>();
        for (var i = -50; i <= 200; i++)
        {
            var sample = Frame(11 + i * .01, i >= 0);
            sample.VelocityWorldYFps = i < 0 ? -2 : -.5 * Math.Exp(-Math.Pow((i * .01 - .15) / .12, 2));
            sample.GForce = i < 0 ? 1 : 1 + .5 * Math.Exp(-Math.Pow((i * .01 - .35) / .08, 2));
            samples.Add(sample);
        }
        return samples;
    }

    private static List<TelemetrySample> WithPause(IReadOnlyList<TelemetrySample> source, double pauseAt)
    {
        var result = new List<TelemetrySample>();
        var inserted = false;
        foreach (var original in source)
        {
            if (!inserted && original.AnalysisTimeSeconds >= pauseAt)
            {
                var pause = Frame(pauseAt, result.Count > 0 && result[result.Count - 1].OnGround);
                pause.SimulatorState = TelemetrySimulatorState.Paused;
                pause.PauseFlags = 8;
                pause.MotionSimulation = false;
                pause.GForce = 9;
                result.Add(pause);
                var end = pause.CopyForAnalysis();
                end.SimulationTimeSeconds += 100;
                result.Add(end);
                inserted = true;
            }
            var copy = original.CopyForAnalysis();
            if (inserted) copy.SimulationTimeSeconds += 100;
            result.Add(copy);
        }
        return result;
    }

    private static TelemetrySample Frame(double time, bool onGround) => new TelemetrySample
    {
        Sequence = (long)Math.Round(time * 1000), HostElapsedSeconds = time + 500,
        SimulationTimeSeconds = time, AnalysisTimeSeconds = time,
        SimulationDeltaSeconds = .01, SimulatorState = TelemetrySimulatorState.Running,
        PauseFlags = 0, FlightSegment = 1, MotionSimulation = true, OnGround = onGround,
        TouchdownNormalVelocityFps = onGround ? 4 : 0, VerticalSpeedFps = -2, VelocityWorldYFps = -2,
        GForce = 1, AboveGroundLevelFeet = onGround ? 0 : 2, PlaneAltitudeFeet = 100,
        GroundAltitudeFeet = 100, GroundSpeedKnots = 100, IndicatedAirspeedKnots = 100,
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
