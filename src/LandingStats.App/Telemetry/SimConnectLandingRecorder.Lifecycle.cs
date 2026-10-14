using System;
using System.Collections.Generic;
using LandingStats.Core;
using Microsoft.FlightSimulator.SimConnect;

namespace LandingStats.App.Telemetry;

internal sealed partial class SimConnectLandingRecorder
{
    [Flags]
    private enum FlightBlock { None = 0, Loading = 1, Teleport = 2, BackOnTrack = 4, Skip = 8, Rtc = 16, Replay = 32, Ended = 64 }
    private enum LifecycleEvents : uint { Pause = 0x100, SimStart, SimStop }

    private bool _sdkStateKnown;
    private bool? _sdkSimRunning;
    private int _sdkPauseFlags = -1;
    private FlightBlock _flightBlock;
    private FlightBlock _cancelledFlightBlocks;
    private bool _sdkFlightSeen;
    private bool _sdkSuspended;
    private bool _pauseFramesObserved;
    private bool _pauseClockAdvanced;
    private double _firstPausedRawTime = double.NaN;
    private double _lastPausedRawTime = double.NaN;
    private bool _multiplePausedFrames;
    private double _pausedClockOffset;
    private TelemetrySample? _lastActiveSdkFrame;
    private long _flightSegment;
    private TelemetryDecisionJournal _pendingLifecycleJournal = new TelemetryDecisionJournal();

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
        if (ReferenceEquals(sender, _simConnect) && data.dwRequestID == (uint)Requests.SimulationState)
            SetSdkSimRunning(data.dwInteger != 0);
    }

    private bool ProcessLifecycleEvent(uint id, uint value)
    {
        switch ((LifecycleEvents)id)
        {
            case LifecycleEvents.Pause: SetSdkPauseFlags(unchecked((int)value)); return true;
            case LifecycleEvents.SimStart: SetSdkSimRunning(true); return true;
            case LifecycleEvents.SimStop: SetSdkSimRunning(false); return true;
            default: return false;
        }
    }

    private void SetSdkPauseFlags(int flags)
    {
        _sdkStateKnown = true;
        if (_sdkPauseFlags == flags) return;
        _sdkPauseFlags = flags;
        RecordLifecycle("pause_flags", "SDK Pause_EX1 state changed; flags are retained without collapsing active/player/full pause.");
        if (flags > 0) SuspendFlight();
    }

    private void SetSdkSimRunning(bool running)
    {
        _sdkStateKnown = true;
        if (_sdkSimRunning == running) return;
        _sdkSimRunning = running;
        RecordLifecycle(running ? "sim_started" : "sim_stopped",
            "SimStart/SimStop describes simulator execution, not the end of a flight. Wait for flow events or valid resumed physics.");
        if (!running && _sdkFlightSeen) SuspendFlight();
    }

    private void SuspendFlight()
    {
        if (_sdkSuspended || _flightBlock != FlightBlock.None) return;
        _sdkSuspended = true;
        _pauseFramesObserved = false;
        _pauseClockAdvanced = false;
        _firstPausedRawTime = double.NaN;
        _lastPausedRawTime = double.NaN;
        _multiplePausedFrames = false;
        RecordLifecycle("capture_suspended", "Keep the current episode, contact witness and pre-roll while confirmed simulator pause is active.");
    }

    private void RecordLifecycle(string code, string reason)
    {
        var time = _lastActiveSdkFrame == null ? _lastSimulationTime : TimeOf(_lastActiveSdkFrame);
        (_episodeDecisionJournal ?? _pendingLifecycleJournal).Record(time, "recorder.lifecycle", code, reason,
            new Dictionary<string, double>
            {
                ["pause_flags"] = _sdkPauseFlags,
                ["sim_running"] = _sdkSimRunning.HasValue ? (_sdkSimRunning.Value ? 1 : 0) : -1,
                ["flight_segment"] = _flightSegment,
                ["blocked_states"] = (int)_flightBlock,
                ["raw_simulation_time"] = _lastSimulationTime,
                ["paused_clock_offset"] = _pausedClockOffset
            });
    }

    private void HandleSdkFlowEvent(SIMCONNECT_FLOW_EVENT flow)
    {
        _sdkStateKnown = true;
        switch (flow)
        {
            case SIMCONNECT_FLOW_EVENT.FLT_LOAD: BeginFlightBlock(FlightBlock.Loading, "flight_loading"); break;
            case SIMCONNECT_FLOW_EVENT.FLT_LOADED: EndFlightBlock(FlightBlock.Loading, "flight_loaded"); break;
            case SIMCONNECT_FLOW_EVENT.TELEPORT_START: BeginFlightBlock(FlightBlock.Teleport, "teleport_started"); break;
            case SIMCONNECT_FLOW_EVENT.TELEPORT_DONE: EndFlightBlock(FlightBlock.Teleport, "teleport_finished"); break;
            case SIMCONNECT_FLOW_EVENT.BACK_ON_TRACK_START: BeginFlightBlock(FlightBlock.BackOnTrack, "back_on_track_started"); break;
            case SIMCONNECT_FLOW_EVENT.BACK_ON_TRACK_DONE: EndFlightBlock(FlightBlock.BackOnTrack, "back_on_track_finished"); break;
            case SIMCONNECT_FLOW_EVENT.SKIP_START: BeginFlightBlock(FlightBlock.Skip, "skip_started"); break;
            case SIMCONNECT_FLOW_EVENT.SKIP_DONE: EndFlightBlock(FlightBlock.Skip, "skip_finished"); break;
            case SIMCONNECT_FLOW_EVENT.RTC_START: BeginFlightBlock(FlightBlock.Rtc, "rtc_started"); break;
            case SIMCONNECT_FLOW_EVENT.RTC_END: EndFlightBlock(FlightBlock.Rtc, "rtc_finished"); break;
            case SIMCONNECT_FLOW_EVENT.REPLAY_START:
                BeginFlightBlock(FlightBlock.Replay, "replay_started");
                _replayActive = true;
                _armed = false;
                SetStatus(RecorderState.Connected, "Recorder.ReplayActive");
                break;
            case SIMCONNECT_FLOW_EVENT.REPLAY_END:
                EndFlightBlock(FlightBlock.Replay, "replay_finished");
                _replayActive = false;
                SetStatus(RecorderState.Connected, "Recorder.ConnectedGate");
                break;
            case SIMCONNECT_FLOW_EVENT.BACK_TO_MAIN_MENU:
            case SIMCONNECT_FLOW_EVENT.FLIGHT_END:
            case SIMCONNECT_FLOW_EVENT.PLANE_CRASH:
                BeginFlightBlock(FlightBlock.Ended, flow.ToString());
                break;
            case SIMCONNECT_FLOW_EVENT.FLIGHT_START:
                if ((_flightBlock & FlightBlock.Ended) != 0) EndFlightBlock(FlightBlock.Ended, "flight_started");
                RecordLifecycle("flight_started", "Flight flow started; airborne physics evidence is still required for a contact.");
                break;
        }
    }

    private void BeginFlightBlock(FlightBlock block, string reason)
    {
        if ((_flightBlock & block) != 0) return;
        _cancelledFlightBlocks &= ~block;
        EndFlightSegment(reason);
        if (block == FlightBlock.Ended)
        {
            // Leaving/starting a flight cancels unfinished cinematics, replay and
            // repositioning. Their completion callbacks need not follow an abort.
            _cancelledFlightBlocks |= _flightBlock & ~FlightBlock.Ended;
            _flightBlock = block;
            _replayActive = false;
        }
        else
        {
            if (block == FlightBlock.Loading) _flightBlock &= ~FlightBlock.Ended;
            _flightBlock |= block; // SKIP can itself load an FLT; wait for both completions.
        }
    }

    private void EndFlightBlock(FlightBlock block, string reason)
    {
        if ((_flightBlock & block) == 0 && (_cancelledFlightBlocks & block) != 0)
        {
            _cancelledFlightBlocks &= ~block;
            RecordLifecycle("cancelled_flow_completed", "Ignore a late completion from a flow cancelled by leaving its flight.");
            return;
        }
        // A completion received without its start still changes aircraft state.
        if ((_flightBlock & block) == 0) EndFlightSegment(reason);
        _flightBlock &= ~block;
        _sdkSimRunning = null;
        _armed = true;
        RecordLifecycle(reason, "State replacement completed; begin with fresh physical contact evidence.");
    }

    private void EndFlightSegment(string reason)
    {
        RecordLifecycle(reason, "Finish any real contact immediately; never join telemetry across a flight-state replacement.");
        if (_lastContactTime.HasValue) CompleteEpisodeWithReason(reason, false);
        else DiscardEpisode();
        // The 15-second capture may already have ended while its final scenery
        // request is still pending. A replacement flight must not satisfy it.
        if (_episodeFacilityCompletion != null && !_episodeFacilityCompletion.Task.IsCompleted)
            CompleteRunwayLookupWithoutRefresh("Flight state changed before the final scenery response: " + reason);
        ResetFlightDetection();
        _flightSegment++;
        _sdkFlightSeen = false;
        _sdkSuspended = false;
        _pausedClockOffset = 0;
        _lastActiveSdkFrame = null;
        _pendingLifecycleJournal = new TelemetryDecisionJournal();
        RecordLifecycle(reason, "New physical segment requires an airborne witness before a ground transition is a landing.");
    }

    private void ResetSdkLifecycle()
    {
        _sdkStateKnown = false;
        _sdkSimRunning = null;
        _sdkPauseFlags = -1;
        _flightBlock = FlightBlock.None;
        _cancelledFlightBlocks = FlightBlock.None;
        _sdkFlightSeen = false;
        _sdkSuspended = false;
        _pausedClockOffset = 0;
        _lastActiveSdkFrame = null;
        _flightSegment++;
        _pendingLifecycleJournal = new TelemetryDecisionJournal();
    }

    private void AnnotateSdkFrame(TelemetrySample sample)
    {
        if (!_sdkStateKnown) return; // Legacy replay/tests and SDK fallback retain conservative motion boundaries.
        sample.PauseFlags = _sdkPauseFlags;
        sample.FlightSegment = _flightSegment;
        sample.AnalysisTimeSeconds = sample.SimulationTimeSeconds - _pausedClockOffset;
        if (_flightBlock != FlightBlock.None || (_sdkSimRunning == false && !_sdkFlightSeen))
        {
            sample.SimulatorState = TelemetrySimulatorState.Inactive;
            return;
        }
        if (_sdkPauseFlags > 0 || _sdkSimRunning == false || (_sdkSuspended && !sample.MotionSimulation))
        {
            sample.SimulatorState = TelemetrySimulatorState.Paused;
            if (!_pauseFramesObserved) _firstPausedRawTime = sample.SimulationTimeSeconds;
            else
            {
                _multiplePausedFrames = true;
                _pauseClockAdvanced |= sample.SimulationTimeSeconds > _firstPausedRawTime;
            }
            _pauseFramesObserved = true;
            _lastPausedRawTime = sample.SimulationTimeSeconds;
            if (_lastActiveSdkFrame != null)
            {
                sample.AnalysisTimeSeconds = TimeOf(_lastActiveSdkFrame);
            }
            return;
        }
        sample.SimulatorState = TelemetrySimulatorState.Running;
        if (_sdkSuspended && sample.MotionSimulation)
        {
            var previous = _lastActiveSdkFrame;
            if (previous != null)
            {
                var gap = sample.SimulationTimeSeconds - previous.SimulationTimeSeconds;
                var delta = sample.SimulationDeltaSeconds;
                if (gap >= 0 && IsFinite(delta) && delta > 0)
                {
                    // If pause frames prove the clock froze, a larger resume gap
                    // is missing active telemetry, not elapsed paused time.
                    var clockAdvanced = _pauseClockAdvanced || (!_multiplePausedFrames &&
                        _pauseFramesObserved && _firstPausedRawTime - previous.SimulationTimeSeconds > delta + 1e-9);
                    var removedPauseSeconds = 0.0;
                    if (!_pauseFramesObserved || clockAdvanced)
                    {
                        removedPauseSeconds = Math.Max(0, gap - delta);
                        if (_pauseFramesObserved)
                        {
                            // The last paused row bounds the time we can remove.
                            // Later time may be missing active telemetry after
                            // resume, even when the pause clock was advancing.
                            removedPauseSeconds = Math.Min(removedPauseSeconds,
                                Math.Max(0, _lastPausedRawTime - previous.SimulationTimeSeconds));
                        }
                        _pausedClockOffset += removedPauseSeconds;
                    }
                    sample.AnalysisTimeSeconds = sample.SimulationTimeSeconds - _pausedClockOffset;
                    (_episodeDecisionJournal ?? _pendingLifecycleJournal).Record(sample.AnalysisTimeSeconds,
                        "recorder.lifecycle", "resume_clock", "Separate confirmed paused time from the resumed SDK frame; preserve missing active time after the last observed pause and when pause frames show a frozen clock.",
                        new Dictionary<string, double> { ["raw_gap_seconds"] = gap, ["frame_delta_seconds"] = delta,
                            ["pause_frames_observed"] = _pauseFramesObserved ? 1 : 0,
                            ["pause_clock_advanced"] = clockAdvanced ? 1 : 0,
                            ["last_paused_raw_time"] = _lastPausedRawTime,
                            ["removed_pause_seconds"] = removedPauseSeconds,
                            ["paused_clock_offset"] = _pausedClockOffset });
                }
                else if (gap != 0)
                {
                    // No defensible active elapsed time: preserve the earlier contact,
                    // but do not extrapolate its impact or geometry across this gap.
                    _contactContinuity.Reset();
                    sample.FlightSegment = ++_flightSegment;
                    RecordLifecycle("resume_time_unavailable", "Resume has no usable SDK frame delta; retain a physics boundary instead of inventing elapsed time.");
                }
                if (gap == 0)
                {
                    if (sample.OnGround == previous.OnGround)
                    {
                        sample.SimulatorState = TelemetrySimulatorState.Paused;
                        sample.AnalysisTimeSeconds = TimeOf(previous);
                        return; // Frozen duplicate must not replace a genuine contact/G peak.
                    }
                    _contactContinuity.Reset();
                    sample.FlightSegment = ++_flightSegment;
                    RecordLifecycle("resume_zero_time_transition", "Ground state changed without physical elapsed time; require a fresh airborne witness.");
                }
            }
            _sdkSuspended = false;
            RecordLifecycle("capture_resumed", "Continue the same episode on its active-physics clock; raw simulator timestamps remain unchanged.");
        }
        if (sample.MotionSimulation)
        {
            _sdkFlightSeen = true;
            _lastActiveSdkFrame = sample;
        }
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
