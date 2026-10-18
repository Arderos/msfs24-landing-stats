using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class PhysicsContinuityRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll()
    {
        NonMotionGroundFlickerNeverBecomesAContact();
        EnablingPhysicsOnTheGroundDoesNotImplyALanding();
        PauseThenGroundCannotBridgeAirborneEvidence();
        ResumeAirborneAllowsTheNextRealContact();
        PauseAfterARealContactDoesNotDeleteIt();
        BouncesRemainIndependentContacts();
        SameTimePhysicsBoundariesSurviveDeduplication();
        SameTimePauseCannotEraseAnEarlierRealContact();
        SameTimePauseCannotCertifyAnInterruptedImpact();
        PostPauseValuesCannotContaminateAnEarlierContact();
        ShortAirborneResumeCannotBorrowThePrePauseFit();
        ReplayHeuristicCannotBorrowPrePauseEvidence();
        SamePhysicsStateStillKeepsTheLatestDelivery();
        RecorderRetainsPauseEvidenceWithoutGrowingIdenticalFrames();
        RejectedOnlyCaptureDoesNotStartALanding();
        RejectedDiagnosticsDoNotResetRunwayMetadata();
        RawDebugStillReceivesNonMotionFrames();
    }

    private static void NonMotionGroundFlickerNeverBecomesAContact() => CheckBoth(
        "non-motion ground flicker",
        new[] { Frame(10, false, false), Frame(11, true, false), Frame(12, false, false), Frame(13, true, false) });

    private static void EnablingPhysicsOnTheGroundDoesNotImplyALanding() => CheckBoth(
        "ULLI-style physics activation on ground",
        new[] { Frame(10, false, false), Frame(11, false, false), Frame(12, true), Frame(13, true) });

    private static void PauseThenGroundCannotBridgeAirborneEvidence() => CheckBoth(
        "airborne pause followed by ground resume",
        new[] { Frame(10, false), Frame(11, false, false), Frame(12, true), Frame(13, true) });

    private static void ResumeAirborneAllowsTheNextRealContact() => CheckBoth(
        "airborne pause followed by airborne resume",
        new[] { Frame(10, false), Frame(11, false, false), Frame(12, false), Frame(13, true), Frame(14, true) }, 13);

    private static void PauseAfterARealContactDoesNotDeleteIt()
    {
        var samples = new[] { Frame(10, false), Frame(11, true), Frame(11.02, true, false), Frame(12, true), Frame(13, true) };
        CheckBoth("pause after a real contact", samples, 11);
        var contact = TouchdownAnalysis.Analyze(samples).Single();
        Assert(!contact.Impact.IsComplete, "interrupted G window was falsely certified complete");
    }

    private static void BouncesRemainIndependentContacts()
    {
        var samples = new[] { Frame(10.9, false), Frame(11, true), Frame(11.1, true), Frame(11.2, false), Frame(11.4, true), Frame(12, true) };
        CheckBoth("ordinary bounce", samples, 11, 11.4);
        var contacts = TouchdownAnalysis.Analyze(samples);
        Assert(contacts[0].EpisodeNumber == contacts[1].EpisodeNumber && contacts[0].ContactNumber == 1 && contacts[1].ContactNumber == 2,
            "ordinary bounce grouping changed");
        Assert(contacts[0].Impact.Status == TouchdownImpactStatus.Liftoff, "bounce no longer closes the first impact at liftoff");
    }

    private static void SameTimePhysicsBoundariesSurviveDeduplication()
    {
        var samples = new[] { Frame(10, false), Frame(11, false, false), Frame(11, true), Frame(12, true) };
        Assert(TelemetryDeduplicator.Deduplicate(samples).Count == samples.Length,
            "same-time off/on physics boundary was overwritten");
        CheckBoth("same-time off/on boundary", samples);

        // All three states can arrive at the same frozen simulator instant.
        CheckBoth("same-time air/off/ground boundary",
            new[] { Frame(10, false), Frame(10, false, false), Frame(10, true), Frame(11, true) });
    }

    private static void SameTimePauseCannotEraseAnEarlierRealContact()
    {
        var samples = new[] { Frame(10, false), Frame(11, true), Frame(11, true, false), Frame(11, true), Frame(12, true) };
        CheckBoth("contact followed by same-time pause/resume", samples, 11);
    }

    private static void SameTimePauseCannotCertifyAnInterruptedImpact()
    {
        var samples = new List<TelemetrySample>();
        for (var i = -50; i <= 200; i++)
        {
            var sample = Frame(11 + i * .01, i >= 0);
            sample.VelocityWorldYFps = i < 0 ? -2 : -.5 * Math.Exp(-Math.Pow((i * .01 - .15) / .12, 2));
            sample.GForce = i < 0 ? 1 : 1 + .5 * Math.Exp(-Math.Pow((i * .01 - .35) / .08, 2));
            samples.Add(sample);
        }
        Assert(TouchdownAnalysis.Analyze(samples).Single().Impact.IsComplete, "uninterrupted impact fixture did not complete");
        var pauseIndex = samples.FindIndex(sample => sample.SimulationTimeSeconds >= 11.1);
        var paused = Frame(samples[pauseIndex].SimulationTimeSeconds, true, false);
        samples.Insert(pauseIndex, paused);
        var result = TouchdownAnalysis.Analyze(TelemetryDeduplicator.Deduplicate(samples)).Single();
        Assert(result.Impact.Status == TouchdownImpactStatus.InvalidTelemetry,
            "impact's internal deduplication erased a same-time pause and certified its G window");
    }

    private static void SamePhysicsStateStillKeepsTheLatestDelivery()
    {
        var first = Frame(10, false);
        var stale = Frame(11, true);
        var latest = Frame(11, false);
        var contact = Frame(12, true);
        var samples = TelemetryDeduplicator.Deduplicate(new[] { first, stale, latest, contact });
        Assert(samples.Count == 3 && ReferenceEquals(samples[1], latest),
            "same-state deduplication did not retain the latest delivery");
        AssertContactTimes("latest same-state delivery", TouchdownAnalysis.Analyze(samples), new[] { 12.0 });
    }

    private static void PostPauseValuesCannotContaminateAnEarlierContact()
    {
        foreach (var pauseTime in new[] { 11.0, 11.02 })
        {
            var contact = Frame(11, true);
            contact.GForce = 1.2;
            var paused = Frame(pauseTime, true, false);
            paused.GForce = 9;
            paused.TouchdownNormalVelocityFps = 999;
            var resumed = Frame(pauseTime, true);
            resumed.GForce = 8;
            resumed.TouchdownNormalVelocityFps = 998;
            var result = TouchdownAnalysis.Analyze(TelemetryDeduplicator.Deduplicate(
                new[] { Frame(10.9, false), contact, paused, resumed })).Single();
            Assert(result.PeakG == 1.2, "legacy peak included a paused or post-pause load");
            Assert(result.LatchedNormalFpm == 240, "contact latch included a paused or post-pause update");
            Assert(!result.Impact.IsComplete, "interrupted contact received a complete impact classification");
        }
    }

    private static void ShortAirborneResumeCannotBorrowThePrePauseFit()
    {
        var samples = new List<TelemetrySample>();
        for (var i = 0; i <= 15; i++)
        {
            var beforePause = Frame(10 + i * .02, false);
            beforePause.VelocityWorldYFps = -100 + i;
            samples.Add(beforePause);
        }
        samples.Add(Frame(10.32, false, false));
        samples.Add(Frame(10.34, false));
        samples.Add(Frame(10.38, true));
        var contact = TouchdownAnalysis.Analyze(samples).Single();
        Assert(!contact.InertialVerticalExtrapolated, "short resumed flight borrowed fit samples from before the pause");
        Assert(contact.InertialVerticalFpm == 240, "short resumed flight did not use its own last airborne velocity");
        Assert(contact.InertialFitDurationSeconds < .15, "fit duration silently spans an interruption");
    }

    private static void ReplayHeuristicCannotBorrowPrePauseEvidence()
    {
        var samples = new List<TelemetrySample>();
        for (var i = 0; i <= 160; i++)
        {
            var sample = Frame(10 + i * .1, i >= 150);
            sample.LatitudeDegrees = 0;
            sample.LongitudeDegrees = 10 + i * .1 * 120 / (3600 * 60);
            sample.PlaneAltitudeFeet = 2000 - i;
            sample.GroundSpeedKnots = .1;
            sample.IndicatedAirspeedKnots = 1;
            sample.VelocityWorldYFps = -.2;
            samples.Add(sample);
        }
        Assert(ReplayTelemetryDetector.IsReplayLike(samples), "replay-like baseline fixture did not trigger the heuristic");
        samples[148].MotionSimulation = false;
        Assert(TouchdownAnalysis.Analyze(samples).Count == 1, "valid airborne resume did not permit its real contact");
        Assert(!ReplayTelemetryDetector.IsReplayLike(samples), "old pre-pause replay evidence rejected a resumed real contact");
    }

    private static void RecorderRetainsPauseEvidenceWithoutGrowingIdenticalFrames()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        LandingEpisodeEventArgs? diagnostic = null;
        recorder.EpisodeCompleted += (_, args) => diagnostic = args;
        Feed(recorder, Frame(10, false));
        for (var i = 0; i < 1000; i++) Feed(recorder, Frame(10, false, false));
        Feed(recorder, Frame(10, true));
        RecorderTestAccess.Call(recorder, "FlushRejectedContacts", null);
        Assert(diagnostic != null && diagnostic.IsDiagnosticOnly, "rejected contact did not preserve a diagnostic-only capture");
        Assert(diagnostic!.RunwayFacilitiesTask.IsCompleted && diagnostic.RunwayFacilitiesTask.Result.Count == 0,
            "diagnostic-only capture waits for landing runway facilities");
        var samples = diagnostic.Samples;
        Assert(samples.Count == 3 && samples[0].MotionSimulation && !samples[1].MotionSimulation && samples[2].MotionSimulation,
            "recorder lost a physics boundary or retained every duplicate pause frame");
        Assert(TouchdownAnalysis.Analyze(TelemetryDeduplicator.Deduplicate(samples)).Count == 0,
            "recorder coalescing recreated a contact across a pause");
        Assert(RecorderTestAccess.Get(recorder, "_lastContactTime") == null,
            "invalid contact restarted the recorder's post-contact timer");
    }

    private static void RejectedOnlyCaptureDoesNotStartALanding()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        var started = 0;
        var captures = new List<LandingEpisodeEventArgs>();
        recorder.EpisodeStarted += (_, _) => started++;
        recorder.EpisodeCompleted += (_, args) => captures.Add(args);
        Feed(recorder, Frame(10, false, false));
        Feed(recorder, Frame(11, true, false));
        Assert(started == 0, "inactive ground flicker announced a real landing episode");
        RecorderTestAccess.Call(recorder, "FlushRejectedContacts", null);
        Assert(captures.Count == 0, "inactive ground flicker created an automatic capture without flight evidence");
    }

    private static void RawDebugStillReceivesNonMotionFrames()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        RecorderTestAccess.Set(recorder, "_rawDebugEnabled", true);
        var received = new List<TelemetrySample>();
        recorder.RawDebugSampleReceived += (_, args) => received.Add(args.Sample);
        var samples = new[] { Frame(10, false, false), Frame(11, true, false), Frame(12, true) };
        foreach (var sample in samples) Feed(recorder, sample);
        Assert(received.SequenceEqual(samples), "contact filtering discarded or changed raw diagnostic deliveries");
    }

    private static void RejectedDiagnosticsDoNotResetRunwayMetadata()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        var previous = new[] { new RunwayFacility { AirportIdent = "TEST", PrimaryName = "09" } };
        var pending = new TaskCompletionSource<IReadOnlyList<RunwayFacility>>();
        RecorderTestAccess.Set(recorder, "_runwayEpisodeId", 42L);
        var sourceCache = (Dictionary<string, IReadOnlyList<RunwayFacility>>)RecorderTestAccess.Get(recorder.NativeSource, "_runways")!;
        sourceCache["TEST"] = previous;
        RecorderTestAccess.Set(recorder.NativeSource, "_runwayBatchKeys", new[] { "TEST" });
        RecorderTestAccess.Set(recorder, "_runwaySnapshotCompletion", pending);
        LandingEpisodeEventArgs? diagnostic = null;
        recorder.EpisodeCompleted += (_, args) => diagnostic = args;
        var airborne = Frame(9, false);
        airborne.AboveGroundLevelFeet = 700; // Flight evidence without starting an approach/runway lookup.
        Feed(recorder, airborne);
        Feed(recorder, Frame(10, false, false));
        Feed(recorder, Frame(11, true, false));
        RecorderTestAccess.Call(recorder, "FlushRejectedContacts", null);
        Assert(diagnostic != null && diagnostic.IsDiagnosticOnly && diagnostic.RunwayFacilitiesTask.IsCompleted &&
               diagnostic.RunwayFacilitiesTask.Result.Count == 0, "diagnostic capture inherited a real landing's runway task");
        Assert(recorder.GetRunwayFacilities(42).Count == 1 && recorder.GetRunwayFacilities(42)[0].AirportIdent == "TEST" && !pending.Task.IsCompleted,
            "rejected diagnostic capture reset or completed another landing's runway metadata");
    }

    private static void CheckBoth(string name, TelemetrySample[] samples, params double[] expectedTimes)
    {
        AssertContactTimes(name + " direct", TouchdownAnalysis.Analyze(samples), expectedTimes);
        AssertContactTimes(name + " deduplicated", TouchdownAnalysis.Analyze(TelemetryDeduplicator.Deduplicate(samples)), expectedTimes);

        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        var captures = new List<LandingEpisodeEventArgs>();
        recorder.EpisodeCompleted += (_, args) => captures.Add(args);
        foreach (var sample in samples) Feed(recorder, sample);
        RecorderTestAccess.Call(recorder, "CompleteEpisode", new object[] { false });
        RecorderTestAccess.Call(recorder, "FlushRejectedContacts", null);
        var recorderContacts = captures.SelectMany(capture => capture.DecisionJournal!.Snapshot().Entries)
            .Where(entry => entry.Code == "contact_flag_transition").Select(entry => entry.TimeSeconds).ToArray();
        Assert(recorderContacts.SequenceEqual(expectedTimes), name + ": recorder accepted the wrong contact sequence");
        var analyzed = captures.SelectMany(capture => TouchdownAnalysis.Analyze(TelemetryDeduplicator.Deduplicate(capture.Samples))).ToArray();
        AssertContactTimes(name + " captured", analyzed, expectedTimes);
        if (expectedTimes.Length == 0)
            Assert(captures.All(capture => capture.IsDiagnosticOnly), name + ": invalid-only capture was marked as a real landing");
    }

    private static TelemetrySample Frame(double time, bool ground, bool motion = true) => new TelemetrySample
    {
        SimulationTimeSeconds = time, HostElapsedSeconds = time, MotionSimulation = motion, OnGround = ground,
        AboveGroundLevelFeet = ground ? 3 : 400, VelocityWorldYFps = ground ? 0 : -4,
        VerticalSpeedFps = ground ? 0 : -4, GForce = 1, PlaneAltitudeFeet = 1000,
        GroundAltitudeFeet = 600, GroundSpeedKnots = 120, IndicatedAirspeedKnots = 120,
        TouchdownNormalVelocityFps = ground ? 4 : 0
    };

    private static void AssertContactTimes(string name, IReadOnlyList<TouchdownResult> contacts, IReadOnlyList<double> expectedTimes) =>
        Assert(contacts.Select(contact => contact.SimulationTimeSeconds).SequenceEqual(expectedTimes), name + ": unexpected contacts");

    private static void Feed(SimConnectLandingRecorder recorder, TelemetrySample sample) =>
        RecorderTestAccess.Call(recorder, "ProcessSample", new object[] { sample });

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
