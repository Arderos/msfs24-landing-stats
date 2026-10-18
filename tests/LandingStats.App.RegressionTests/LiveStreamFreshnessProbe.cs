using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Interop;
using System.Windows.Threading;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.RegressionTests;

// Opt-in live test changes only this test client's subscriptions. It never
// pauses/moves the aircraft or starts recording/uploads in the application.
internal static class LiveStreamFreshnessProbe
{
    private enum Id : uint { Guard, Full }
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        using var log = new StreamWriter(new FileStream(Path.Combine(directory, "stream-recovery.tsv"), FileMode.CreateNew));
        log.AutoFlush = true; log.WriteLine("case\thost_s\tevent\tdetail");
        foreach (var missing in new[] { "guard", "full", "both" })
        {
            var clock = Stopwatch.StartNew(); var frame = new DispatcherFrame(); var ports = new Ports();
            var connections = 0; var guard = 0; var full = 0; var guardAfter = 0; var fullAfter = 0; var cut = false;
            double stoppedAt = 0;
            void Log(string kind, string detail) => log.WriteLine(missing + "\t" + clock.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture) + "\t" + kind + "\t" + detail);
            using var window = new HwndSource(new HwndSourceParameters("Landing Stats stream recovery probe") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
            using var source = new SimConnectSource(window.Handle, ports, ports);
            window.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) => source.HandleWindowMessage(message, ref handled));
            source.Connected += name => { connections++; Log("connected", name); };
            source.Disconnected += reason => Log("disconnected", reason.ToString());
            source.GuardReceived += value => { guard++; if (cut) { guardAfter++; if (guardAfter == 1) Log("guard_resumed", (clock.Elapsed.TotalSeconds - stoppedAt).ToString("R", CultureInfo.InvariantCulture)); } };
            source.SampleReceived += value => { full++; if (cut) { fullAfter++; if (fullAfter == 1) Log("full_resumed", (clock.Elapsed.TotalSeconds - stoppedAt).ToString("R", CultureInfo.InvariantCulture)); } };
            source.StatusChanged += (_, value) => Log("status", value.State + " " + value.MessageKey + " " + string.Join(" ", value.MessageArguments));
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, __) =>
            {
                if (!cut && clock.Elapsed.TotalSeconds >= 3 && guard >= 20 && full >= 20)
                {
                    var native = (SimConnect)RecorderTestAccess.Get(source, "_simConnect")!;
                    if (missing != "full") native.RequestDataOnSimObject(Id.Guard, Id.Guard, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                    if (missing != "guard") native.RequestDataOnSimObject(Id.Full, Id.Full, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                    stoppedAt = clock.Elapsed.TotalSeconds; cut = true; Log("subscription_cut", missing);
                }
                if (clock.Elapsed.TotalSeconds >= 17) frame.Continue = false;
            };
            try { timer.Start(); source.Start(); Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); source.Dispose(); }
            foreach (var entry in ports.DecisionJournal.Snapshot().Entries) Log("diagnostic", entry.Component + " " + entry.Code + " " + entry.Reason);
            var entries = ports.DecisionJournal.Snapshot().Entries;
            var retries = entries.Count(value => value.Code == "required_stream_retry");
            var errors = entries.Count(value => value.Code == "simconnect_request_rejected");
            Log("result", "connections=" + connections + " guard=" + guard + " full=" + full + " after=" + guardAfter + "/" + fullAfter + " retries=" + retries + " sdk_errors=" + errors);
            Console.WriteLine(missing + ": connections=" + connections + " after=" + guardAfter + "/" + fullAfter + " retries=" + retries + " SDK errors=" + errors);
            if (!cut) { Console.WriteLine("INCONCLUSIVE: live advancing Guard and Full were unavailable."); return 3; }
            if (connections != 1 || guardAfter < 20 || fullAfter < 20 || errors != 0 || retries != (missing == "both" ? 2 : 1)) return 2;
        }
        return 0;
    }
    private sealed class Ports : ISimulatorCaptureContext, ISceneryQueryObserver
    {
        public bool RawDebugEnabled => true;
        public bool IsRecording => false;
        public bool HasContact => false;
        public TelemetryDecisionJournal DecisionJournal { get; } = new TelemetryDecisionJournal();
        public double LastSimulationTime => 0;
        public long EpisodeId => 0;
        public bool IsPreliminary => false;
        public double DecisionTime => 0;
        public void FinishRunwayLookupStage(IReadOnlyList<RunwayFacility> facilities) { }
        public void ResetConnection() { }
    }
}
