using System;
using System.Collections.Generic;
using LandingStats.Core;

namespace LandingStats.App.Telemetry;

internal sealed partial class LandingEpisodeCoordinator
{
    private ReplaySourceAssessor _replaySourceAssessor = new ReplaySourceAssessor();
    private ReplaySourceEvidence? _lastReplayEvidence;
    private bool _externalReplayActive;
    private bool _resumeCaptureBelowGate;
    private readonly List<TelemetrySample> _pendingReplayFrames = new List<TelemetrySample>();
    private int _pendingReplayCoalesced;

    private bool ProcessReplaySource(TelemetrySample sample)
    {
        var evidence = sample.SourceEvidence;
        var assessment = _replaySourceAssessor.Observe(evidence);
        _lastReplayEvidence = evidence;
        if (assessment.Kind == ReplaySourceKind.ConfirmedSdkReplay && (_flightBlock & FlightBlock.Replay) == 0)
            HandleFlowEvent(SimulatorFlowEvent.REPLAY_START);
        else if (evidence?.SdkReplay == SdkReplayState.Inactive &&
            evidence.Observation.Availability != SourceObservationAvailability.Gap && (_flightBlock & FlightBlock.Replay) != 0)
            HandleFlowEvent(SimulatorFlowEvent.REPLAY_END);

        if ((_flightBlock & FlightBlock.Replay) != 0)
        {
            ClearPendingReplayFrames();
            return true;
        }
        if (assessment.Kind == ReplaySourceKind.ExternallyDrivenPose)
        {
            ClearPendingReplayFrames();
            if (!_externalReplayActive)
            {
                _externalReplayActive = _replayActive = true;
                _resumeCaptureBelowGate = false;
                // Pending frames have never entered the episode. Keep a real
                // contact's available rollout; discard an unfinished approach.
                EndFlightSegment("external_replay_started");
                _armed = false;
                PublishCaptureStatus();
            }
            return true;
        }
        if (_externalReplayActive)
        {
            // A missing, old, partial or slewing observation is not evidence
            // that the replay ended. A fresh release of all three axes is.
            if (!HasFreshReleasedSource(evidence)) return true;
            _externalReplayActive = _replayActive = false;
            EndFlightSegment("external_replay_finished");
            _resumeCaptureBelowGate = true;
            MetadataRequested?.Invoke(this, EventArgs.Empty);
            PublishCaptureStatus();
        }

        if (assessment.Reason == ReplaySourceAssessmentReason.AwaitingMovement ||
            (_pendingReplayFrames.Count != 0 && !HasFreshReleasedSource(evidence)))
        {
            // Usually one USER interval (250 ms). A stationary frozen aircraft
            // can remain here indefinitely: keep bounded endpoints, never turn
            // the static freeze itself into replay proof or a ground contact.
            if (_pendingReplayFrames.Count >= PreRollMaximumSamples)
            {
                _pendingReplayCoalesced = _pendingReplayCoalesced > int.MaxValue - _pendingReplayFrames.Count
                    ? int.MaxValue : _pendingReplayCoalesced + _pendingReplayFrames.Count - 2;
                _pendingReplayFrames.RemoveRange(1, _pendingReplayFrames.Count - 2);
            }
            _pendingReplayFrames.Add(sample);
            if (_pendingReplayFrames.Count == 1) PublishCaptureStatus();
            return true;
        }
        if (_pendingReplayFrames.Count != 0)
        {
            // No movement while frozen: resume the ordinary path without
            // inventing a replay transition. Replay-positive frames are never
            // flushed through this branch.
            var pending = _pendingReplayFrames.ToArray();
            var coalesced = _pendingReplayCoalesced;
            ClearPendingReplayFrames();
            if (coalesced != 0) DecisionJournal.Record(_lastSimulationTime, "recorder", "stationary_freeze_coalesced",
                "Retained endpoints of a long stationary freeze while awaiting source movement.",
                new Dictionary<string, double> { ["coalesced_frames"] = coalesced });
            foreach (var frame in pending) ProcessLiveSample(frame);
            PublishCaptureStatus();
        }
        return false;
    }

    private static bool HasFreshReleasedSource(ReplaySourceEvidence? evidence) => evidence != null &&
        evidence.SdkReplay != SdkReplayState.Active && evidence.AgeSeconds >= 0 &&
        evidence.AgeSeconds <= ReplaySourceAssessor.MaximumObservationAgeSeconds &&
        evidence.Observation.Availability == SourceObservationAvailability.Available &&
        evidence.Observation.FreezeMask == 0 && evidence.Observation.Slew == 0 &&
        IsFinite(evidence.Observation.Latitude) && IsFinite(evidence.Observation.Longitude) &&
        IsFinite(evidence.Observation.AltitudeFeet) && IsFinite(evidence.Observation.Pitch) &&
        IsFinite(evidence.Observation.Bank) && IsFinite(evidence.Observation.Heading);

    private void ClearPendingReplayFrames()
    {
        _pendingReplayFrames.Clear();
        _pendingReplayCoalesced = 0;
    }

    private void ResetReplayObservation()
    {
        ClearPendingReplayFrames();
        _lastReplayEvidence = null;
        _replaySourceAssessor = new ReplaySourceAssessor();
    }
}
