using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LandingStats.App.Telemetry;
using LandingStats.Core;

namespace LandingStats.App.RegressionTests;

internal static class LandingCaptureGateRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll()
    {
        RunningSdkDoesNotTurnGroundChatterIntoCaptures();
        InterruptedFlightStillProducesDiagnostics();
        ActiveGroundHistoryDoesNotQualify();
        ExpiredOrReplacedFlightDoesNotQualify();
    }

    private static void RunningSdkDoesNotTurnGroundChatterIntoCaptures()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        var episodes = Listen(recorder);
        Call(recorder, "SetSdkPauseFlags", 0);
        Call(recorder, "SetSdkSimRunning", true);
        for (var i = 0; i < 200; i++)
        {
            Feed(recorder, Frame(i * .1, false, false));
            Feed(recorder, Frame(i * .1 + .05, true, false));
        }
        // Starting physics while already on the ground is not flight evidence.
        Feed(recorder, Frame(20, false, false));
        Feed(recorder, Frame(20.1, true, true));
        Call(recorder, "FlushRejectedContacts");
        Assert(episodes.Count == 0, "SDK Running ground chatter emitted an automatic archive");
        Assert((long)RecorderTestAccess.Get(recorder, "_nextEpisodeId")! == 0,
            "ground chatter was filtered after starting an episode instead of at the capture gate");

        var approach = Frame(21, false, true);
        approach.AboveGroundLevelFeet = 400;
        approach.VelocityWorldYFps = -4;
        Feed(recorder, approach);
        Feed(recorder, Frame(22, true, true));
        Feed(recorder, Frame(38, true, true));
        Assert(episodes.Count == 1 && !episodes[0].IsDiagnosticOnly, "first real landing after chatter lost");
        Assert(episodes[0].DecisionJournal!.Snapshot().Entries.Count(e => e.Code == "contact_flag_transition") == 1,
            "ground chatter introduced false contacts");
    }

    private static void InterruptedFlightStillProducesDiagnostics()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        var episodes = Listen(recorder);
        var airborne = Frame(1, false, true); // Above the approach gate: only pre-roll witnesses this flight.
        Feed(recorder, airborne);
        Feed(recorder, Frame(2, false, false));
        Feed(recorder, Frame(3, true, false));
        Call(recorder, "FlushRejectedContacts");
        Assert(episodes.Count == 1 && episodes[0].IsDiagnosticOnly, "interrupted real flight lost its diagnostic capture");
        Assert(episodes[0].Samples.Contains(airborne) && episodes[0].Samples.Count == 3,
            "diagnostic capture lost the active airborne witness or the interruption");
        Assert(episodes[0].DecisionJournal!.Snapshot().Entries.All(e => e.Code != "contact_flag_transition"),
            "interrupted flight was promoted to a measured landing");
    }

    private static void ActiveGroundHistoryDoesNotQualify()
    {
        using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
        var episodes = Listen(recorder);
        Feed(recorder, Frame(1, true, true));
        Feed(recorder, Frame(2, false, false));
        Feed(recorder, Frame(3, true, false));
        Call(recorder, "FlushRejectedContacts");
        Assert(episodes.Count == 0, "active taxi/ground history was mistaken for flight evidence");
    }

    private static void ExpiredOrReplacedFlightDoesNotQualify()
    {
        foreach (var boundary in new[] { "expired", "replaced", "segment", "paused", "inactive" })
        {
            using var recorder = new SimConnectLandingRecorder(IntPtr.Zero);
            var episodes = Listen(recorder);
            var air = Frame(1, false, true);
            if (boundary == "paused") air.SimulatorState = TelemetrySimulatorState.Paused;
            if (boundary == "inactive") air.SimulatorState = TelemetrySimulatorState.Inactive;
            Feed(recorder, air);
            if (boundary == "replaced") Call(recorder, "EndFlightSegment", "test_flight_replacement");
            var time = boundary == "expired" ? 30 : 2;
            var suspended = Frame(time, false, false);
            var ground = Frame(time + .1, true, false);
            if (boundary == "segment") { suspended.FlightSegment = 2; ground.FlightSegment = 2; }
            Feed(recorder, suspended);
            Feed(recorder, ground);
            Call(recorder, "FlushRejectedContacts");
            Assert(episodes.Count == 0, boundary + " flight evidence leaked into a new automatic capture");
        }
    }

    private static List<LandingEpisodeEventArgs> Listen(SimConnectLandingRecorder recorder)
    {
        var episodes = new List<LandingEpisodeEventArgs>();
        recorder.EpisodeCompleted += (_, e) => episodes.Add(e);
        return episodes;
    }

    private static TelemetrySample Frame(double time, bool ground, bool motion) => new TelemetrySample
    {
        Sequence = (long)(time * 100), SimulationTimeSeconds = time, AnalysisTimeSeconds = time,
        HostElapsedSeconds = time, SimulatorState = TelemetrySimulatorState.Running,
        FlightSegment = 1, OnGround = ground, MotionSimulation = motion, AboveGroundLevelFeet = 700,
    };

    private static void Feed(SimConnectLandingRecorder recorder, TelemetrySample sample) => Call(recorder, "ProcessSample", sample);
    private static void Call(SimConnectLandingRecorder recorder, string name, params object[] arguments) =>
        RecorderTestAccess.Call(recorder, name, arguments);
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
