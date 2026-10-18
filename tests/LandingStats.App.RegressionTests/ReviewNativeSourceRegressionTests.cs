using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.RegressionTests;

internal static class ReviewNativeSourceRegressionTests
{
    internal static void RunAll()
    {
        using var h = new Harness();
        h.SetControl();
        RecorderTestAccess.Call(h.Source, "Disconnect");
        h.CheckControlReset();
        Check(h.Reason == SourceDisconnectionReason.ConnectionClosed, "Disconnect reason was not preserved.");
        h.Attach(); h.SetControl();
        h.Dispatch = () =>
        {
            RecorderTestAccess.Call(h.Source, "HandleSdkFlowEvent", new object[] { (SIMCONNECT_FLOW_EVENT)Enum.Parse(typeof(SIMCONNECT_FLOW_EVENT), "FLT_LOAD") });
            RecorderTestAccess.Call(h.Source, "SetFullTelemetryEnabled", new object[] { true });
            h.CheckControlReset();
            Check(h.MetadataRequests == 0 && h.TelemetryRequests == 0, "SDK writes ran inside ReceiveMessage callback.");
        };
        var handled = false; h.Source.HandleWindowMessage(SimConnectSource.WindowMessage, ref handled);
        Check(handled && h.MetadataRequests == 1 && h.TelemetryRequests == 1 && h.MetadataOnce, "Deferred effects were lost or replaced the metadata subscription.");
        var flows = h.Flows;
        RecorderTestAccess.Call(h.Source, "HandleSdkFlowEvent", new object[] { (SIMCONNECT_FLOW_EVENT)int.MaxValue });
        Check(h.Flows == flows, "Unknown numeric SDK flow entered the lifecycle reducer.");
        h.Attach(); h.Now = 11; RecorderTestAccess.Set(h.Source, "_connectionSetupComplete", false);
        RecorderTestAccess.Set(h.Source, "_connectionOpenedSeconds", 0.0);
        RecorderTestAccess.Call(h.Source, "OnRetryTimerTick", new object?[] { null, EventArgs.Empty });
        Check(h.Reason == SourceDisconnectionReason.OpenTimeout, "Unacknowledged native connection did not expire.");

        using var failure = new SimConnectSource(IntPtr.Zero, h, h, openConnection: () => throw new UnauthorizedAccessException("fixture"));
        failure.Start(); Check(!failure.IsConnected, "Non-COM constructor failure escaped retry handling.");
        IncrementalEnumeration();
    }
    private static void IncrementalEnumeration()
    {
        var api = new Joysticks(); var reader = new WindowsJoystickReader(api); var sample = new TelemetrySample();
        reader.BeginRefresh(); var done = false;
        for (var step = 0; step < 16; step++)
        {
            var before = api.Polls;
            done = reader.RefreshNext(out var changed);
            Check(api.Polls - before <= 1 && !changed, "Hotplug enumeration monopolized a full polling turn or replaced an unchanged catalog.");
            reader.ReadInto(sample);
            Check(sample.RawControllerAxesValid[0] && sample.RawControllerDeviceId[0] == 0, "Partial enumeration invalidated an existing controller.");
        }
        Check(done, "Enumeration did not complete after the bounded ID range.");
    }
    private sealed class Joysticks : WindowsJoystickReader.IJoystickApi
    {
        internal int Polls;
        public uint DeviceCount => 16;
        public bool TryGetState(uint id, out WindowsJoystickReader.JoyInfoEx state)
        { Polls++; state = new WindowsJoystickReader.JoyInfoEx { XPosition = 50 }; return id == 0; }
        public bool TryGetCapabilities(uint id, out WindowsJoystickReader.JoyCaps caps)
        { caps = new WindowsJoystickReader.JoyCaps { ProductName = "fixture", RegistryKey = "", AxisCount = 1, XMaximum = 100 }; return id == 0; }
    }
    private sealed class Harness : IDisposable, ISimulatorCaptureContext, ISceneryQueryObserver
    {
        internal readonly SimConnectSource Source;
        private readonly SimConnect _native;
        internal Action? Dispatch; internal int MetadataRequests, TelemetryRequests, Flows; internal bool MetadataOnce; internal double Now;
        internal SourceDisconnectionReason Reason;
        internal Harness()
        {
            _native = (SimConnect)FormatterServices.GetUninitializedObject(typeof(SimConnect)); GC.SuppressFinalize(_native);
            Source = new SimConnectSource(IntPtr.Zero, this, this, openConnection: () => throw new UnauthorizedAccessException("fixture"),
                receiveConnection: _ => Dispatch?.Invoke(), disposeConnection: _ => { },
                requestTelemetry: (_, __, ___, ____) => { TelemetryRequests++; return 1; }, subscriptionClock: () => Now,
                requestMetadata: (_, once) => { MetadataRequests++; MetadataOnce = once; });
            Source.Disconnected += value => Reason = value; Source.LifecycleReceived += (_, __) => Flows++;
            Attach();
        }
        internal void Attach() { RecorderTestAccess.Set(Source, "_simConnect", _native); RecorderTestAccess.Set(Source, "_connectionSetupComplete", true); }
        internal void SetControl()
        { RecorderTestAccess.Set(Source, "_lastGuardAglFeet", 50.0); RecorderTestAccess.Set(Source, "_axisElevatorSetPercent", 22.0); RecorderTestAccess.Set(Source, "_axisElevatorSetReceivedHostSeconds", 0.0); }
        internal void CheckControlReset()
        { Check(double.IsNaN((double)RecorderTestAccess.Get(Source, "_lastGuardAglFeet")!) && double.IsNaN((double)RecorderTestAccess.Get(Source, "_axisElevatorSetReceivedHostSeconds")!), "Production source retained old guard/elevator context."); }
        public bool RawDebugEnabled => false; public bool IsRecording => false; public bool HasContact => false;
        public TelemetryDecisionJournal DecisionJournal { get; } = new TelemetryDecisionJournal(); public double LastSimulationTime => 0;
        public long EpisodeId => 0; public bool IsPreliminary => false; public double DecisionTime => 0;
        public void FinishRunwayLookupStage(IReadOnlyList<RunwayFacility> facilities) { } public void ResetConnection() { }
        public void Dispose() => Source.Dispose();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
