using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using Microsoft.FlightSimulator.SimConnect;

// Explicit manual diagnostic, never part of the unattended regression suite.
// It subscribes to data only: no aircraft writes, input events, or network upload.
internal static class LiveSubscriptionProbe
{
    private enum Id { Frame = 1 }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct SimpleFrame { public double SimulationTime; }
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        using var writer = new StreamWriter(new FileStream(Path.Combine(directory, "subscriptions.tsv"), FileMode.CreateNew));
        writer.AutoFlush = true;
        writer.WriteLine("case\thost_s\tevent\tdetails");
        foreach (var name in new[] { "SimConnect.dll", "Microsoft.FlightSimulator.SimConnect.dll" })
        {
            using var sha = SHA256.Create(); using var file = File.OpenRead(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name));
            writer.WriteLine("environment\t0\tsha256\t" + name + "=" + BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant());
        }
        var failures = 0;
        for (var repeat = 1; repeat <= 3; repeat++)
        foreach (var full in new[] { false, true })
        foreach (var initialNever in new[] { true, false })
            if (!RunCase(writer, repeat, full, initialNever)) failures++;
        Console.WriteLine("Completed 12 fresh-connection cases; inconclusive/failed cases=" + failures);
        return failures == 0 ? 0 : 2;
    }
    private static bool RunCase(StreamWriter writer, int repeat, bool full, bool initialNever)
    {
        var name = repeat + "/" + (full ? "full" : "simple") + "/" + (initialNever ? "initial-never" : "data-then-never");
        var clock = Stopwatch.StartNew();
        void Log(string kind, string text) => writer.WriteLine(name + "\t" + clock.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture) + "\t" + kind + "\t" + text);
        using var signal = new AutoResetEvent(false);
        var ports = new Ports(); using var definitionSource = new SimConnectSource(IntPtr.Zero, ports, ports);
        SimConnect? connection = null;
        var opened = false; var frames = 0; var initialErrors = 0; var otherErrors = 0; var quit = false;
        var switched = false; uint firstPacket = 0; double firstRequestAt = 0, switchedAt = 0, firstFrameAt = double.NaN;
        var frameCountAtSwitch = 0; var lateFrames = 0;
        try
        {
            connection = new SimConnect("Landing Stats read-only subscription A/B " + name, IntPtr.Zero, 0, signal, 0);
            void Request(bool enabled)
            {
                connection.RequestDataOnSimObject(Id.Frame, Id.Frame, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    enabled ? SIMCONNECT_PERIOD.SIM_FRAME : SIMCONNECT_PERIOD.NEVER, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
                var packet = connection.GetLastSentPacketID();
                if (!opened) { firstPacket = packet; firstRequestAt = clock.Elapsed.TotalSeconds; opened = true; }
                Log("request", "packet=" + packet + " request=1 definition=1 object=0 period=" + (enabled ? "SIM_FRAME" : "NEVER") + " flags=DEFAULT origin=0 interval=0 limit=0");
            }
            connection.OnRecvOpen += (sender, data) =>
            {
                Log("open", "utc=" + DateTime.UtcNow.ToString("o") + " app=" + data.szApplicationName + " version=" + data.dwApplicationVersionMajor + "." + data.dwApplicationVersionMinor + "." + data.dwApplicationBuildMajor + "." + data.dwApplicationBuildMinor + " sdk=" + data.dwSimConnectVersionMajor + "." + data.dwSimConnectVersionMinor + "." + data.dwSimConnectBuildMajor + "." + data.dwSimConnectBuildMinor);
                if (full)
                {
                    typeof(SimConnectSource).GetMethod("RegisterFrameDefinition", Private)!.Invoke(definitionSource, new object[] { sender });
                    sender.RegisterDataDefineStruct<SimFrameData>(Id.Frame);
                    var journal = (SimConnectOperationJournal)typeof(SimConnectSource).GetField("_simConnectOperations", Private)!.GetValue(definitionSource)!;
                    for (uint packet = 1; packet <= sender.GetLastSentPacketID(); packet++)
                    { var operation = journal.Name(packet); if (operation != null) Log("definition", "packet=" + packet + " " + operation); }
                }
                else
                {
                    sender.AddToDataDefinition(Id.Frame, "SIMULATION TIME", "seconds", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
                    Log("definition", "packet=" + sender.GetLastSentPacketID() + " SIMULATION TIME FLOAT64 seconds");
                    sender.RegisterDataDefineStruct<SimpleFrame>(Id.Frame);
                }
                Request(!initialNever);
            };
            connection.OnRecvSimobjectData += (sender, data) =>
            {
                if (data.dwRequestID != 1 || data.dwData.Length == 0) throw new InvalidDataException("Unexpected probe data.");
                frames++;
                if (switched && !initialNever && clock.Elapsed.TotalSeconds - switchedAt > 0.5) lateFrames++;
                var time = full ? ((SimFrameData)data.dwData[0]).SimulationTimeSeconds : ((SimpleFrame)data.dwData[0]).SimulationTime;
                if (double.IsNaN(firstFrameAt)) { firstFrameAt = clock.Elapsed.TotalSeconds; Log("first_frame", "simulation_s=" + time.ToString("R", CultureInfo.InvariantCulture) + " since_first_request_s=" + (firstFrameAt - firstRequestAt).ToString("R", CultureInfo.InvariantCulture)); }
            };
            connection.OnRecvException += (sender, data) =>
            {
                if (data.dwSendID == firstPacket) initialErrors++; else otherErrors++;
                Log("sdk_exception", "code=" + data.dwException + " packet=" + data.dwSendID + " index=" + data.dwIndex + " first_request=" + (data.dwSendID == firstPacket));
            };
            connection.OnRecvQuit += (sender, data) => { quit = true; Log("quit", "simulator_quit"); };
            while (!quit && clock.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (signal.WaitOne(50)) connection.ReceiveMessage();
                if (opened && !switched && clock.Elapsed.TotalSeconds - firstRequestAt >= 2 && (initialNever || frames > 0))
                { frameCountAtSwitch = frames; switchedAt = clock.Elapsed.TotalSeconds; switched = true; Request(initialNever); }
                if (switched && clock.Elapsed.TotalSeconds - switchedAt >= 2) break;
            }
            Log("result", "opened=" + opened + " frames=" + frames + " frames_before_switch=" + frameCountAtSwitch + " late_frames_after_stop=" + lateFrames + " first_request_errors=" + initialErrors + " other_errors=" + otherErrors);
            Console.WriteLine(name + ": frames=" + frames + " initial errors=" + initialErrors + " other errors=" + otherErrors);
            return opened && switched && frames > 0 && otherErrors == 0 && lateFrames == 0 && (!initialNever || frameCountAtSwitch == 0);
        }
        catch (Exception error) { Log("failure", error.ToString().Replace("\r", " ").Replace("\n", " ")); Console.Error.WriteLine(name + ": " + error.Message); return false; }
        finally { connection?.Dispose(); Log("closed", "outside_dispatch=true"); }
    }
    private sealed class Ports : ISimulatorCaptureContext, ISceneryQueryObserver
    {
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
    }
}
