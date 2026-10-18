using System;
using System.Collections.Generic;
using LandingStats.Runtime.Contracts;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.Telemetry;

internal sealed partial class SimConnectSource
{
    private void RequestHealthProbeNative(SimConnect connection, uint systemRequest, uint frameRequest)
    {
        connection.RequestSystemState((Requests)systemRequest, "Sim");
        RememberOperation(connection, "Probe simulator state", true, systemRequest);
        connection.RequestDataOnSimObject((Requests)frameRequest, Definitions.Guard, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        RememberOperation(connection, "Probe simulation clock (ONCE)", true, frameRequest, (uint)Definitions.Guard);
    }

    internal void MaintainStreamHealth()
    {
        var connection = _simConnect;
        if (connection == null || !_connectionSetupComplete || _disposed || _dispatchDepth != 0) return;
        try
        {
            var now = _subscriptionClock();
            if (_streamWatchdog.ConnectionUnresponsive(now))
            { ReconnectSilentStream("Simulator stopped answering independent liveness requests."); return; }
            var expected = _streamWatchdog.ExpectedSince(now);
            if (expected.HasValue)
            {
                var guard = CheckStream(_guardSubscription, "guard", now, expected.Value);
                var full = CheckStream(_fullSubscription, "full", now, expected.Value);
                if (guard == SubscriptionFreshness.Reconnect || full == SubscriptionFreshness.Reconnect)
                { ReconnectSilentStream("A required telemetry stream did not recover after two resubscriptions."); return; }
            }
            if (_streamWatchdog.BeginProbe(now))
                _requestHealthProbe(connection, _streamWatchdog.SystemRequest, _streamWatchdog.FrameRequest);
        }
        catch (Exception exception)
        {
            SetStatus(RecorderState.Error, "Recorder.ReceiveFailedFormat", exception.Message);
            DisconnectWithReason(SourceDisconnectionReason.ReceiveFailed);
        }
    }

    private SubscriptionFreshness CheckStream(RequiredTelemetrySubscription stream, string name, double now, double expected)
    {
        var result = stream.CheckFreshness(now, expected);
        if (result == SubscriptionFreshness.Retry)
        {
            _capture.DecisionJournal.Record(_capture.LastSimulationTime, "source." + name, "required_stream_retry",
                "Simulation advances but this required stream is silent; resubmit its subscription.",
                new Dictionary<string, double> { ["host_seconds"] = now });
            _recoverStatusAfterFrame = true;
            SetStatus(RecorderState.Error, "Recorder.TelemetryRecovering");
        }
        return result;
    }

    private void ReconnectSilentStream(string reason)
    {
        _capture.DecisionJournal.Record(_capture.LastSimulationTime, "source", "stream_connection_reconnect", reason);
        // This is a source discontinuity, never the simulator's real QUIT.
        DisconnectWithReason(SourceDisconnectionReason.SilentStream);
    }
}
