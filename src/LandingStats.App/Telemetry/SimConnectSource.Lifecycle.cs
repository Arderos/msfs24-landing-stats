using System;
using LandingStats.Runtime.Contracts;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.Telemetry;

internal sealed partial class SimConnectSource
{
    private enum LifecycleEvents : uint { Pause = 0x100, SimStart, SimStop }
    private void SubscribeToLifecycle(SimConnect connection)
    {
        // Independent requests: an older server rejecting an extended event must
        // not prevent the remaining lifecycle signals or telemetry fallback.
        TryLifecycleRequest(connection, "Subscribe Pause_EX1", () => connection.SubscribeToSystemEvent(LifecycleEvents.Pause, "Pause_EX1"), eventId: (uint)LifecycleEvents.Pause);
        TryLifecycleRequest(connection, "Subscribe SimStart", () => connection.SubscribeToSystemEvent(LifecycleEvents.SimStart, "SimStart"), eventId: (uint)LifecycleEvents.SimStart);
        TryLifecycleRequest(connection, "Subscribe SimStop", () => connection.SubscribeToSystemEvent(LifecycleEvents.SimStop, "SimStop"), eventId: (uint)LifecycleEvents.SimStop);
        TryLifecycleRequest(connection, "Request Sim state", () => connection.RequestSystemState(Requests.SimulationState, "Sim"), requestId: (uint)Requests.SimulationState);
    }

    private void TryLifecycleRequest(SimConnect connection, string name, Action request, uint? eventId = null, uint? requestId = null)
    {
        try { request(); RememberOperation(connection, name, true, requestId, eventId: eventId); }
        catch { /* Unknown motion stops remain discontinuities without SDK confirmation. */ }
    }

    private void OnRecvSystemState(SimConnect sender, SIMCONNECT_RECV_SYSTEM_STATE data)
    {
        if (!ReferenceEquals(sender, _simConnect)) return;
        if (data.dwRequestID == (uint)Requests.AircraftConfiguration)
        { ObserveAircraftConfiguration(data.szString); return; }
        if (_streamWatchdog.ObserveSystem(data.dwRequestID, data.dwInteger != 0, _subscriptionClock())) return;
        if (data.dwRequestID == (uint)Requests.SimulationState)
        {
            _streamWatchdog.SetRunning(data.dwInteger != 0);
            LifecycleReceived?.Invoke(SourceLifecycle.SimRunning, data.dwInteger != 0 ? 1 : 0);
        }
    }

    private bool ProcessLifecycleEvent(uint id, uint value)
    {
        switch ((LifecycleEvents)id)
        {
            case LifecycleEvents.Pause: _streamWatchdog.SetPause(unchecked((int)value)); LifecycleReceived?.Invoke(SourceLifecycle.PauseFlags, unchecked((int)value)); return true;
            case LifecycleEvents.SimStart: _streamWatchdog.SetRunning(true); LifecycleReceived?.Invoke(SourceLifecycle.SimRunning, 1); return true;
            case LifecycleEvents.SimStop: _streamWatchdog.SetRunning(false); LifecycleReceived?.Invoke(SourceLifecycle.SimRunning, 0); return true;
            default: return false;
        }
    }


    private void HandleSdkFlowEvent(SIMCONNECT_FLOW_EVENT flow)
    {
        if (Enum.TryParse(flow.ToString(), out SimulatorFlowEvent translated) && Enum.IsDefined(typeof(SimulatorFlowEvent), translated))
        {
            ResetControlContext();
            _streamWatchdog.Flow(translated);
            LifecycleReceived?.Invoke(SourceLifecycle.Flow, (int)translated);
            // The response is asynchronous. Publish unknown first in the worker,
            // then request an independent ONCE without replacing SECOND|CHANGED.
            RefreshMetadata();
        }
    }
}
