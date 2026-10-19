using System;
using System.Collections.Generic;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Telemetry;

internal enum SimulatorFlowEvent
{
    FLT_LOAD = 1,
    FLT_LOADED = 2,
    TELEPORT_START = 3,
    TELEPORT_DONE = 4,
    BACK_ON_TRACK_START = 5,
    BACK_ON_TRACK_DONE = 6,
    SKIP_START = 7,
    SKIP_DONE = 8,
    RTC_START = 9,
    RTC_END = 10,
    REPLAY_START = 11,
    REPLAY_END = 12,
    BACK_TO_MAIN_MENU = 13,
    FLIGHT_END = 14,
    PLANE_CRASH = 15,
    FLIGHT_START = 16,
}

internal sealed partial class LandingEpisodeCoordinator
{
    [Flags]
    private enum FlightBlock { None = 0, Loading = 1, Teleport = 2, BackOnTrack = 4, Skip = 8, Rtc = 16, Replay = 32, Ended = 64 }
    private bool _sdkStateKnown;
    private bool? _sdkSimRunning;
    private int _sdkPauseFlags = -1;
    private FlightBlock _flightBlock;
    internal bool IsFlightEnded => (_flightBlock & FlightBlock.Ended) != 0;
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

    internal void SetSdkPauseFlags(int flags)
    {
        _sdkStateKnown = true;
        if (_sdkPauseFlags == flags) return;
        _sdkPauseFlags = flags;
        RecordLifecycle("pause_flags", "SDK Pause_EX1 state changed; flags are retained without collapsing active/player/full pause.");
        if (flags > 0) SuspendFlight();
    }

    internal void SetSdkSimRunning(bool running)
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

    internal void HandleFlowEvent(SimulatorFlowEvent flow)
    {
        _sdkStateKnown = true;
        switch (flow)
        {
            case SimulatorFlowEvent.FLT_LOAD: BeginFlightBlock(FlightBlock.Loading, "flight_loading"); break;
            case SimulatorFlowEvent.FLT_LOADED: EndFlightBlock(FlightBlock.Loading, "flight_loaded"); break;
            case SimulatorFlowEvent.TELEPORT_START: BeginFlightBlock(FlightBlock.Teleport, "teleport_started"); break;
            case SimulatorFlowEvent.TELEPORT_DONE: EndFlightBlock(FlightBlock.Teleport, "teleport_finished"); break;
            case SimulatorFlowEvent.BACK_ON_TRACK_START: BeginFlightBlock(FlightBlock.BackOnTrack, "back_on_track_started"); break;
            case SimulatorFlowEvent.BACK_ON_TRACK_DONE: EndFlightBlock(FlightBlock.BackOnTrack, "back_on_track_finished"); break;
            case SimulatorFlowEvent.SKIP_START: BeginFlightBlock(FlightBlock.Skip, "skip_started"); break;
            case SimulatorFlowEvent.SKIP_DONE: EndFlightBlock(FlightBlock.Skip, "skip_finished"); break;
            case SimulatorFlowEvent.RTC_START: BeginFlightBlock(FlightBlock.Rtc, "rtc_started"); break;
            case SimulatorFlowEvent.RTC_END: EndFlightBlock(FlightBlock.Rtc, "rtc_finished"); break;
            case SimulatorFlowEvent.REPLAY_START:
                ClearPendingReplayFrames();
                BeginFlightBlock(FlightBlock.Replay, "replay_started");
                _replayActive = true;
                _resumeCaptureBelowGate = false;
                _armed = false;
                SetStatus(RecorderState.Connected, "Recorder.ReplayActive");
                break;
            case SimulatorFlowEvent.REPLAY_END:
                EndFlightBlock(FlightBlock.Replay, "replay_finished");
                _replayActive = _externalReplayActive;
                _resumeCaptureBelowGate = !_replayActive;
                PublishCaptureStatus();
                break;
            case SimulatorFlowEvent.BACK_TO_MAIN_MENU:
            case SimulatorFlowEvent.FLIGHT_END:
            case SimulatorFlowEvent.PLANE_CRASH:
                BeginFlightBlock(FlightBlock.Ended, flow.ToString());
                break;
            case SimulatorFlowEvent.FLIGHT_START:
                if ((_flightBlock & FlightBlock.Ended) != 0) EndFlightBlock(FlightBlock.Ended, "flight_started");
                RecordLifecycle("flight_started", "Flight flow started; airborne physics evidence is still required for a contact.");
                break;
        }
    }

    private void BeginFlightBlock(FlightBlock block, string reason)
    {
        if ((_flightBlock & block) != 0) return;
        ClearPendingReplayFrames();
        _cancelledFlightBlocks &= ~block;
        EndFlightSegment(reason);
        if (block == FlightBlock.Ended)
        {
            // Leaving/starting a flight cancels unfinished cinematics, replay and
            // repositioning. Their completion callbacks need not follow an abort.
            _cancelledFlightBlocks |= _flightBlock & ~FlightBlock.Ended;
            _flightBlock = block;
            _externalReplayActive = false;
            _replayActive = false;
            _resumeCaptureBelowGate = false;
            ResetReplayObservation();
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
        MetadataRequested?.Invoke(this, EventArgs.Empty);
        RecordLifecycle(reason, "State replacement completed; begin with fresh physical contact evidence.");
    }

    private void EndFlightSegment(string reason)
    {
        var wasRecording = IsRecording;
        RecordLifecycle(reason, "Finish any real contact immediately; never join telemetry across a flight-state replacement.");
        if (_lastContactTime.HasValue) CompleteEpisodeWithReason(reason, false);
        else DiscardEpisode();
        // The 15-second capture may already have ended while its final scenery
        // request is still pending. A replacement flight must not satisfy it.
        if (_facilities.SnapshotTask != null && !_facilities.SnapshotTask.IsCompleted)
            _facilities.CompleteWithoutRefresh("Flight state changed before the final scenery response: " + reason);
        ResetFlightDetection();
        _aircraftOnGround = null;
        SetMetadata("Unknown aircraft", "unknown", "unknown");
        _flightSegment++;
        _sdkFlightSeen = false;
        _sdkSuspended = false;
        _pausedClockOffset = 0;
        _lastActiveSdkFrame = null;
        _pendingLifecycleJournal = new TelemetryDecisionJournal();
        RecordLifecycle(reason, "New physical segment requires an airborne witness before a ground transition is a landing.");
        if (wasRecording) PublishCaptureStatus();
    }

    internal void ResetSdkLifecycle()
    {
        ResetReplayObservation();
        _replayActive = _externalReplayActive;
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

    internal void RestoreTransportLifecycle(SimulatorLifecycleSnapshotMessage state)
    {
        _sdkStateKnown = state.Known; _sdkSimRunning = state.Running < 0 ? (bool?)null : state.Running != 0;
        _sdkPauseFlags = state.Pause; _flightBlock = (FlightBlock)state.Blocks;
        _cancelledFlightBlocks = (FlightBlock)state.Cancelled;
        _replayActive = _externalReplayActive || (_flightBlock & FlightBlock.Replay) != 0;
        _armed = !_replayActive;
        PublishCaptureStatus();
    }

    internal void AnnotateSdkFrame(TelemetrySample sample)
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
