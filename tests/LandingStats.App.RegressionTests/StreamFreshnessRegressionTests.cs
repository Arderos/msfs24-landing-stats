using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.RegressionTests;

internal static class StreamFreshnessRegressionTests
{
    internal static void RunAll()
    {
        var requestType = typeof(SimConnectSource).GetNestedType("Requests", System.Reflection.BindingFlags.NonPublic)!;
        foreach (var id in new uint[] { 0, 1, 0x40000000, 0x7fffffff, 0x80000000, 0xfffffffe })
            Check(Convert.ToUInt32(Enum.ToObject(requestType, id), System.Globalization.CultureInfo.InvariantCulture) == id,
                "The SDK's Enum-to-UInt32 request conversion overflowed or changed a probe identity.");
        foreach (var missing in new uint[] { 0, 1 })
        {
            using var h = new Harness(); h.Enable();
            for (var i = 0; i <= 17 && h.Source.IsConnected; i++)
            {
                h.Now = i; h.Frame(1 - missing, i + 100); h.Source.MaintainStreamHealth(); h.AnswerProbe(i + 99.98); h.Retry();
            }
            Check(h.Disconnects == 1 && h.Quits == 0 && h.Closed == 1, "Silent stream must reconnect once without manufacturing simulator QUIT.");
            Check(h.Requests.Count(x => x == missing) == 3 && h.Requests.Count(x => x == 1 - missing) == 1,
                "Only the missing required stream receives two retries, despite out-of-order ONCE replies.");
        }

        using (var h = new Harness())
        {
            h.Enable();
            for (var i = 0; i <= 8; i++)
            { h.Now = i; h.Frame(0, i + 100); h.Source.MaintainStreamHealth(); h.AnswerProbe(i + 100); h.Retry(); }
            Check(h.Requests.Count(x => x == 1) == 2, "Full should have retried after five seconds of confirmed advancing physics.");
            h.Frame(1, 108);
            for (var i = 9; i < 40; i++)
            { h.Now = i; h.Frame(0, i + 100); h.Frame(1, i + 100); h.Source.MaintainStreamHealth(); h.AnswerProbe(i + 100); h.Retry(); }
            Check(h.Source.IsConnected && h.Disconnects == 0 && h.Requests.Count == 3, "Recovered stream must resume without a reconnect loop.");
        }

        foreach (var mode in new[] { "pause", "motion", "clock", "loading", "menu", "stopped", "replay" })
        {
            using var h = new Harness(); h.Enable();
            h.Watchdog.SetRunning(true); h.Watchdog.SetPause(0); h.Watchdog.ObserveData(100, true, 0);
            if (mode == "pause") h.Watchdog.SetPause(4);
            if (mode == "loading") h.Watchdog.Flow(SimulatorFlowEvent.FLT_LOAD);
            if (mode == "menu") h.Watchdog.Flow(SimulatorFlowEvent.BACK_TO_MAIN_MENU);
            if (mode == "replay") h.Watchdog.Flow(SimulatorFlowEvent.REPLAY_START);
            if (mode == "stopped") h.Watchdog.SetRunning(false);
            for (var i = 1; i < 120; i++)
            {
                h.Now = i;
                h.Source.MaintainStreamHealth(); h.AnswerProbe(mode == "clock" ? 100 : i + 100, mode != "motion", mode != "stopped"); h.Retry();
            }
            Check(h.Source.IsConnected && h.Requests.Count == 2 && h.Disconnects == 0, "Legitimate suspension falsely recovered: " + mode);
        }

        using (var h = new Harness())
        {
            h.Enable(full: false);
            for (var i = 0; i < 40; i++)
            { h.Now = i; h.Frame(0, i + 100, 5000); h.Source.MaintainStreamHealth(); h.AnswerProbe(i + 100); h.Retry(); }
            Check(h.Requests.SequenceEqual(new uint[] { 0 }), "Full above the AGL gate is intentionally absent, including its initial NEVER.");
        }

        using (var h = new Harness())
        {
            h.Enable();
            for (var i = 0; i <= 20 && h.Source.IsConnected; i++)
            { h.Now = i; h.Source.MaintainStreamHealth(); h.AnswerProbe(100 + i); h.Retry(); }
            Check(h.Disconnects == 1 && h.Published == 0, "Independent probes must recover both silent streams without publishing synthetic frames.");
        }

        using (var h = new Harness())
        {
            h.Enable(); h.Watchdog.Reset(0);
            h.Now = 29; h.Source.MaintainStreamHealth(); Check(h.Source.IsConnected, "Connection deadline fired early.");
            h.Now = 30; h.Source.MaintainStreamHealth(); Check(h.Disconnects == 1 && h.Quits == 0, "Non-answering connection did not recover.");
        }

        var watchdog = new SimulatorStreamWatchdog(); watchdog.Reset(0); watchdog.BeginProbe(0);
        var oldSystem = watchdog.SystemRequest; var oldFrame = watchdog.FrameRequest;
        watchdog.Reset(1); watchdog.BeginProbe(1);
        Check(oldSystem != watchdog.SystemRequest && oldFrame != watchdog.FrameRequest &&
            !watchdog.ObserveSystem(oldSystem, true, 2) && !watchdog.ObserveProbe(oldFrame, 123, true, 2), "Retired connection probe crossed generation fence.");
        watchdog.ObserveData(100, true, 2); watchdog.ObserveData(101, true, 3);
        Check(watchdog.ExpectedSince(3) == 3, "Advancing clock did not establish expectations.");
        watchdog.ObserveData(100.98, true, 3.1);
        Check(watchdog.ExpectedSince(4) == 3, "Slightly out-of-order evidence reset a required-stream timeout.");
        watchdog.ObserveData(1, true, 4);
        Check(watchdog.ExpectedSince(4) == null, "A real simulation-clock reversal did not reset expectations.");
    }

    internal static void SuspendedTransportStillRecovers()
    {
        foreach (var mode in new[] { "pause", "motion", "loading", "menu", "stopped", "replay" })
        foreach (var healthy in new[] { false, true })
        {
            using var h = new Harness(); h.Enable();
            h.Watchdog.SetRunning(true); h.Watchdog.SetPause(0);
            h.Watchdog.ObserveData(100, mode != "motion", 0);
            if (mode == "pause") h.Watchdog.SetPause(4);
            if (mode == "loading") h.Watchdog.Flow(SimulatorFlowEvent.FLT_LOAD);
            if (mode == "menu") h.Watchdog.Flow(SimulatorFlowEvent.BACK_TO_MAIN_MENU);
            if (mode == "replay") h.Watchdog.Flow(SimulatorFlowEvent.REPLAY_START);
            if (mode == "stopped") h.Watchdog.SetRunning(false);
            for (var i = 1; i <= 600 && h.Source.IsConnected; i++)
            {
                h.Now = i; h.Source.MaintainStreamHealth();
                if (healthy) h.AnswerProbe(100, mode != "motion", mode != "stopped");
                if (i < 120) Check(h.Source.IsConnected, "Suspension grace expired early: " + mode);
            }
            Check(healthy ? h.Source.IsConnected && h.Disconnects == 0 : !h.Source.IsConnected && h.Disconnects == 1,
                "Suspended independent transport health was misclassified: " + mode);
            Check(h.Quits == 0 && h.Published == 0, "Health recovery manufactured simulator QUIT or telemetry.");
        }
    }

    private sealed class Harness : IDisposable, ISimulatorCaptureContext, ISceneryQueryObserver
    {
        internal double Now;
        internal readonly SimConnectSource Source;
        internal readonly List<uint> Requests = new List<uint>();
        internal int Disconnects, Quits, Closed, Published;
        private readonly SimConnect _native;
        private uint _packet;
        internal SimulatorStreamWatchdog Watchdog => (SimulatorStreamWatchdog)RecorderTestAccess.Get(Source, "_streamWatchdog")!;
        internal Harness()
        {
            _native = (SimConnect)FormatterServices.GetUninitializedObject(typeof(SimConnect)); GC.SuppressFinalize(_native);
            Source = new SimConnectSource(IntPtr.Zero, this, this, () => _native, _ => { }, _ => Closed++,
                (_, request, __, enabled) => { Requests.Add(request); return ++_packet; }, () => Now, requestHealthProbe: (_, __, ___) => { });
            RecorderTestAccess.Set(Source, "_simConnect", _native); RecorderTestAccess.Set(Source, "_connectionSetupComplete", true);
            Source.Disconnected += _ => Disconnects++; Source.SimulatorExited += (_, __) => Quits++;
            Source.GuardReceived += _ => Published++; Source.SampleReceived += _ => Published++;
            Watchdog.Reset(0);
        }
        internal void Enable(bool full = true)
        { Retry(); if (full) RecorderTestAccess.Call(Source, "SetFullTelemetryEnabled", new object[] { true }); }
        internal void Retry() => RecorderTestAccess.Call(Source, "RetryRequiredSubscriptions");
        internal void Frame(uint request, double time, double agl = 100)
        {
            object frame = request == 0 ? (object)new SimGuardData { SimulationTimeSeconds = time, MotionSimulation = 1, AboveGroundLevelFeet = agl } :
                new SimFrameData { SimulationTimeSeconds = time, MotionSimulation = 1, AboveGroundLevelFeet = agl };
            RecorderTestAccess.Call(Source, "OnRecvSimobjectData", new object[] { _native,
                new SIMCONNECT_RECV_SIMOBJECT_DATA { dwRequestID = request, dwData = new[] { frame } } });
        }
        internal void AnswerProbe(double time, bool motion = true, bool running = true)
        {
            if (Watchdog.SystemRequest != 0) RecorderTestAccess.Call(Source, "OnRecvSystemState", new object[] { _native,
                new SIMCONNECT_RECV_SYSTEM_STATE { dwRequestID = Watchdog.SystemRequest, dwInteger = running ? 1u : 0u } });
            if (Watchdog.FrameRequest != 0) RecorderTestAccess.Call(Source, "OnRecvSimobjectData", new object[] { _native,
                new SIMCONNECT_RECV_SIMOBJECT_DATA { dwRequestID = Watchdog.FrameRequest, dwData = new object[] {
                    new SimGuardData { SimulationTimeSeconds = time, MotionSimulation = motion ? 1 : 0 } } } });
        }
        public bool RawDebugEnabled => false;
        public bool IsRecording => false;
        public bool HasContact => false;
        public TelemetryDecisionJournal DecisionJournal { get; } = new TelemetryDecisionJournal();
        public double LastSimulationTime => 0;
        public long EpisodeId => 0;
        public bool IsPreliminary => false;
        public double DecisionTime => 0;
        public void FinishRunwayLookupStage(IReadOnlyList<RunwayFacility> facilities) { }
        public void ResetConnection() { }
        public void Dispose() => Source.Dispose();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
