using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class KinematicDiscontinuityRegressionTests
{
    public static void RunAll()
    {
        RecordedFa18ReplacementIsNotFlightMotion();
        RelocationCannotInventGroundContact();
        ExistingContactSurvivesWithoutBorrowingReplacementForces();
        SegmentsNeverShareContactNumberingOrRawSeries();
        LegitimateFastSlowAndVerticalFlightRemainContinuous();
        PausedRowsRemainInRawSelectionButNotThePhysicsClock();
        PreparedInputIsIdempotentAndRetainsBoundaryEvidence();
        MissingChannelsDoNotInventRelocation();
        LegacyDuplicateSequencesUseContactTime();
        PausedOffsetAcrossRelocationUsesAnalysisClock();
        LegacyClockResetSplitsFlightsWithoutInventingContact();
        UnknownInactivePhysicsSplitsButConfirmedPauseJoins();
        GeometryInputsAreSplitBeforeOptionsAreRecovered();
    }

    private static void RecordedFa18ReplacementIsNotFlightMotion()
    {
        var before = Frame(70.247983889537863, true);
        before.LatitudeDegrees = 39.413062931764912;
        before.LongitudeDegrees = -117.05406005370118;
        before.PlaneAltitudeFeet = 7790.3938313122899;
        before.VelocityWorldYFps = 0;
        before.GroundSpeedKnots = 0;
        before.GForce = 7.5012255699423047;
        var after = Frame(70.26621629006695, false);
        after.LatitudeDegrees = 39.436049706593451;
        after.LongitudeDegrees = -117.02867400758909;
        after.PlaneAltitudeFeet = 8903.4220115375756;
        after.VelocityWorldYFps = -.26407582323927231;
        after.GroundSpeedKnots = 532.2844798659529;
        after.GForce = 7.494407355128442;
        Assert(TelemetryKinematicDiscontinuity.TryDetect(before, after, out var boundary) &&
            boundary!.HorizontalDistanceMeters > 3300 && boundary.HorizontalAllowanceMeters < 200,
            "recorded FA18 relocation was treated as normal motion");
    }

    private static void RelocationCannotInventGroundContact()
    {
        var samples = new[] { Frame(10, false), Frame(10.02, true, 3000), Frame(10.04, true, 3001) };
        Assert(TouchdownAnalysis.Analyze(samples).Count == 0, "teleport onto the ground created a landing");
    }

    private static void ExistingContactSurvivesWithoutBorrowingReplacementForces()
    {
        var samples = TwoFlights();
        var first = TouchdownAnalysis.Analyze(samples).First();
        Assert(first.SimulationTimeSeconds == 10.02 && first.Impact.Status == TouchdownImpactStatus.InvalidTelemetry &&
            !first.Impact.IsComplete, "relocation was misclassified as a genuine bounce/liftoff or erased the contact");
        Assert(first.PeakG == 1.5 && first.Impact.PeakG == 1.5 && first.LatchedNormalFpm == 240,
            "replacement-flight G or touchdown latch contaminated the first contact");
        Assert(samples.All(sample => sample.MotionSimulation && sample.AnalysisDiscontinuity == null),
            "analysis modified the original raw observations");
    }

    private static void SegmentsNeverShareContactNumberingOrRawSeries()
    {
        var samples = TwoFlights();
        var contacts = TouchdownAnalysis.Analyze(samples);
        Assert(contacts.Count == 2 && contacts[0].EpisodeNumber != contacts[1].EpisodeNumber &&
            contacts.All(contact => contact.ContactNumber == 1), "replacement-flight contacts merged into one bounce episode");
        var first = TelemetryKinematicDiscontinuity.SelectContactSegment(samples,
            contacts[0].Sequence, contacts[0].SimulationTimeSeconds);
        var second = TelemetryKinematicDiscontinuity.SelectContactSegment(samples,
            contacts[1].Sequence, contacts[1].SimulationTimeSeconds);
        Assert(first.Count == 3 && second.Count == 3 && first.All(sample => sample.SimulationTimeSeconds < 10.06) &&
            second.All(sample => sample.SimulationTimeSeconds >= 10.06), "raw 3D/control series crosses relocation");
        Assert(first.Concat(second).All(sample => samples.Contains(sample) && sample.AnalysisDiscontinuity == null),
            "raw segment selection returned manufactured analysis frames");
    }

    private static void LegitimateFastSlowAndVerticalFlightRemainContinuous()
    {
        foreach (var knots in new[] { 0.0, .01, 30, 140, 600, 1600 })
        foreach (var dt in new[] { .01, .5, 10.0 })
        {
            var first = Frame(10, false);
            var second = Frame(10 + dt, false, knots * 1852 / 3600 * dt);
            first.GroundSpeedKnots = second.GroundSpeedKnots = knots;
            Assert(!TelemetryKinematicDiscontinuity.TryDetect(first, second, out _), "physically explained ground speed was rejected");
        }
        var hovering = Frame(10, false);
        var waterContact = Frame(10.02, true);
        hovering.GroundSpeedKnots = waterContact.GroundSpeedKnots = 0;
        waterContact.PlaneAltitudeFeet = hovering.PlaneAltitudeFeet - .08;
        Assert(TouchdownAnalysis.Analyze(new[] { hovering, waterContact }).Count == 1,
            "zero-speed helicopter/water contact was rejected");
        var bounce = new[] { Frame(10, false), Frame(10.02, true), Frame(10.04, false), Frame(10.06, true) };
        var contacts = TouchdownAnalysis.Analyze(bounce);
        Assert(contacts.Count == 2 && contacts[0].EpisodeNumber == contacts[1].EpisodeNumber &&
            contacts[0].Impact.Status == TouchdownImpactStatus.Liftoff, "genuine bounce was split as relocation");
    }

    private static void PausedRowsRemainInRawSelectionButNotThePhysicsClock()
    {
        var air = Frame(10, false);
        air.SimulatorState = TelemetrySimulatorState.Running;
        var paused = Frame(40, false);
        paused.SimulatorState = TelemetrySimulatorState.Paused;
        paused.AnalysisTimeSeconds = 10;
        paused.GForce = 9;
        var contact = Frame(40.02, true);
        contact.SimulatorState = TelemetrySimulatorState.Running;
        contact.AnalysisTimeSeconds = 10.02;
        var raw = new[] { air, paused, contact };
        var result = TouchdownAnalysis.Analyze(raw).Single();
        Assert(result.SimulationTimeSeconds == 10.02, "confirmed pause changed contact active time");
        var selected = TelemetryKinematicDiscontinuity.SelectContactSegment(raw, result.Sequence, result.SimulationTimeSeconds);
        Assert(selected.Count == 3 && ReferenceEquals(selected[1], paused), "raw selector deleted the first half or paused evidence");
    }

    private static void PreparedInputIsIdempotentAndRetainsBoundaryEvidence()
    {
        var raw = TwoFlights();
        var prepared = TelemetryAnalysisFrames.Prepare(raw);
        var twice = TelemetryAnalysisFrames.Prepare(prepared);
        Assert(prepared.Count == raw.Length + 1 && twice.Count == prepared.Count &&
            twice.Count(sample => sample.AnalysisDiscontinuity != null) == 1, "preparing frames duplicates or loses relocation barrier");
        var expected = TouchdownAnalysis.Analyze(raw);
        var actual = TouchdownAnalysis.Analyze(twice);
        Assert(expected.Count == actual.Count && expected.Zip(actual, (a, b) =>
            a.EpisodeNumber == b.EpisodeNumber && a.Impact.Status == b.Impact.Status && a.PeakG == b.PeakG).All(equal => equal),
            "an already-prepared input hides its physical segmentation");
    }

    private static void MissingChannelsDoNotInventRelocation()
    {
        var first = Frame(10, false);
        var second = Frame(10.02, true);
        first.LatitudeDegrees = first.LongitudeDegrees = 0;
        Assert(!TelemetryKinematicDiscontinuity.TryDetect(first, second, out _), "missing legacy position was treated as a teleport");
        first.MotionSimulation = false;
        second.LongitudeDegrees = 100;
        second.PlaneAltitudeFeet = 20000;
        Assert(!TelemetryKinematicDiscontinuity.TryDetect(first, second, out _), "inactive scenery setup was declared measured flight motion");
    }

    private static void LegacyDuplicateSequencesUseContactTime()
    {
        var samples = TwoFlights();
        foreach (var sample in samples) sample.Sequence = 0;
        var second = TelemetryKinematicDiscontinuity.SelectContactSegment(samples, 0, 10.08);
        Assert(second.Count == 3 && ReferenceEquals(second[0], samples[3]), "legacy Sequence=0 selected the wrong flight");
    }

    private static void PausedOffsetAcrossRelocationUsesAnalysisClock()
    {
        var samples = TwoFlights();
        foreach (var sample in samples)
        {
            sample.SimulatorState = TelemetrySimulatorState.Running;
            sample.AnalysisTimeSeconds = sample.SimulationTimeSeconds;
            if (sample.SimulationTimeSeconds >= 10.06) sample.SimulationTimeSeconds += 30;
        }
        var paused = Frame(40.04, true);
        paused.SimulatorState = TelemetrySimulatorState.Paused;
        paused.AnalysisTimeSeconds = 10.04;
        var raw = samples.Take(3).Concat(new[] { paused }).Concat(samples.Skip(3)).ToArray();
        var contacts = TouchdownAnalysis.Analyze(raw);
        Assert(contacts.Count == 2 && Math.Abs(contacts[1].SimulationTimeSeconds - 10.08) < 1e-9,
            "normalized contact time did not preserve the pause offset");
        var second = TelemetryKinematicDiscontinuity.SelectContactSegment(raw,
            contacts[1].Sequence, contacts[1].SimulationTimeSeconds);
        Assert(second.Count == 3 && ReferenceEquals(second[0], samples[3]) &&
            second[1].SimulationTimeSeconds > 40 && second[1].AnalysisTimeSeconds < 11,
            "raw contact segment was searched with the wrong clock or its timestamps were rewritten");
        var first = TelemetryKinematicDiscontinuity.SelectContactSegment(raw,
            contacts[0].Sequence, contacts[0].SimulationTimeSeconds);
        Assert(first.Contains(paused), "raw pause evidence disappeared while isolating relocation");
    }

    private static void LegacyClockResetSplitsFlightsWithoutInventingContact()
    {
        var samples = new[] { Frame(100, false), Frame(101, true), Frame(0, false), Frame(.02, true) };
        var contacts = TouchdownAnalysis.Analyze(samples);
        Assert(contacts.Count == 2 && contacts[0].EpisodeNumber != contacts[1].EpisodeNumber &&
            contacts.All(contact => contact.ContactNumber == 1), "legacy clock reset merged unrelated flights");
        var second = TelemetryKinematicDiscontinuity.SelectContactSegment(samples,
            contacts[1].Sequence, contacts[1].SimulationTimeSeconds);
        Assert(second.Count == 2 && ReferenceEquals(second[0], samples[2]), "legacy clock reset mixed stored series");
        Assert(TouchdownAnalysis.Analyze(new[] { Frame(100, false), Frame(0, true) }).Count == 0,
            "clock reset directly onto the ground invented a contact");
    }

    private static void UnknownInactivePhysicsSplitsButConfirmedPauseJoins()
    {
        var stopped = Frame(10.04, true);
        stopped.MotionSimulation = false;
        var samples = new[] { Frame(10, false), Frame(10.02, true), stopped,
            Frame(10.06, false), Frame(10.08, true) };
        var interrupted = TouchdownAnalysis.Analyze(samples);
        Assert(interrupted.Count == 2 && interrupted[0].EpisodeNumber != interrupted[1].EpisodeNumber,
            "unexplained inactive physics reused another flight's contact episode");
        var second = TelemetryKinematicDiscontinuity.SelectContactSegment(samples,
            interrupted[1].Sequence, interrupted[1].SimulationTimeSeconds);
        Assert(second.Count == 2 && ReferenceEquals(second[0], samples[3]), "unknown inactive physics mixed stored series");
        stopped.SimulatorState = TelemetrySimulatorState.Paused;
        foreach (var sample in samples.Where(sample => sample != stopped)) sample.SimulatorState = TelemetrySimulatorState.Running;
        var paused = TouchdownAnalysis.Analyze(samples);
        Assert(paused.Count == 2 && paused[0].EpisodeNumber == paused[1].EpisodeNumber,
            "confirmed pause broke a genuine bounced landing into separate flights");
        var retained = TelemetryKinematicDiscontinuity.SelectContactSegment(samples,
            paused[1].Sequence, paused[1].SimulationTimeSeconds);
        Assert(retained.Count == samples.Length, "confirmed pause truncated the original raw flight");
    }

    private static void GeometryInputsAreSplitBeforeOptionsAreRecovered()
    {
        var jump = TwoFlights();
        var stopped = Frame(10.04, true);
        stopped.MotionSimulation = false;
        var inactive = new[] { Frame(10, false), Frame(10.02, true), stopped,
            Frame(10.06, false), Frame(10.08, true) };
        var clockReset = new[] { Frame(100, false), Frame(101, true), Frame(0, false), Frame(.02, true) };
        foreach (var raw in new[] { jump, inactive, clockReset })
        {
            var groups = TelemetryKinematicDiscontinuity.SplitPhysicalSegments(raw);
            Assert(groups.Count == 2 && groups.All(group => group.Count > 0) &&
                groups.SelectMany(group => group).SequenceEqual(raw),
                "geometry input split dropped original rows or mixed physical segments");
            var preparedGroups = TelemetryKinematicDiscontinuity.SplitPhysicalSegments(TelemetryAnalysisFrames.Prepare(raw));
            Assert(preparedGroups.Count == 2 && preparedGroups.SelectMany(group => group).All(sample =>
                sample.AnalysisDiscontinuity == null), "geometry helper leaked manufactured boundary rows");
        }
        stopped.SimulatorState = TelemetrySimulatorState.Paused;
        Assert(TelemetryKinematicDiscontinuity.SplitPhysicalSegments(inactive).Count == 1,
            "SDK-confirmed pause split geometry recovery");
    }

    private static TelemetrySample[] TwoFlights()
    {
        var samples = new[] { Frame(10, false), Frame(10.02, true), Frame(10.04, true),
            Frame(10.06, false, 3000), Frame(10.08, true, 3001), Frame(10.10, true, 3002) };
        samples[1].GForce = 1.4;
        samples[2].GForce = 1.5;
        for (var i = 3; i < samples.Length; i++)
        {
            samples[i].GForce = 9;
            samples[i].TouchdownNormalVelocityFps = 999;
        }
        return samples;
    }

    private static TelemetrySample Frame(double time, bool ground, double northMeters = 0) => new TelemetrySample
    {
        Sequence = (long)Math.Round(time * 100), SimulationTimeSeconds = time, HostElapsedSeconds = time,
        SimulationDeltaSeconds = .02, OnGround = ground, MotionSimulation = true,
        AboveGroundLevelFeet = ground ? 3 : 4, PlaneAltitudeFeet = ground ? 603 : 604, GroundAltitudeFeet = 600,
        LatitudeDegrees = 42 + northMeters / 111195, LongitudeDegrees = 23,
        GroundSpeedKnots = 120, IndicatedAirspeedKnots = 120, VelocityWorldYFps = ground ? 0 : -4,
        GForce = 1, TouchdownNormalVelocityFps = ground ? 4 : 0,
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
