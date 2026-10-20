using System;
using System.Collections.Generic;
using System.Linq;
using LandingStats.App.Models;
using LandingStats.Core;
using LandingStats.Runtime.Contracts;

namespace LandingStats.App.Telemetry;

/// <summary>Managed landing state. The source lifetime and native handles do not own this state.</summary>
internal sealed partial class LandingEpisodeCoordinator
{
    private readonly IEpisodeFacilityPort _facilities;
    private readonly Func<double> _hostSeconds;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<IReadOnlyList<string>> _controlInputSources;
    private readonly Guid _captureSessionId;
    private bool _stopped;

    public LandingEpisodeCoordinator(IEpisodeFacilityPort facilities, Func<double> hostSeconds,
        Func<IReadOnlyList<string>> controlInputSources, Func<DateTime>? utcNow = null, Guid captureSessionId = default)
    {
        _facilities = facilities; _hostSeconds = hostSeconds; _controlInputSources = controlInputSources;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _captureSessionId = captureSessionId == Guid.Empty ? Guid.NewGuid() : captureSessionId;
    }
    public event EventHandler<RecorderStatusEventArgs>? StatusChanged;
    public event EventHandler<LandingEpisodeEventArgs>? EpisodeCompleted;
    // Accepted approach data at a source gap is retained locally, without
    // creating an automatic no-landing report or a fabricated contact.
    public event EventHandler<LandingEpisodeEventArgs>? ApproachInterrupted;
    public event EventHandler<LandingEpisodeStartedEventArgs>? EpisodeStarted;
    public event EventHandler<RawDebugCaptureStartedEventArgs>? RawDebugCaptureStarted;
    public event EventHandler<RawDebugSampleEventArgs>? RawDebugSampleReceived;
    public event EventHandler? RawDebugCaptureStopped;
    public event EventHandler<AircraftGroundStateEventArgs>? AircraftGroundStateChanged;
    public event EventHandler? MetadataRequested;

    internal bool IsRecording => _episodeSamples != null;
    internal bool HasContact => _lastContactTime.HasValue;
    internal bool RawDebugEnabled => _rawDebugEnabled;
    internal bool IsAircraftAirborne => _aircraftOnGround == false;
    internal bool IsAircraftGrounded => _aircraftOnGround == true;
    internal double LastSimulationTime => _lastSimulationTime;
    internal TelemetryDecisionJournal DecisionJournal => _episodeDecisionJournal ?? _pendingLifecycleJournal;
    internal string Simulator { get => _simulator; set => _simulator = value; }

    internal void SetMetadata(string title, string type, string model)
    {
        _aircraftTitle = title; _aircraftType = type; _aircraftModel = model;
        // SIM_FRAME can start an approach before the slower metadata reply.
        // Complete missing fields while this episode still owns the connection,
        // but never relabel an already identified episode with another aircraft.
        if (_episodeSamples == null) return;
        if (!CompatibleMetadata(_episodeAircraftTitle, title) ||
            !CompatibleMetadata(_episodeAircraftType, type) || !CompatibleMetadata(_episodeAircraftModel, model))
        {
            _episodeDecisionJournal?.Record(_lastSimulationTime, "recorder", "aircraft_metadata_conflict",
                "Rejected metadata that conflicts with this episode's known aircraft identity.");
            return;
        }
        var changed = false;
        void Fill(ref string target, string value)
        { if (!KnownMetadata(target) && KnownMetadata(value)) { target = value; changed = true; } }
        Fill(ref _episodeAircraftTitle, title); Fill(ref _episodeAircraftType, type); Fill(ref _episodeAircraftModel, model);
        if (changed) _episodeDecisionJournal?.Record(_lastSimulationTime, "recorder", "aircraft_metadata_resolved",
            "Completed missing episode aircraft metadata from the current source; known identity and captured frames are unchanged.");
    }
    private static bool KnownMetadata(string value) => !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "Unknown aircraft", StringComparison.OrdinalIgnoreCase);
    private static bool CompatibleMetadata(string previous, string incoming) => !KnownMetadata(previous) || !KnownMetadata(incoming) ||
        string.Equals(previous, incoming, StringComparison.Ordinal);

    internal void SetRawDebugEnabled(bool enabled)
    {
        if (_rawDebugEnabled == enabled) return;
        _rawDebugEnabled = enabled;
        if (enabled)
            RawDebugCaptureStarted?.Invoke(this, new RawDebugCaptureStartedEventArgs(_preRoll.ToArray(), _utcNow(),
                _simulator, _aircraftTitle, _aircraftType, _aircraftModel, _controlInputSources()));
        else RawDebugCaptureStopped?.Invoke(this, EventArgs.Empty);
    }

    internal void Tick()
    {
        var diagnostic = _rejectedContactBatcher.FlushExpired(_utcNow());
        if (diagnostic != null) EpisodeCompleted?.Invoke(this, diagnostic);
    }

    internal void SourceInterrupted(string reason)
    {
        if (_stopped) return;
        if (_lastContactTime.HasValue) CompleteEpisodeWithReason(reason, false);
        else
        {
            if (_episodeSamples != null && _episodeSamples.Count != 0)
            {
                _episodeDecisionJournal?.Record(_lastSimulationTime, "recorder", "approach_interrupted", reason);
                _facilities.CompleteWithoutRefresh("Source interrupted before contact: " + reason);
                ApproachInterrupted?.Invoke(this, new LandingEpisodeEventArgs(_activeEpisodeId, _episodeSamples.ToArray(),
                    _episodeSimulator, _episodeAircraftTitle, _episodeAircraftType, _episodeAircraftModel, _facilities.CachedAirports,
                    _episodeControlSources, _episodeDecisionJournal, isDiagnosticOnly: true,
                    initialFacilities: _facilities.CurrentSnapshot(), persistentId: _activePersistentId));
            }
            DiscardEpisode();
        }
        _facilities.CompleteWithoutRefresh("Source generation ended: " + reason);
        _replayActive = _externalReplayActive;
        ResetFlightDetection();
        ResetSdkLifecycle();
        _aircraftOnGround = null;
        SetMetadata("Unknown aircraft", "unknown", "unknown");
    }

    internal void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        ClearPendingReplayFrames();
        if (_lastContactTime.HasValue) CompleteEpisodeWithReason("recorder_disposed", false);
        else DiscardEpisode();
        _facilities.CompleteWithoutRefresh("Recorder stopped; settle this episode's remaining scenery lookup.");
        SetRawDebugEnabled(false);
        FlushRejectedContacts();
    }

    private static double TimeOf(TelemetrySample sample) => TelemetryAnalysisFrames.TimeOf(sample);
    private void SetStatus(RecorderState state, string messageKey, params object[] arguments) =>
        StatusChanged?.Invoke(this, new RecorderStatusEventArgs(state, messageKey, arguments));

    internal void PublishCaptureStatus()
    {
        // Acquisition may be a process behind on its FullTelemetry command.
        // Only this owner knows whether the current capture is still active.
        if (_replayActive) SetStatus(RecorderState.Connected, "Recorder.ReplayActive");
        else if (_pendingReplayFrames.Count != 0) SetStatus(RecorderState.Connected, "Recorder.ReplayChecking");
        else if (IsRecording) SetStatus(RecorderState.Recording, HasContact ? "Recorder.ContactRollout" : "Recorder.BelowGate");
        else SetStatus(RecorderState.Connected, "Recorder.ConnectedGate");
    }

    private const double CaptureStartAglFeet = 500.0;
    private const double CaptureStartDescentFps = -0.5;
    private const double PreRollSeconds = 15.0;
    private const double PostContactSeconds = 15.0;
    private const double GoAroundResetAglFeet = 1000.0;
    private const double MaximumApproachSeconds = 300.0;
    private const int PreRollMaximumSamples = 4096;
    private const int EpisodeMaximumSamples = 65536;
    private readonly RejectedContactBatcher _rejectedContactBatcher = new RejectedContactBatcher();
    private readonly PreRollBuffer _preRoll = new PreRollBuffer(PreRollSeconds, PreRollMaximumSamples);
    private List<TelemetrySample>? _episodeSamples;
    private TelemetryDecisionJournal? _episodeDecisionJournal;
    private int _episodeCoalescedFrameCount;
    private int _episodeNonMotionFrameCount;
    private bool _rawDebugEnabled;
    private long _nextEpisodeId;
    private long _activeEpisodeId;
    private Guid _activePersistentId;
    private string _episodeSimulator = "MSFS", _episodeAircraftTitle = "Unknown aircraft", _episodeAircraftType = "unknown", _episodeAircraftModel = "unknown";
    private IReadOnlyList<string> _episodeControlSources = Array.Empty<string>();
    private double _lastSimulationTime = double.NaN;
    private double _episodeStartTime;
    private double? _lastContactTime;
    private readonly TelemetryContactContinuity _contactContinuity = new TelemetryContactContinuity();
    private bool _previousOnGround = true;
    private bool _armed = true;
    private string _simulator = "MSFS";
    private string _aircraftTitle = "Unknown aircraft";
    private string _aircraftType = "unknown";
    private string _aircraftModel = "unknown";
    private bool _replayActive;
    private bool? _aircraftOnGround;
    private TelemetrySample? _lastKinematicFrame;
    private double _axisElevatorResetHostSeconds = double.NaN;

    internal void ProcessSample(TelemetrySample sample)
    {
        if (_stopped) throw new ObjectDisposedException(nameof(LandingEpisodeCoordinator));
        // Decide before clocks, contact witnesses, pre-roll or RAW can consume a
        // replay frame. The first frozen observation waits for movement proof.
        if (ProcessReplaySource(sample)) return;
        ProcessLiveSample(sample);
    }

    private void ProcessLiveSample(TelemetrySample sample)
    {
        // The native owner may still report its cached event after a boundary
        // inferred here. Its age identifies the original observation, so a new
        // event survives even if it arrives before a source could process a reset.
        if (sample.AxisElevatorSetValid && !double.IsNaN(_axisElevatorResetHostSeconds) &&
            sample.HostElapsedSeconds - sample.AxisElevatorSetAgeSeconds <= _axisElevatorResetHostSeconds + 0.000001)
        {
            sample.AxisElevatorSetValid = false; sample.AxisElevatorSetPercent = 0; sample.AxisElevatorSetAgeSeconds = double.NaN;
        }
        if (_sdkStateKnown && !double.IsNaN(_lastSimulationTime) &&
            sample.SimulationTimeSeconds < _lastSimulationTime - 0.001)
            EndFlightSegment("simulation_time_reversed");
        AnnotateSdkFrame(sample);
        var sampleTime = TimeOf(sample);
        if (_replayActive)
        {
            _lastSimulationTime = sample.SimulationTimeSeconds;
            _previousOnGround = sample.OnGround;
            return;
        }

        // Replay frames describe the replay camera/aircraft, not the live
        // aircraft. They must not close the replay warning or enter the raw
        // community telemetry stream without provenance.
        if (_rawDebugEnabled)
        {
            RawDebugSampleReceived?.Invoke(this, new RawDebugSampleEventArgs(sample));
        }

        // Handoff may first be observed while paused on the ground. A later
        // airborne bounce must not hide the fact that this landing is over.
        if (_resumeCaptureBelowGate && sample.OnGround && sample.SimulatorState != TelemetrySimulatorState.Inactive)
        {
            _replayApproachDiscarded = true;
            _resumeCaptureBelowGate = _requireReplayHandoffHistory = false;
            RecordLifecycle("replay_landing_discarded", "The first live frame after replay is already grounded; discard the remaining approach, including bounces.");
        }

        if (sample.SimulatorState == TelemetrySimulatorState.Paused ||
            sample.SimulatorState == TelemetrySimulatorState.Inactive)
        {
            _lastSimulationTime = sample.SimulationTimeSeconds;
            if (sample.SimulatorState == TelemetrySimulatorState.Paused)
                AppendEpisodeSample(sample);
            else
            {
                _contactContinuity.Reset();
                _lastKinematicFrame = null;
            }
            return;
        }
        UpdateAircraftGroundState(sample.OnGround);

        if (!double.IsNaN(_lastSimulationTime) && sample.SimulationTimeSeconds < _lastSimulationTime - 0.001)
        {
            if (_lastContactTime.HasValue)
            {
                CompleteEpisodeWithReason("simulation_time_reversed");
            }
            else
            {
                DiscardEpisode();
            }

            ResetFlightDetection();
            PublishCaptureStatus();
        }

        if (_lastKinematicFrame != null &&
            TelemetryKinematicDiscontinuity.TryDetect(_lastKinematicFrame, sample, out var discontinuity))
        {
            (_episodeDecisionJournal ?? _pendingLifecycleJournal).Record(sampleTime,
                "recorder.lifecycle", "position_discontinuity",
                "Position replacement exceeds the motion explained by active time, speeds and acceleration; close the old capture before this frame.",
                discontinuity!.Measurements());
            EndFlightSegment("position_discontinuity");
            AnnotateSdkFrame(sample);
            sampleTime = TimeOf(sample);
        }
        _lastKinematicFrame = sample.MotionSimulation ? sample.CopyForAnalysis() : null;
        _lastSimulationTime = sample.SimulationTimeSeconds;

        if (_replayApproachDiscarded)
        {
            _resumeCaptureBelowGate = _requireReplayHandoffHistory = false;
            if (!sample.MotionSimulation || sample.OnGround || !IsFinite(sample.AboveGroundLevelFeet) ||
                sample.AboveGroundLevelFeet < GoAroundResetAglFeet)
            {
                _armed = false;
                _preRoll.Clear();
                _contactContinuity.Reset();
                _previousOnGround = sample.OnGround;
                return;
            }
            _replayApproachDiscarded = false;
            RecordLifecycle("replay_approach_reset", "A new airborne approach above the go-around reset height may be recorded.");
        }
        AddToPreRoll(sample);

        if (sample.MotionSimulation && !sample.OnGround)
        {
            if (sample.AboveGroundLevelFeet >= GoAroundResetAglFeet)
            {
                _armed = true;
            }
        }

        var isContact = _contactContinuity.Observe(sample);
        var rejectedContact = sample.OnGround && !_previousOnGround && !isContact;
        // A rejected flag transition is relevant only if recent active flight
        // could have ended here. Loading/ground chatter alone must not create
        // an automatic capture, even when SDK lifecycle state says Running.
        var diagnosticContact = rejectedContact && HasActiveAirbornePreRoll(sample);
        var wasRecording = _episodeSamples != null;
        var startedNow = false;
        var resumedBelowGate = _resumeCaptureBelowGate && sample.MotionSimulation && !sample.OnGround &&
            sample.AboveGroundLevelFeet >= -50 && sample.AboveGroundLevelFeet <= CaptureStartAglFeet;
        if (sample.MotionSimulation && !sample.OnGround) _resumeCaptureBelowGate = false;
        if (_episodeSamples == null && _armed && (resumedBelowGate || ShouldStartEpisode(sample) || isContact || diagnosticContact))
        {
            StartEpisode(sample, isContact, diagnosticContact);
            startedNow = true;
        }

        if (_episodeSamples != null && !startedNow)
        {
            AppendEpisodeSample(sample);
        }

        if (isContact)
        {
            if (_requireReplayHandoffHistory)
            {
                _requireReplayHandoffHistory = false;
                var history = TelemetryDeduplicator.Deduplicate(TelemetryAnalysisFrames.Prepare(_episodeSamples!));
                if (!TouchdownAnalysis.HasPreContactHistory(history, history.Count - 1))
                {
                    _episodeDecisionJournal?.Record(sampleTime, "analysis", "insufficient_live_history",
                        "First live contact after replay lacks 325 ms of continuous airborne history and five finite fit samples spanning 150 ms before the final 75 ms; discard this approach including bounces.",
                        new Dictionary<string, double> { ["contact_number"] = 1 });
                    CompleteEpisodeWithReason("replay_handoff_rejected", false);
                    _replayApproachDiscarded = true;
                    _armed = false;
                    _contactContinuity.Reset();
                    _previousOnGround = sample.OnGround;
                    return;
                }
            }
            _lastContactTime = sampleTime;
            _episodeDecisionJournal?.Record(sampleTime, "recorder", "contact_flag_transition",
                "Observed an airborne-to-ground flag transition; restart post-contact capture from this contact.",
                new Dictionary<string, double>
                {
                    ["post_contact_seconds"] = PostContactSeconds,
                    ["vertical_velocity_fps"] = sample.VelocityWorldYFps
                });
            SetStatus(RecorderState.Recording, "Recorder.ContactRollout");
        }

        _previousOnGround = sample.OnGround;

        if (rejectedContact && _episodeSamples != null)
        {
            _episodeDecisionJournal?.Record(sampleTime, "recorder", "physics_discontinuity_rejected",
                "Ground transition has no airborne evidence in the current active-physics segment; retain raw diagnostics, not a landing.");
            if (!_lastContactTime.HasValue)
            {
                CompleteEpisodeWithReason("rejected_contact_diagnostics", false);
                if (wasRecording) SetStatus(RecorderState.Connected, "Recorder.ConnectedGate");
                return;
            }
        }

        if (_episodeSamples != null && _episodeSamples.Count >= EpisodeMaximumSamples)
        {
            if (_lastContactTime.HasValue)
            {
                CompleteEpisodeWithReason("episode_sample_limit");
            }
            else
            {
                DiscardEpisode();
                _armed = true;
                SetStatus(RecorderState.Connected, "Recorder.ApproachRestarted");
            }
            return;
        }

        if (_episodeSamples != null && _lastContactTime.HasValue && sampleTime - _lastContactTime.Value >= PostContactSeconds)
        {
            CompleteEpisodeWithReason("post_contact_capture_complete");
        }
        else if (_episodeSamples != null && !_lastContactTime.HasValue &&
                 sample.AboveGroundLevelFeet >= GoAroundResetAglFeet && sample.VelocityWorldYFps > 0)
        {
            DiscardEpisode();
            _armed = true;
            SetStatus(RecorderState.Connected, "Recorder.GoAround");
        }
        else if (_episodeSamples != null && !_lastContactTime.HasValue && sampleTime - _episodeStartTime >= MaximumApproachSeconds)
        {
            DiscardEpisode();
            _armed = true;
            SetStatus(RecorderState.Connected, "Recorder.ApproachRestarted");
        }
    }

    private void UpdateAircraftGroundState(bool onGround)
    {
        if (_aircraftOnGround == onGround)
        {
            return;
        }

        _aircraftOnGround = onGround;
        AircraftGroundStateChanged?.Invoke(this, new AircraftGroundStateEventArgs(onGround));
    }

    private void StartEpisode(TelemetrySample trigger, bool contactTriggered, bool diagnosticOnly = false)
    {
        if (!diagnosticOnly) FlushRejectedContacts();
        _activeEpisodeId = checked(_nextEpisodeId + 1);
        _nextEpisodeId = _activeEpisodeId;
        _activePersistentId = StableRuntimeIdentity.Episode(_captureSessionId, _activeEpisodeId);
        _episodeSimulator = _simulator; _episodeAircraftTitle = _aircraftTitle; _episodeAircraftType = _aircraftType; _episodeAircraftModel = _aircraftModel;
        _episodeControlSources = _controlInputSources().ToArray();
        _episodeSamples = new List<TelemetrySample>(_preRoll.Count + 4096);
        foreach (var sample in _preRoll)
        {
            _episodeSamples.Add(sample);
        }

        _episodeStartTime = TimeOf(trigger);
        _lastContactTime = null;
        _armed = false;
        _episodeDecisionJournal = new TelemetryDecisionJournal(_pendingLifecycleJournal.Snapshot());
        _pendingLifecycleJournal = new TelemetryDecisionJournal();
        _episodeCoalescedFrameCount = 0;
        _episodeNonMotionFrameCount = trigger.MotionSimulation ? 0 : 1;
        _episodeDecisionJournal.Record(_episodeStartTime, "recorder",
            diagnosticOnly ? "capture_started_for_rejected_contact" : contactTriggered ? "capture_started_at_contact" : "capture_started_at_approach_gate",
            diagnosticOnly ? "Retain pre-roll and the rejected ground transition for diagnostics only."
                : contactTriggered ? "Started capture from an observed contact, retaining the available pre-roll."
                : "Started capture on the descending, airborne approach gate, retaining the available pre-roll.",
            new Dictionary<string, double>
            {
                ["pre_roll_sample_count"] = _episodeSamples.Count,
                ["agl_feet"] = trigger.AboveGroundLevelFeet,
                ["vertical_velocity_fps"] = trigger.VelocityWorldYFps,
                ["gate_agl_feet"] = CaptureStartAglFeet,
                ["gate_descent_fps"] = CaptureStartDescentFps
            });
        if (diagnosticOnly) return;
        _facilities.BeginEpisode(_activeEpisodeId);
        _facilities.SetDecisionContext(_episodeDecisionJournal, _episodeStartTime);
        _facilities.RequestPreliminary(trigger, _episodeSamples);
        EpisodeStarted?.Invoke(this, new LandingEpisodeStartedEventArgs(_activeEpisodeId));
        MetadataRequested?.Invoke(this, EventArgs.Empty);

        SetStatus(
            RecorderState.Recording,
            contactTriggered ? "Recorder.LateStart" : "Recorder.BelowGate");
    }

    private void AppendEpisodeSample(TelemetrySample sample)
    {
        if (_episodeSamples == null)
        {
            return;
        }

        if (!sample.MotionSimulation && _episodeNonMotionFrameCount < int.MaxValue) _episodeNonMotionFrameCount++;

        // Keep both ends of a confirmed pause, irrespective of stale motion flag
        // chatter or a clock which continues running for hours in the menu.
        if (sample.SimulatorState == TelemetrySimulatorState.Paused && _episodeSamples.Count >= 2 &&
            _episodeSamples[_episodeSamples.Count - 1].SimulatorState == TelemetrySimulatorState.Paused &&
            _episodeSamples[_episodeSamples.Count - 2].SimulatorState == TelemetrySimulatorState.Paused)
        {
            _episodeSamples[_episodeSamples.Count - 1] = sample;
            if (_episodeCoalescedFrameCount < int.MaxValue) _episodeCoalescedFrameCount++;
            return;
        }

        if (sample.SimulatorState != TelemetrySimulatorState.Paused && _episodeSamples.Count > 0 &&
            _episodeSamples[_episodeSamples.Count - 1].MotionSimulation == sample.MotionSimulation &&
            _episodeSamples[_episodeSamples.Count - 1].SimulatorState == sample.SimulatorState &&
            _episodeSamples[_episodeSamples.Count - 1].FlightSegment == sample.FlightSegment &&
            Math.Abs(TimeOf(_episodeSamples[_episodeSamples.Count - 1]) - TimeOf(sample)) <= 0.000000001)
        {
            // SIM_FRAME can continue while the simulator clock is paused. Keep the
            // newest state within each physics state, preserving pause/resume boundaries.
            _episodeSamples[_episodeSamples.Count - 1] = sample;
            if (_episodeCoalescedFrameCount < int.MaxValue) _episodeCoalescedFrameCount++;
            return;
        }

        _episodeSamples.Add(sample);
    }

    internal void CompleteEpisode(bool refreshAirportFacilities = true)
    {
        var samples = _episodeSamples;
        var episodeId = _activeEpisodeId;
        var persistentId = _activePersistentId;
        var decisionJournal = _episodeDecisionJournal;
        var diagnosticOnly = !_lastContactTime.HasValue;
        _episodeSamples = null;
        _episodeDecisionJournal = null;
        _activeEpisodeId = 0;
        _activePersistentId = Guid.Empty;
        _lastContactTime = null;
        _armed = true;
        _preRoll.Clear();
        if (samples != null) PublishCaptureStatus();
        if (samples == null || samples.Count == 0)
        {
            return;
        }

        decisionJournal?.Record(TimeOf(samples[samples.Count - 1]), "recorder", "capture_completed",
            "Completed capture; same-time deliveries kept their latest state without erasing physics boundaries. Non-motion observations are retained for diagnostics.",
            new Dictionary<string, double>
            {
                ["retained_sample_count"] = samples.Count,
                ["same_time_frames_coalesced"] = _episodeCoalescedFrameCount,
                ["non_motion_frames_observed_after_start"] = _episodeNonMotionFrameCount
            });

        // Keep this approach's snapshot, but never inherit another episode's
        // scenery when a late-start or salvaged capture completes.
        if (!diagnosticOnly)
        {
            if (_facilities.EpisodeId != episodeId) _facilities.BeginEpisode(episodeId);
            _facilities.SetDecisionContext(decisionJournal, TimeOf(samples[samples.Count - 1]));
            if (!refreshAirportFacilities || !_facilities.CanQuery)
                _facilities.CompleteWithoutRefresh("Capture ended without a final scenery lookup; retain only this approach's preliminary facilities.");
        }
        var completedEpisode = new LandingEpisodeEventArgs(
                episodeId,
                samples.ToArray(),
                _episodeSimulator,
                _episodeAircraftTitle,
                _episodeAircraftType,
                _episodeAircraftModel,
                _facilities.CachedAirports,
                _episodeControlSources,
                decisionJournal: decisionJournal,
                runwayFacilitiesTask: diagnosticOnly ? null : _facilities.RunwayTask,
                isDiagnosticOnly: diagnosticOnly,
                facilitiesTask: diagnosticOnly ? null : _facilities.SnapshotTask,
                initialFacilities: diagnosticOnly ? null : _facilities.CurrentSnapshot(), persistentId: persistentId);
        foreach (var completed in _rejectedContactBatcher.Accept(completedEpisode, _utcNow()))
            EpisodeCompleted?.Invoke(this, completed);

        if (!diagnosticOnly && refreshAirportFacilities && _facilities.CanQuery) _facilities.RequestFinal(samples);
    }

    internal void DiscardEpisode()
    {
        if (_activeEpisodeId != 0 && _activeEpisodeId == _facilities.EpisodeId)
        {
            _facilities.CompleteWithoutRefresh("Approach discarded; cancel its native requests before another flight can reply.");
            _facilities.DiscardEpisode(_activeEpisodeId);
        }
        _episodeSamples = null;
        _episodeDecisionJournal = null;
        _activeEpisodeId = 0;
        _activePersistentId = Guid.Empty;
        _lastContactTime = null;
    }

    internal void CompleteEpisodeWithReason(string reason, bool refreshAirportFacilities = true)
    {
        _episodeDecisionJournal?.Record(_episodeSamples != null && _episodeSamples.Count > 0
            ? TimeOf(_episodeSamples[_episodeSamples.Count - 1]) : _lastSimulationTime, "recorder", reason,
            "This condition ended the capture; existing samples are passed to landing analysis.");
        CompleteEpisode(refreshAirportFacilities);
    }

    private void AddToPreRoll(TelemetrySample sample)
    {
        // SDK-classified pauses do not consume approach history. Legacy captures
        // retain their original monotonic-receipt policy.
        _preRoll.Add(sample, sample.SimulatorState == TelemetrySimulatorState.Legacy
            ? _hostSeconds() : TimeOf(sample));
    }

    private static bool ShouldStartEpisode(TelemetrySample sample)
    {
        return sample.MotionSimulation &&
               !sample.OnGround &&
               sample.AboveGroundLevelFeet >= -50 &&
               sample.AboveGroundLevelFeet <= CaptureStartAglFeet &&
               sample.VelocityWorldYFps <= CaptureStartDescentFps;
    }

    private bool HasActiveAirbornePreRoll(TelemetrySample trigger)
    {
        foreach (var sample in _preRoll)
        {
            if (sample.FlightSegment == trigger.FlightSegment &&
                sample.MotionSimulation && !sample.OnGround &&
                (sample.SimulatorState == TelemetrySimulatorState.Legacy ||
                 sample.SimulatorState == TelemetrySimulatorState.Running))
                return true;
        }
        return false;
    }

    internal void ResetFlightDetection()
    {
        FlushRejectedContacts();
        _axisElevatorResetHostSeconds = _hostSeconds();
        _lastKinematicFrame = null;
        _preRoll.Clear();
        _contactContinuity.Reset();
        _previousOnGround = true;
        _armed = true;
        _lastSimulationTime = double.NaN;
    }

    internal void FlushRejectedContacts()
    {
        var diagnostic = _rejectedContactBatcher.Flush();
        if (diagnostic != null) EpisodeCompleted?.Invoke(this, diagnostic);
    }
}
