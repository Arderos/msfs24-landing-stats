using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.RegressionTests;

internal static class PauseRecorderRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll()
    {
        PausesPreserveApproachContactAndRollout();
        ActivePauseWithMotionFlagDoesNotAddMeasurements();
        PauseBetweenBouncesPreservesTheEpisode();
        EscapeThenMainMenuSavesTheAvailableLanding();
        PausedApproachThenMainMenuDoesNotInventALanding();
        LoadingAndTeleportCannotBridgeAContact();
        ReplayDoesNotAppendToTheRealLanding();
        InterruptedFlowDoesNotBlockTheNextFlight();
        NestedSkipLoadWaitsForSkipCompletion();
        SimStopWhilePausedDoesNotDiscardTheApproach();
        FrozenClockPauseDoesNotHideMissingActiveFrames();
        FrozenClockAtTheNextFrameDoesNotHideMissingActiveFrames();
        AdvancingClockPauseDoesNotHideMissingActiveFrames();
        LegacyFramesStillRejectLoadingContacts();
    }

    private static void PausesPreserveApproachContactAndRollout()
    {
        var baseline = CaptureLanding(null, 0, false, false);
        var expected = Contacts(baseline).Single();
        Assert(expected.Impact.IsComplete, "recorder baseline has no complete impact");
        foreach (var flags in new[] { 1, 4, 8 })
        foreach (var pauseAt in new[] { -.2, .1 })
        foreach (var advancing in new[] { false, true })
        foreach (var noFrames in new[] { false, true })
        {
            var captures = CaptureLanding(pauseAt, flags, advancing, noFrames);
            var actual = Contacts(captures).Single();
            var name = $"flags={flags}, pause={pauseAt}, advancing={advancing}, noFrames={noFrames}";
            Assert(captures.Count(capture => !capture.IsDiagnosticOnly) == 1, "pause split a recorded landing: " + name);
            Assert(actual.Impact.IsComplete && actual.Impact.Status == expected.Impact.Status &&
                Close(actual.Impact.PeakG, expected.Impact.PeakG) &&
                Close(actual.Impact.EndOffsetSeconds, expected.Impact.EndOffsetSeconds) &&
                Close(actual.InertialVerticalFpm, expected.InertialVerticalFpm), "pause changed landing measurement: " + name);
            var raw = captures.Single(capture => !capture.IsDiagnosticOnly).Samples;
            Assert(raw.Any(sample => sample.OnGround) && raw.Any(sample => !sample.OnGround && sample.MotionSimulation),
                "pause lost the first half of the landing: " + name);
            if (!noFrames)
            {
                var pausedRows = raw.Where(sample => sample.SimulatorState == TelemetrySimulatorState.Paused).ToArray();
                Assert(pausedRows.Length > 0 && pausedRows.Length <= 2, "pause evidence absent or grows with frozen deliveries: " + name);
                if (advancing)
                    Assert(pausedRows.Length == 2 && pausedRows[0].SimulationTimeSeconds < pausedRows[1].SimulationTimeSeconds,
                        "advancing pause lost its raw start/end endpoints: " + name);
            }
        }
    }

    private static List<LandingEpisodeEventArgs> CaptureLanding(double? pauseAt, int flags, bool advancing, bool noFrames)
    {
        using var test = new RecorderFixture();
        var inserted = false;
        var skippedSourceTime = 0.0;
        for (var i = -50; i <= 220; i++)
        {
            var offset = i * .01;
            if (!inserted && pauseAt.HasValue && offset >= pauseAt.Value)
            {
                test.Pause(flags);
                if (!noFrames)
                    for (var pausedFrame = 1; pausedFrame <= 3; pausedFrame++)
                        test.Feed(offset - .01, offset - .01 + (advancing ? pausedFrame * 10 : 0), false);
                if (advancing) skippedSourceTime = 30;
                test.Pause(0);
                inserted = true;
            }
            test.Feed(offset, offset + skippedSourceTime);
        }
        test.Complete();
        return test.Captures;
    }

    private static void PauseBetweenBouncesPreservesTheEpisode()
    {
        using var test = new RecorderFixture();
        test.Feed(-.1, -.1);
        test.Feed(0, 0);
        test.Feed(.1, .1);
        test.Feed(.2, .2, onGround: false);
        test.Pause(8);
        test.Feed(.2, 30.2, false, false);
        test.Pause(0);
        test.Feed(.21, 30.21, onGround: false);
        test.Feed(.3, 30.3);
        test.Feed(.4, 30.4);
        test.Complete();
        var contacts = Contacts(test.Captures);
        Assert(test.Captures.Count(capture => !capture.IsDiagnosticOnly) == 1 && contacts.Length == 2 &&
            contacts[0].EpisodeNumber == contacts[1].EpisodeNumber,
            "pause between bounces lost a contact or split the episode");
    }

    private static void ActivePauseWithMotionFlagDoesNotAddMeasurements()
    {
        using var test = new RecorderFixture();
        for (var i = -30; i <= 200; i++)
        {
            var time = i * .01;
            if (i == 10)
            {
                test.Pause(4);
                test.Feed(.09, .09, motion: true, gForce: 9);
                test.Pause(0);
            }
            test.Feed(time, time);
        }
        test.Complete();
        var contact = Contacts(test.Captures).Single();
        Assert(contact.Impact.IsComplete && Close(contact.Impact.PeakG, 1.5),
            "Active Pause measured a stale force despite explicit SDK pause state");
        Assert(test.Captures.Single(capture => !capture.IsDiagnosticOnly).Samples.Any(sample =>
            sample.SimulatorState == TelemetrySimulatorState.Paused && sample.MotionSimulation && sample.GForce == 9),
            "Active Pause diagnostic evidence was rewritten or omitted");
    }

    private static void EscapeThenMainMenuSavesTheAvailableLanding()
    {
        using var test = new RecorderFixture();
        test.Feed(-.1, -.1);
        test.Feed(0, 0);
        test.Feed(.1, .1);
        test.Pause(8);
        test.Feed(.1, .1, false);
        test.Flow(SIMCONNECT_FLOW_EVENT.BACK_TO_MAIN_MENU);
        Assert(test.Captures.Count(capture => !capture.IsDiagnosticOnly) == 1, "main menu did not finalize the existing landing");
        var contact = Contacts(test.Captures).Single();
        Assert(!contact.Impact.IsComplete, "menu exit invented the remainder of an interrupted impact");
        var saved = test.Captures.Single(capture => !capture.IsDiagnosticOnly);
        Assert(saved.RunwayFacilitiesTask.IsCompleted, "menu exit left the saved landing waiting for a new runway lookup");
        test.Feed(5, 5, false);
        test.Complete();
        Assert(Contacts(test.Captures).Length == 1, "main-menu frames created another landing");
    }

    private static void PausedApproachThenMainMenuDoesNotInventALanding()
    {
        using var test = new RecorderFixture();
        test.Feed(-.5, -.5);
        test.Pause(8);
        test.Feed(-.5, 25, false);
        test.Flow(SIMCONNECT_FLOW_EVENT.BACK_TO_MAIN_MENU);
        test.Feed(1, 26, false);
        test.Complete();
        Assert(Contacts(test.Captures).Length == 0, "exiting a paused approach produced a landing");
    }

    private static void LoadingAndTeleportCannotBridgeAContact()
    {
        foreach (var transition in new[]
        {
            (SIMCONNECT_FLOW_EVENT.FLT_LOAD, SIMCONNECT_FLOW_EVENT.FLT_LOADED),
            (SIMCONNECT_FLOW_EVENT.TELEPORT_START, SIMCONNECT_FLOW_EVENT.TELEPORT_DONE),
            (SIMCONNECT_FLOW_EVENT.BACK_ON_TRACK_START, SIMCONNECT_FLOW_EVENT.BACK_ON_TRACK_DONE),
            (SIMCONNECT_FLOW_EVENT.SKIP_START, SIMCONNECT_FLOW_EVENT.SKIP_DONE),
        })
        {
            using var test = new RecorderFixture();
            test.Feed(-.5, -.5);
            test.Pause(8);
            test.Flow(transition.Item1);
            test.Feed(1, 30, false);
            test.Flow(transition.Item2);
            test.Flow(SIMCONNECT_FLOW_EVENT.FLIGHT_START);
            test.Pause(0);
            test.Feed(1, 31);
            test.Complete();
            Assert(Contacts(test.Captures).Length == 0, "lifecycle transition was stitched as a paused landing: " + transition.Item1);
        }
    }

    private static void ReplayDoesNotAppendToTheRealLanding()
    {
        using var test = new RecorderFixture();
        test.Feed(-.1, -.1);
        test.Feed(0, 0);
        test.Feed(.1, .1);
        test.Flow(SIMCONNECT_FLOW_EVENT.REPLAY_START);
        test.Feed(-.1, .2);
        test.Feed(0, .3);
        test.Flow(SIMCONNECT_FLOW_EVENT.REPLAY_END);
        test.Feed(.4, .4);
        test.Complete();
        Assert(Contacts(test.Captures).Length == 1, "replay erased or duplicated the original landing");
    }

    private static void SimStopWhilePausedDoesNotDiscardTheApproach()
    {
        using var test = new RecorderFixture();
        test.Feed(-.5, -.5);
        test.Pause(8);
        test.SimRunning(false);
        test.Feed(-.5, 29.5, false);
        test.SimRunning(true);
        test.Pause(0);
        test.Feed(-.49, 29.51);
        test.Feed(0, 30);
        test.Complete();
        Assert(Contacts(test.Captures).Length == 1, "SimStop accompanying a confirmed pause discarded approach continuity");
    }

    private static void InterruptedFlowDoesNotBlockTheNextFlight()
    {
        foreach (var flow in new[] { SIMCONNECT_FLOW_EVENT.REPLAY_START, SIMCONNECT_FLOW_EVENT.RTC_START })
        {
            using var test = new RecorderFixture();
            test.Feed(-.5, -.5);
            test.Flow(flow);
            test.Flow(SIMCONNECT_FLOW_EVENT.BACK_TO_MAIN_MENU);
            test.Flow(SIMCONNECT_FLOW_EVENT.FLT_LOAD);
            test.Flow(SIMCONNECT_FLOW_EVENT.FLT_LOADED);
            test.Flow(SIMCONNECT_FLOW_EVENT.FLIGHT_START);
            test.SimRunning(true);
            test.Pause(0);
            test.Feed(-.1, 10);
            test.Feed(0, 10.1);
            test.Complete();
            Assert(Contacts(test.Captures).Length == 1, "aborted flow left a sticky blocker on the next flight: " + flow);
        }
    }

    private static void NestedSkipLoadWaitsForSkipCompletion()
    {
        using var test = new RecorderFixture();
        test.Feed(-.5, -.5);
        test.Flow(SIMCONNECT_FLOW_EVENT.SKIP_START);
        test.Flow(SIMCONNECT_FLOW_EVENT.FLT_LOAD);
        test.Flow(SIMCONNECT_FLOW_EVENT.FLT_LOADED);
        test.Flow(SIMCONNECT_FLOW_EVENT.FLIGHT_START);
        test.SimRunning(true);
        test.Feed(-.1, 10);
        test.Feed(0, 10.1);
        // A skip may itself load an FLT: readiness must wait for both operations.
        test.Flow(SIMCONNECT_FLOW_EVENT.SKIP_DONE);
        test.Feed(.1, 10.2);
        test.Feed(-.1, 10.3);
        test.Feed(0, 10.4);
        test.Complete();
        Assert(Contacts(test.Captures).Length == 1, "nested skip/load was ready too early or left a sticky blocker");
    }

    private static void LegacyFramesStillRejectLoadingContacts()
    {
        using var test = new RecorderFixture(ready: false);
        test.Feed(-.5, 0, false);
        test.Feed(0, 8);
        test.Complete();
        Assert(Contacts(test.Captures).Length == 0, "SDK changes regressed protection for legacy/loading telemetry");
    }

    private static void FrozenClockPauseDoesNotHideMissingActiveFrames()
    {
        using var test = new RecorderFixture();
        test.Feed(-.1, -.1);
        test.Feed(0, 0);
        test.Pause(8);
        test.Feed(0, 0, false);
        test.Pause(0);
        // The source clock demonstrably froze; the 0.30 s after resume is a
        // missing active-physics interval, not another 0.29 s to remove as pause.
        test.Feed(.3, .3);
        test.Feed(.4, .4);
        test.Complete();
        var capture = test.Captures.Single(episode => !episode.IsDiagnosticOnly);
        var resumed = capture.Samples.First(sample => sample.SimulatorState == TelemetrySimulatorState.Running &&
            Math.Abs(sample.SimulationTimeSeconds - 100.3) < .000001);
        Assert(Close(resumed.AnalysisTimeSeconds, 100.3), "frozen-clock pause hid missing active physics time");
        Assert(!Contacts(test.Captures).Single().Impact.IsComplete, "missing post-resume physics was certified as a complete impact");
    }

    private static TouchdownResult[] Contacts(IEnumerable<LandingEpisodeEventArgs> captures) =>
        captures.SelectMany(capture => TouchdownAnalysis.Analyze(capture.Samples)).ToArray();

    private static void FrozenClockAtTheNextFrameDoesNotHideMissingActiveFrames()
    {
        using var test = new RecorderFixture();
        test.Feed(-.1, -.1);
        test.Feed(0, 0);
        test.Pause(8);
        // The pause freezes one frame after the last delivered active sample.
        // Repeated identical paused timestamps prove it is not an advancing clock.
        test.Feed(0, .01, false);
        test.Feed(0, .01, false);
        test.Pause(0);
        test.Feed(.3, .3);
        test.Feed(.4, .4);
        test.Complete();
        var capture = test.Captures.Single(episode => !episode.IsDiagnosticOnly);
        var resumed = capture.Samples.First(sample => sample.SimulatorState == TelemetrySimulatorState.Running &&
            Math.Abs(sample.SimulationTimeSeconds - 100.3) < .000001);
        Assert(Close(resumed.AnalysisTimeSeconds, 100.3), "one-frame pause boundary hid missing active physics time");
        Assert(!Contacts(test.Captures).Single().Impact.IsComplete, "missing active physics after a frozen pause was certified complete");
    }

    private static void AdvancingClockPauseDoesNotHideMissingActiveFrames()
    {
        using var test = new RecorderFixture();
        for (var index = -30; index <= 0; index++) test.Feed(index * .01, index * .01);
        test.Pause(8);
        test.Feed(0, 10, false);
        test.Feed(0, 20, false);
        test.Pause(0);
        // The pause accounts for twenty raw seconds. The first resumed delivery
        // is missing 0.30 active seconds; its SDK delta still describes one frame.
        for (var index = 30; index <= 210; index++) test.Feed(index * .01, 20 + index * .01);
        test.Complete();
        var capture = test.Captures.Single(episode => !episode.IsDiagnosticOnly);
        var resumed = capture.Samples.First(sample => sample.SimulatorState == TelemetrySimulatorState.Running &&
            Math.Abs(sample.SimulationTimeSeconds - 120.3) < .000001);
        Assert(Close(resumed.AnalysisTimeSeconds, 100.3), "advancing-clock pause hid missing post-resume physics time");
        Assert(!Contacts(test.Captures).Single().Impact.IsComplete,
            "missing active physics after an advancing-clock pause was certified as a complete impact");
        var clock = capture.DecisionJournal!.Snapshot().Entries.Single(entry => entry.Code == "resume_clock");
        Assert(Close(clock.Details["last_paused_raw_time"], 120) && Close(clock.Details["removed_pause_seconds"], 20),
            "pause clock journal omitted the observed boundary or removed time");
        Assert(capture.Samples.Where(sample => sample.SimulatorState == TelemetrySimulatorState.Paused)
            .Select(sample => sample.SimulationTimeSeconds).SequenceEqual(new[] { 110.0, 120.0 }),
            "pause normalization changed the original paused observations");
    }

    private sealed class RecorderFixture : IDisposable
    {
        private readonly SimConnectLandingRecorder _recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        private long _sequence;
        public List<LandingEpisodeEventArgs> Captures { get; } = new List<LandingEpisodeEventArgs>();

        public RecorderFixture(bool ready = true)
        {
            _recorder.EpisodeCompleted += (_, episode) => Captures.Add(episode);
            if (!ready) return;
            SimRunning(true);
            Pause(0);
            Flow(SIMCONNECT_FLOW_EVENT.FLIGHT_START);
        }

        public void Feed(double activeOffset, double sourceOffset, bool motion = true, bool? onGround = null, double? gForce = null)
        {
            var ground = onGround ?? activeOffset >= 0;
            var sample = new TelemetrySample
            {
                Sequence = _sequence++, HostElapsedSeconds = _sequence * .01,
                SimulationTimeSeconds = 100 + sourceOffset, SimulationDeltaSeconds = .01,
                OnGround = ground, MotionSimulation = motion,
                AboveGroundLevelFeet = 3 + Math.Max(0, -activeOffset * 2),
                PlaneAltitudeFeet = 603 + Math.Max(0, -activeOffset * 2), GroundAltitudeFeet = 600,
                LatitudeDegrees = 42 + activeOffset * .0005, LongitudeDegrees = 23,
                PitchDegrees = 3, BankDegrees = 0,
                GroundSpeedKnots = 108, IndicatedAirspeedKnots = 108,
                VelocityWorldYFps = !ground ? -2 : -.5 * Math.Exp(-Math.Pow((activeOffset - .15) / .12, 2)),
                VerticalSpeedFps = !ground ? -2 : 0,
                TouchdownNormalVelocityFps = ground ? 4 : 0,
                GForce = gForce ?? (!ground ? 1 : 1 + .5 * Math.Exp(-Math.Pow((activeOffset - .35) / .08, 2))),
            };
            Call("ProcessSample", sample);
        }

        public void Pause(int flags) => Call("SetSdkPauseFlags", flags);
        public void SimRunning(bool running) => Call("SetSdkSimRunning", running);
        public void Flow(SIMCONNECT_FLOW_EVENT flow) => Call("HandleSdkFlowEvent", flow);
        public void Complete() => Call("CompleteEpisode", false);
        public void Dispose() => _recorder.Dispose();

        private void Call(string name, params object[] arguments)
        {
            var owner = RecorderTestAccess.Owner(_recorder, name);
            var method = owner.GetType().GetMethod(name, PrivateInstance);
            if (method == null) throw new InvalidOperationException("Missing recorder lifecycle method: " + name);
            try { method.Invoke(owner, arguments); }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            { throw new InvalidOperationException(name + " failed: " + exception.InnerException.Message, exception.InnerException); }
        }
    }

    private static bool Close(double first, double second) => first.Equals(second) || Math.Abs(first - second) < .000001;
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
