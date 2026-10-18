using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;

// Opt-in, read-only live SDK check. No coordinator, history, upload, or UI root.
internal static class LiveSourceProbe
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        using var log = new StreamWriter(new FileStream(Path.Combine(directory, "source-starts.tsv"), FileMode.CreateNew));
        log.AutoFlush = true; log.WriteLine("case\thost_s\tevent\tdetails");
        var failures = 0;
        for (var repeat = 1; repeat <= 3; repeat++)
        foreach (var raw in new[] { false, true })
        {
            var name = repeat + (raw ? "/raw" : "/normal"); var clock = Stopwatch.StartNew(); var guard = 0; var full = 0; var connected = 0; var metadata = 0;
            var observations = 0; var unavailable = 0;
            void Log(string kind, string text) => log.WriteLine(name + "\t" + clock.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture) + "\t" + kind + "\t" + text);
            var ports = new Ports(raw); var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            using var window = new HwndSource(new HwndSourceParameters("Landing Stats read-only source probe") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
            using var source = new SimConnectSource(window.Handle, ports, ports);
            window.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) => source.HandleWindowMessage(message, ref handled));
            source.Connected += simulator => { connected++; Log("connected", simulator); };
            source.GuardReceived += value => { guard++; if (guard == 1) Log("first_guard", "on_ground=" + value.OnGround); };
            source.SampleReceived += value => { full++; if (full == 1) Log("first_full", "on_ground=" + value.OnGround); };
            source.MetadataReceived += (title, type, model) => { metadata++; Log("metadata", title + " | " + type + " | " + model); };
            source.SourceObservationReceived += value =>
            {
                if (value.Availability == SourceObservationAvailability.Available) observations++;
                if (value.Availability == SourceObservationAvailability.Unavailable) unavailable++;
                Log("source_observation", string.Join(" | ", value.Availability, value.Reason, "freeze=" + value.FreezeMask,
                    "slew=" + value.Slew, "request=" + value.RequestId, "sdk_error=" + value.SdkException,
                    "sim_s=" + value.SimulationTime.ToString("R", CultureInfo.InvariantCulture),
                    "lat=" + value.Latitude.ToString("R", CultureInfo.InvariantCulture), "lon=" + value.Longitude.ToString("R", CultureInfo.InvariantCulture),
                    "alt_ft=" + value.AltitudeFeet.ToString("R", CultureInfo.InvariantCulture), "pitch=" + value.Pitch.ToString("R", CultureInfo.InvariantCulture),
                    "bank=" + value.Bank.ToString("R", CultureInfo.InvariantCulture), "heading=" + value.Heading.ToString("R", CultureInfo.InvariantCulture)));
            };
            source.StatusChanged += (_, value) => Log("status", value.State + " " + value.MessageKey + " " + string.Join(" ", value.MessageArguments));
            timer.Tick += (_, _) => frame.Continue = false;
            try { timer.Start(); source.Start(); Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); source.Dispose(); }
            var errors = 0;
            foreach (var entry in ports.DecisionJournal.Snapshot().Entries)
            {
                if (entry.Code == "simconnect_request_rejected") errors++;
                Log("diagnostic", entry.Code + " " + entry.Reason);
            }
            Log("result", "connections=" + connected + " guard=" + guard + " full=" + full + " metadata=" + metadata + " sdk_errors=" + errors + " observations=" + observations + " unavailable=" + unavailable);
            Console.WriteLine(name + ": connections=" + connected + " guard=" + guard + " full=" + full + " metadata=" + metadata + " SDK errors=" + errors + " observations=" + observations + " unavailable=" + unavailable);
            if (connected == 0) { Console.WriteLine("INCONCLUSIVE: MSFS did not open a connection; live metadata was not verified."); return 3; }
            if (connected != 1 || guard < 20 || full < 20 || metadata == 0 || errors != 0 || observations < 5 || unavailable != 0) failures++;
        }
        return failures == 0 ? 0 : 2;
    }
    private sealed class Ports : ISimulatorCaptureContext, ISceneryQueryObserver
    {
        public Ports(bool raw) { RawDebugEnabled = raw; }
        public bool RawDebugEnabled { get; }
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
