using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class ContactSourceContinuityRegressionTests
{
    public static void RunAll()
    {
        ControlledPhysicsCannotCreateContacts();
        ReleaseNeedsAnObservedAirborneInterval();
        UnknownObservationsAndSourceChangesCannotBridgeRelease();
        ExistingContactsAndShortBouncesSurvive();
        LegacyAndStaleEvidenceRemainUnknown();
        LiveAndOfflineAgreeAcrossRecovery();
        PausedSourceControlSurvivesResume();
        PausedCoalescingRetainsSourceBoundaries();
        LegacyCheckpointRemainsReadable();
    }

    private static void ControlledPhysicsCannotCreateContacts()
    {
        foreach (var mask in new[] { 1, 2, 4, 7 })
        {
            Check(Count(Frame(0), Frame(.01, true, mask)) == 0, "A frozen touchdown was accepted.");
            Check(Count(Frame(0, false, mask), Frame(.01, true)) == 0, "A frozen airborne witness was accepted.");
        }
        Check(Count(Frame(0, slew: 1), Frame(.01, true)) == 0, "Slew established an airborne witness.");
        Check(Count(Frame(0, sdk: SdkReplayState.Active), Frame(.01, true)) == 0, "SDK replay established an airborne witness.");
    }

    private static void ReleaseNeedsAnObservedAirborneInterval()
    {
        var start = new[] { Frame(0, true), Frame(.1, mask: 7), Frame(.2, mask: 7) };
        Check(Count(start.Concat(new[] { Frame(.3), Frame(.31, true) }).ToArray()) == 0,
            "The release boundary's single air point created a touchdown.");
        Check(Count(start.Concat(new[] { Frame(.3), Frame(.3), Frame(.31, true) }).ToArray()) == 0,
            "A repeated physics timestamp manufactured an airborne interval.");
        Check(Count(start.Concat(new[] { Frame(.3), Frame(.299), Frame(.31, true) }).ToArray()) == 0,
            "A reversed timestamp manufactured an airborne interval.");
        Check(Count(start.Concat(new[] { Frame(.3), Frame(.30001), Frame(.30002, true) }).ToArray()) == 1,
            "A real released airborne interval was subject to a duration or speed gate.");
        Check(Count(start.Concat(new[] { Frame(.3, true), Frame(.4), Frame(.41), Frame(.42, true) }).ToArray()) == 1,
            "Grounded release permanently disabled a subsequent real departure.");
    }

    private static void UnknownObservationsAndSourceChangesCannotBridgeRelease()
    {
        var missing = Frame(.3); missing.SourceEvidence = null;
        var stale = Frame(.3, observationTime: -2);
        var oldRelease = Frame(.3, observationTime: .1);
        foreach (var uncertain in new[] { missing, stale, oldRelease })
            Check(Count(Frame(0, mask: 7), Frame(.2, mask: 7), uncertain, Frame(.4, true)) == 0,
                "Unknown or pre-interruption source data released a frozen contact.");
        Check(Count(Frame(0, mask: 7), Frame(.2), Frame(.3, generation: 2), Frame(.4, true, generation: 2)) == 0,
            "An airborne interval spanned different source generations.");
        Check(Count(Frame(0, mask: 7), Frame(.2), Frame(.3, generation: 2), Frame(.31, generation: 2), Frame(.4, true, generation: 2)) == 1,
            "Fresh physical flight from a new source could not recover.");
    }

    private static void ExistingContactsAndShortBouncesSurvive()
    {
        Check(Count(Frame(0), Frame(.01, true), Frame(.02), Frame(.03, true)) == 2,
            "Ordinary short zero-groundspeed bounces changed.");
        var samples = new[] { Frame(0), Frame(.01, true), Frame(.02, mask: 7), Frame(.03, true, mask: 7),
            Frame(.04), Frame(.05), Frame(.06, true) };
        Check(Count(samples) == 2, "A later freeze removed an accepted contact or valid resumed contact.");
        Check(TouchdownAnalysis.Analyze(samples).Count == 2, "Public analysis and contact continuity disagree.");
    }

    private static void LegacyAndStaleEvidenceRemainUnknown()
    {
        var legacy = new[] { Frame(0), Frame(.01, true) };
        foreach (var sample in legacy) sample.SourceEvidence = null;
        Check(Count(legacy) == 1, "Recordings without optional source metadata changed.");
        Check(Count(Frame(1, mask: 7, observationTime: -2), Frame(1.01, true)) == 1,
            "Stale freeze was treated as current evidence.");
    }

    private static void LiveAndOfflineAgreeAcrossRecovery()
    {
        foreach (var validInterval in new[] { false, true })
        {
            var samples = new List<TelemetrySample> { Frame(0, true), Frame(.1, true), Frame(.2, mask: 7), Frame(.3, mask: 7), Frame(.4) };
            if (validInterval) samples.Add(Frame(.41));
            samples.Add(Frame(.42, true));
            for (var i = 1; i <= 20; i++) samples.Add(Frame(.42 + i, true));
            for (var restoreAt = -1; restoreAt < 7; restoreAt++)
            {
                var harness = new ReplayRecordingRegressionTests.Harness();
                for (var index = 0; index < samples.Count; index++)
                {
                    harness.Feed(samples[index].CopyForAnalysis());
                    if (index == restoreAt)
                    {
                        var before = harness.Coordinator.CaptureCheckpoint();
                        harness.Restore(before);
                        Check(before.SequenceEqual(harness.Coordinator.CaptureCheckpoint()), "Source-contact checkpoint changed on restoration.");
                    }
                }
                harness.Coordinator.Stop();
                var liveContacts = harness.Captures.Where(capture => !capture.IsDiagnosticOnly)
                    .Sum(capture => TouchdownAnalysis.Analyze(capture.Samples).Count);
                Check(liveContacts == (validInterval ? 1 : 0), "Live contact outcome changed across source recovery.");
                Check(TouchdownAnalysis.Analyze(samples).Count == liveContacts, "Live and offline source-contact policies diverged.");
                Check(!ReplayTelemetryDetector.HasReplaySourceEvidence(samples), "A static freeze was relabelled replay.");
                if (!validInterval)
                    Check(harness.Captures.Any(capture => capture.IsDiagnosticOnly), "Rejected contact lost its raw diagnostic episode.");
            }
        }
    }

    private static void LegacyCheckpointRemainsReadable()
    {
        var harness = new ReplayRecordingRegressionTests.Harness();
        harness.Feed(Frame(0));
        var current = CumulativeGroundDepartureRegressionTests.RemoveCumulativeTail(
            harness.Coordinator.CaptureCheckpoint());
        // Version 6 appends four flags for empty grounded history. Version 4
        // also predates the three absent source-continuity flags from version 5.
        foreach (var version in new[] { 0x3443454c, 0x3543454c, 0x3643454c })
        {
            var legacy = current.Take(current.Length - (version == 0x3443454c ? 7 : version == 0x3543454c ? 4 : 0)).ToArray();
            Array.Copy(BitConverter.GetBytes(version), legacy, 4);
            harness.Restore(legacy);
            harness.Feed(Frame(.01, true));
            Check(harness.Coordinator.HasContact, "An older airborne checkpoint no longer accepts an ordinary contact.");
        }
    }

    private static void PausedSourceControlSurvivesResume()
    {
        foreach (var mask in new[] { 1, 7 })
        foreach (var restore in new[] { false, true })
        foreach (var validInterval in new[] { false, true })
        {
            var harness = new ReplayRecordingRegressionTests.Harness();
            harness.Feed(Frame(0, true));
            harness.Feed(Frame(.1));
            harness.Coordinator.SetSdkPauseFlags(1);
            harness.Feed(Frame(.2, mask: mask));
            if (restore) harness.Restore(harness.Coordinator.CaptureCheckpoint());
            harness.Coordinator.SetSdkPauseFlags(0);
            harness.Feed(Frame(.3));
            if (validInterval) harness.Feed(Frame(.31));
            harness.Feed(Frame(.32, true));
            Check(harness.Coordinator.HasContact == validInterval,
                "Source control observed during pause was lost, or a released interval could not resume contact detection.");
            harness.Coordinator.Stop();
        }
    }

    private static void PausedCoalescingRetainsSourceBoundaries()
    {
        var harness = new ReplayRecordingRegressionTests.Harness();
        harness.Feed(Frame(0, true));
        harness.Feed(Frame(.1));
        harness.Coordinator.SetSdkPauseFlags(1);
        harness.Feed(Frame(.2));
        harness.Feed(Frame(.3, mask: 1));
        for (var index = 0; index < 1000; index++)
        {
            var paused = Frame(.4 + index * .001, ground: index % 2 == 0);
            paused.MotionSimulation = index % 3 == 0;
            harness.Feed(paused);
        }
        harness.Restore(harness.Coordinator.CaptureCheckpoint());
        harness.Coordinator.SetSdkPauseFlags(0);
        harness.Feed(Frame(1.5));
        harness.Feed(Frame(1.51, true));
        harness.Coordinator.Stop();
        var capture = harness.Captures.Single();
        Check(capture.IsDiagnosticOnly && TouchdownAnalysis.Analyze(capture.Samples).Count == 0,
            "Pause coalescing erased source interruption and recreated a contact offline.");
        Check(capture.Samples.Any(sample => TelemetryContactPhysics.IsBlocked(sample)),
            "The middle paused freeze observation disappeared from recorded evidence.");
        Check(capture.Samples.Count < 12, "Unchanged paused source state retained every stale motion/ground flag update.");
    }

    private static int Count(params TelemetrySample[] samples)
    {
        var continuity = new TelemetryContactContinuity();
        return samples.Count(sample => continuity.Observe(sample));
    }

    private static TelemetrySample Frame(double time, bool ground = false, int mask = 0, int slew = 0,
        SdkReplayState sdk = SdkReplayState.Unknown, long generation = 1, double? observationTime = null)
    {
        const long frequency = 1000000;
        var frameQpc = 10000000 + (long)Math.Round(time * frequency);
        var observedQpc = 10000000 + (long)Math.Round((observationTime ?? time) * frequency);
        return new TelemetrySample
        {
            HostElapsedSeconds = time, SimulationTimeSeconds = time, SimulationDeltaSeconds = .01,
            MotionSimulation = true, OnGround = ground, LatitudeDegrees = 42, LongitudeDegrees = 23,
            AboveGroundLevelFeet = ground ? 0 : 5, PlaneAltitudeFeet = 604, GroundAltitudeFeet = 600,
            GroundSpeedKnots = 0, IndicatedAirspeedKnots = 0, VelocityWorldYFps = ground ? 0 : -4,
            TouchdownNormalVelocityFps = ground ? 4 : 0, GForce = ground ? 1.2 : 1,
            SourceEvidence = new ReplaySourceEvidence(ReplaySourceCoreRegressionTests.Worker, generation, 1,
                observedQpc, frameQpc, frequency, sdk, new ReplaySourceObservation(SourceObservationAvailability.Available,
                    SourceObservationReason.None, mask, slew, 1, simulationTime: observationTime ?? time,
                    latitude: 42, longitude: 23, altitudeFeet: 604, pitch: 0, bank: 0, heading: 180))
        };
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
