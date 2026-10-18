using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using LandingStats.App.Models;
using LandingStats.App.Telemetry;
using LandingStats.Core;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.RegressionTests;

internal static class SourceObservationIsolationRegressionTests
{
    internal static void RunAll()
    {
        Check(Marshal.SizeOf(typeof(SimConnectSource.SourceObservationData)) == 72, "Native optional definition layout differs from its eleven registered variables.");
        using var harness = new Harness();
        harness.Poll(); var first = harness.Request; Check(harness.Definitions == 1 && first == 0x60000000, "Observation did not begin in its independent request range.");
        harness.Now = .25; harness.Reply(first, 7); Check(harness.Observations.Last().FreezeMask == 7, "Optional USER flags were not decoded.");
        harness.Poll(); var timedOut = harness.Request;
        harness.Now = 2; harness.Poll(); Check(harness.Observations.Last().Reason == SourceObservationReason.RequestTimeout, "Missing optional reply was not marked unavailable.");
        var count = harness.Observations.Count; harness.Reply(timedOut, 0); Check(harness.Observations.Count == count, "Late timed-out reply refreshed flags.");
        harness.RequiredFrames(); Check(harness.Published == 2 && harness.Source.IsConnected && harness.Closed == 0,
            "Optional failure stopped required telemetry.");
        harness.Now = 7; harness.Poll(); var request = harness.Request;
        Check(request > timedOut, "Optional timeout disabled retries or reused request identity.");
        harness.Reject(); Check(harness.Observations.Last().SdkException == 3 && harness.Errors == 0, "Optional SDK rejection became a required-stream error.");
        harness.Now = 12; harness.Poll(); harness.Reply(harness.Request, 1);
        Check(harness.Observations.Last().Availability == SourceObservationAvailability.Available && harness.Observations.Last().FreezeMask == 1,
            "An optional capability failed to recover after rejection.");
        harness.Poll(); harness.Reply(harness.Request, -1);
        Check(harness.Observations.Last().Reason == SourceObservationReason.InvalidData, "Invalid booleans became fabricated flags.");
        // Failed optional definition is retried, without disconnecting or touching Full/Guard.
        using var definition = new Harness { FailDefinition = true };
        definition.Poll(); Check(definition.Observations.Single().Reason == SourceObservationReason.DefinitionRejected, "Definition failure was hidden.");
        definition.Now = 29; definition.Poll(); Check(definition.Definitions == 1, "Unsupported definition caused a busy retry loop.");
        definition.FailDefinition = false; definition.Now = 30; definition.Poll(); definition.Reply(definition.Request, 0);
        Check(definition.Definitions == 2 && definition.Source.IsConnected && definition.Observations.Last().FreezeMask == 0,
            "Definition failure became permanently disabled or reset the recording connection.");
    }
    private sealed class Harness : IDisposable, ISimulatorCaptureContext, ISceneryQueryObserver
    {
        internal readonly SimConnectSource Source;
        internal readonly List<ReplaySourceObservation> Observations = new List<ReplaySourceObservation>();
        internal double Now; internal uint Request; internal int Definitions, Closed, Errors, Published; internal bool FailDefinition;
        private readonly SimConnect _native; private uint _packet;
        internal Harness()
        {
            _native = (SimConnect)FormatterServices.GetUninitializedObject(typeof(SimConnect)); GC.SuppressFinalize(_native);
            Source = new SimConnectSource(IntPtr.Zero, this, this, () => _native, _ => { }, _ => Closed++,
                (_, __, ___, ____) => 1, () => Now, requestHealthProbe: (_, __, ___) => { },
                registerSourceObservation: _ => { Definitions++; if (FailDefinition) throw new InvalidOperationException("unsupported fixture"); },
                requestSourceObservation: (_, request) => { Request = request; return ++_packet; });
            RecorderTestAccess.Set(Source, "_simConnect", _native); RecorderTestAccess.Set(Source, "_connectionSetupComplete", true);
            Source.SourceObservationReceived += observation => Observations.Add(observation);
            Source.StatusChanged += (_, args) => { if (args.State == RecorderState.Error) Errors++; };
            Source.GuardReceived += _ => Published++; Source.SampleReceived += _ => Published++;
        }
        internal void Poll() => RecorderTestAccess.Call(Source, "MaintainSourceObservation");
        internal void Reply(uint request, int mask)
        {
            var value = new SimConnectSource.SourceObservationData { Time = Now, Latitude = 42, Longitude = 23, Altitude = 1000,
                FreezePosition = mask < 0 ? 2 : mask & 1, FreezeAltitude = (mask >> 1) & 1, FreezeAttitude = (mask >> 2) & 1 };
            RecorderTestAccess.Call(Source, "OnRecvSimobjectData", new object[] { _native,
                new SIMCONNECT_RECV_SIMOBJECT_DATA { dwRequestID = request, dwData = new object[] { value } } });
        }
        internal void Reject() => RecorderTestAccess.Call(Source, "OnRecvException", new object[] { _native,
            new SIMCONNECT_RECV_EXCEPTION { dwException = 3, dwSendID = _packet } });
        internal void RequiredFrames()
        {
            // AGL above the gate avoids changing the required subscription fixture.
            RecorderTestAccess.Call(Source, "OnRecvSimobjectData", new object[] { _native, new SIMCONNECT_RECV_SIMOBJECT_DATA { dwRequestID = 0,
                dwData = new object[] { new SimGuardData { SimulationTimeSeconds = Now, MotionSimulation = 1, AboveGroundLevelFeet = 4000 } } } });
            RecorderTestAccess.Call(Source, "OnRecvSimobjectData", new object[] { _native, new SIMCONNECT_RECV_SIMOBJECT_DATA { dwRequestID = 1,
                dwData = new object[] { new SimFrameData { SimulationTimeSeconds = Now, MotionSimulation = 1, AboveGroundLevelFeet = 4000 } } } });
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
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
